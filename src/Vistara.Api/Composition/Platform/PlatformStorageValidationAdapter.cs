using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Api.Features.Admin;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Runs a candidate storage validation. The active storage configuration is
/// only read, never written, and the deployment's existing trusted host list is
/// the single place that can widen the network policy. Network policy is
/// enforced before any provider client is constructed, so a rejected candidate
/// never causes a submitted credential to be used.
/// </summary>
internal sealed class PlatformStorageValidationAdapter(
    IStorageValidationClientFactory factory,
    IOptions<MediaOptions> media) : IStorageValidationPort
{
    private readonly MediaOptions _media = media is null
        ? throw new ArgumentNullException(nameof(media))
        : media.Value;

    public async ValueTask<StorageValidationOutcome> ValidateAsync(
        StorageValidationCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (candidate.Endpoint is { } endpoint && !IsPermitted(endpoint))
        {
            return StorageValidationOutcome.Rejected(
                StorageValidationDetails.RejectedMessage,
                StorageValidationDetails.EndpointRejected);
        }

        if (!HasUsableCredential(candidate))
        {
            return StorageValidationOutcome.Rejected(
                StorageValidationDetails.RejectedMessage,
                StorageValidationDetails.CredentialMissing);
        }

        IStorageValidationClient client;
        try
        {
            client = await factory.CreateAsync(candidate, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Client construction failures carry provider text, so the caller
            // only ever learns that the credential was not usable.
            return StorageValidationOutcome.Rejected(
                StorageValidationDetails.RejectedMessage,
                StorageValidationDetails.CredentialRejected);
        }

        // Disposal is unconditional so a timeout or caller cancellation still
        // tears down the one-shot client and the credential it holds.
        await using (client.ConfigureAwait(false))
        {
            try
            {
                return await client.ProbeAsync(
                    StorageProbeNaming.CreateKey(),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                return StorageValidationOutcome.Rejected(
                    StorageValidationDetails.RejectedMessage,
                    StorageValidationDetails.Unreachable);
            }
        }
    }

    /// <summary>
    /// An anonymous S3 candidate is only accepted when the operator already
    /// trusts the endpoint host, which is how an emulator is configured.
    /// </summary>
    private bool HasUsableCredential(StorageValidationCandidate candidate) =>
        candidate.Kind != StorageCandidateKind.S3 ||
        candidate.S3Credential != S3CredentialKind.Anonymous ||
        (candidate.Endpoint is { } endpoint &&
            IsExplicitlyTrusted(endpoint.Host) &&
            (_media.Storage.S3.AllowInsecureHttp || _media.Storage.Azure.EmulatorMode));

    private bool IsPermitted(Uri endpoint)
    {
        bool trusted = IsExplicitlyTrusted(endpoint.Host);
        if (endpoint.Scheme == Uri.UriSchemeHttp &&
            !(trusted && (_media.Storage.S3.AllowInsecureHttp ||
                _media.Storage.Azure.EmulatorMode)))
        {
            return false;
        }

        if (trusted)
        {
            return true;
        }

        if (IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out IPAddress? literal))
        {
            return !StorageValidationEndpoint.IsBlockedAddress(literal);
        }

        IPAddress[] resolved;
        try
        {
            resolved = Dns.GetHostAddresses(endpoint.Host);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return resolved.Length != 0 &&
            !resolved.Any(StorageValidationEndpoint.IsBlockedAddress);
    }

    /// <summary>
    /// Only the operator's existing trusted endpoint list can widen the
    /// policy; a request can never nominate its own exemption.
    /// </summary>
    private bool IsExplicitlyTrusted(string host) =>
        _media.Storage.S3.AllowedEndpointHosts.Any(allowed =>
            string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Builds the shipped one-shot provider clients. This is the only place a
/// submitted credential is revealed, and the value goes straight into the
/// provider SDK without being stored, cached, or copied anywhere else.
/// </summary>
internal sealed class PlatformStorageValidationClientFactory
    : IStorageValidationClientFactory
{
    public ValueTask<IStorageValidationClient> CreateAsync(
        StorageValidationCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        IStorageValidationClient client = candidate.Kind switch
        {
            StorageCandidateKind.Filesystem =>
                new FilesystemValidationClient(candidate.RootPath!),
            StorageCandidateKind.AzureBlob => CreateAzure(candidate),
            _ => CreateS3(candidate),
        };
        return ValueTask.FromResult(client);
    }

    private static AzureValidationClient CreateAzure(
        StorageValidationCandidate candidate)
    {
        var containerUri = new Uri(
            candidate.Endpoint!,
            $"/{candidate.Container}");
        BlobContainerClient container = candidate.AzureCredential switch
        {
            AzureCredentialKind.ManagedIdentity =>
                new BlobContainerClient(containerUri, new DefaultAzureCredential()),
            AzureCredentialKind.AccountKey =>
                new BlobContainerClient(
                    containerUri,
                    new StorageSharedKeyCredential(
                        candidate.AccountName!,
                        candidate.AccountKey!.Reveal())),
            _ => new BlobContainerClient(
                new Uri(
                    $"{containerUri}?{candidate.SasToken!.Reveal().TrimStart('?')}")),
        };
        return new AzureValidationClient(container);
    }

    private static S3ValidationClient CreateS3(
        StorageValidationCandidate candidate)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = candidate.Endpoint!.ToString(),
            ForcePathStyle = candidate.ForcePathStyle,
            AuthenticationRegion = candidate.Region,
            UseHttp = candidate.Endpoint.Scheme == Uri.UriSchemeHttp,
            MaxErrorRetry = 0,
        };
        AWSCredentials credentials = candidate switch
        {
            { S3Credential: S3CredentialKind.AccessKey, SessionToken: not null } =>
                new SessionAWSCredentials(
                    candidate.AccessKeyId!.Reveal(),
                    candidate.SecretAccessKey!.Reveal(),
                    candidate.SessionToken.Reveal()),
            { S3Credential: S3CredentialKind.AccessKey } =>
                new BasicAWSCredentials(
                    candidate.AccessKeyId!.Reveal(),
                    candidate.SecretAccessKey!.Reveal()),
            _ => new AnonymousAWSCredentials(),
        };
        return new S3ValidationClient(
            new AmazonS3Client(credentials, config),
            candidate.Container!);
    }
}

/// <summary>Checks a candidate directory without touching existing files.</summary>
internal sealed class FilesystemValidationClient(string rootPath)
    : IStorageValidationClient
{
    public async ValueTask<StorageValidationOutcome> ProbeAsync(
        string probeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(probeKey);
        var recorder = new StorageProbeRecorder();
        if (!Directory.Exists(rootPath))
        {
            return recorder.Fail(
                StorageCheckId.Reachable,
                StorageValidationDetails.PathMissing,
                StorageValidationDetails.RejectedMessage);
        }

        recorder.Pass(StorageCheckId.Reachable);
        recorder.Skip(StorageCheckId.Authenticated, "A directory needs no credential.");

        try
        {
            _ = Directory.EnumerateFileSystemEntries(rootPath).Take(1).Count();
            recorder.Pass(StorageCheckId.Read);
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            return recorder.Fail(
                StorageCheckId.Read,
                StorageValidationDetails.ListDenied,
                StorageValidationDetails.RejectedMessage);
        }

        string probe = Path.Combine(
            rootPath,
            probeKey.Replace('/', '-').Replace('\\', '-'));
        try
        {
            await File.WriteAllBytesAsync(probe, [], cancellationToken);
            recorder.Pass(StorageCheckId.Write);
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            return recorder.Fail(
                StorageCheckId.Write,
                StorageValidationDetails.WriteDenied,
                StorageValidationDetails.RejectedMessage);
        }

        try
        {
            File.Delete(probe);
            recorder.Pass(StorageCheckId.Delete);
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            return recorder.Fail(
                StorageCheckId.Delete,
                StorageValidationDetails.DeleteDenied,
                StorageValidationDetails.RejectedMessage);
        }

        return recorder.Complete(StorageValidationDetails.ValidMessage);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Exercises an Azure Blob container with the submitted credential: list under
/// the reserved probe prefix, then write and delete one empty probe blob.
/// </summary>
internal sealed class AzureValidationClient(BlobContainerClient container)
    : IStorageValidationClient
{
    public async ValueTask<StorageValidationOutcome> ProbeAsync(
        string probeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(probeKey);
        var recorder = new StorageProbeRecorder();
        try
        {
            await foreach (BlobItem _ in container
                .GetBlobsAsync(
                    BlobTraits.None,
                    BlobStates.None,
                    StorageProbeNaming.Prefix,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                break;
            }

            recorder.Pass(StorageCheckId.Reachable);
            recorder.Pass(StorageCheckId.Authenticated);
            recorder.Pass(StorageCheckId.Read);
        }
        catch (AuthenticationFailedException)
        {
            recorder.Pass(StorageCheckId.Reachable);
            return recorder.Fail(
                StorageCheckId.Authenticated,
                StorageValidationDetails.CredentialRejected,
                StorageValidationDetails.RejectedMessage);
        }
        catch (RequestFailedException failure)
        {
            if (failure.Status is 401 or 403)
            {
                recorder.Pass(StorageCheckId.Reachable);
                return recorder.Fail(
                    StorageCheckId.Authenticated,
                    StorageValidationDetails.CredentialRejected,
                    StorageValidationDetails.RejectedMessage);
            }

            if (failure.Status is >= 400 and < 500)
            {
                recorder.Pass(StorageCheckId.Reachable);
                recorder.Pass(StorageCheckId.Authenticated);
                return recorder.Fail(
                    StorageCheckId.Read,
                    StorageValidationDetails.ListDenied,
                    StorageValidationDetails.RejectedMessage);
            }

            return recorder.Fail(
                StorageCheckId.Reachable,
                StorageValidationDetails.Unreachable,
                StorageValidationDetails.RejectedMessage);
        }

        BlobClient probe = container.GetBlobClient(probeKey);
        try
        {
            _ = await probe.UploadAsync(
                Stream.Null,
                overwrite: false,
                cancellationToken)
                .ConfigureAwait(false);
            recorder.Pass(StorageCheckId.Write);
        }
        catch (RequestFailedException)
        {
            return recorder.Fail(
                StorageCheckId.Write,
                StorageValidationDetails.WriteDenied,
                StorageValidationDetails.RejectedMessage);
        }

        try
        {
            _ = await probe.DeleteIfExistsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            recorder.Pass(StorageCheckId.Delete);
        }
        catch (RequestFailedException)
        {
            return recorder.Fail(
                StorageCheckId.Delete,
                StorageValidationDetails.DeleteDenied,
                StorageValidationDetails.RejectedMessage);
        }

        return recorder.Complete(StorageValidationDetails.ValidMessage);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Exercises an S3-compatible bucket with the submitted credential: list under
/// the reserved probe prefix, then write and delete one empty probe object.
/// </summary>
internal sealed class S3ValidationClient(IAmazonS3 client, string bucket)
    : IStorageValidationClient
{
    public async ValueTask<StorageValidationOutcome> ProbeAsync(
        string probeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(probeKey);
        var recorder = new StorageProbeRecorder();
        try
        {
            _ = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = StorageProbeNaming.Prefix,
                    MaxKeys = 1,
                },
                cancellationToken)
                .ConfigureAwait(false);
            recorder.Pass(StorageCheckId.Reachable);
            recorder.Pass(StorageCheckId.Authenticated);
            recorder.Pass(StorageCheckId.Read);
        }
        catch (AmazonS3Exception failure)
        {
            if (failure.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden)
            {
                recorder.Pass(StorageCheckId.Reachable);
                return recorder.Fail(
                    StorageCheckId.Authenticated,
                    StorageValidationDetails.CredentialRejected,
                    StorageValidationDetails.RejectedMessage);
            }

            if ((int)failure.StatusCode is >= 400 and < 500)
            {
                recorder.Pass(StorageCheckId.Reachable);
                recorder.Pass(StorageCheckId.Authenticated);
                return recorder.Fail(
                    StorageCheckId.Read,
                    StorageValidationDetails.ListDenied,
                    StorageValidationDetails.RejectedMessage);
            }

            return recorder.Fail(
                StorageCheckId.Reachable,
                StorageValidationDetails.Unreachable,
                StorageValidationDetails.RejectedMessage);
        }

        try
        {
            _ = await client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = probeKey,
                    ContentBody = string.Empty,
                },
                cancellationToken)
                .ConfigureAwait(false);
            recorder.Pass(StorageCheckId.Write);
        }
        catch (AmazonS3Exception)
        {
            return recorder.Fail(
                StorageCheckId.Write,
                StorageValidationDetails.WriteDenied,
                StorageValidationDetails.RejectedMessage);
        }

        try
        {
            _ = await client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucket, Key = probeKey },
                cancellationToken)
                .ConfigureAwait(false);
            recorder.Pass(StorageCheckId.Delete);
        }
        catch (AmazonS3Exception)
        {
            return recorder.Fail(
                StorageCheckId.Delete,
                StorageValidationDetails.DeleteDenied,
                StorageValidationDetails.RejectedMessage);
        }

        return recorder.Complete(StorageValidationDetails.ValidMessage);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
