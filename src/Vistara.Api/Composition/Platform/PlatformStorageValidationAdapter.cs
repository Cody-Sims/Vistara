using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Api.Features.Admin;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Runs a candidate storage probe. The active storage configuration is only
/// read, never written, and the deployment's existing trusted host list is the
/// single place that can widen the network policy.
/// </summary>
internal sealed class PlatformStorageValidationAdapter(
    IStorageValidationProbe probe,
    IOptions<MediaOptions> media) : IStorageValidationPort
{
    private readonly MediaOptions _media = media is null
        ? throw new ArgumentNullException(nameof(media))
        : media.Value;

    public async ValueTask<StorageValidationOutcome> ValidateAsync(
        StorageValidationTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (target.Endpoint is { } endpoint && !IsPermitted(endpoint, out string code))
        {
            return new StorageValidationOutcome(
                false,
                code,
                "The endpoint is not an allowed validation target.");
        }

        try
        {
            return await probe.ProbeAsync(target, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Provider text routinely carries endpoints and credentials, so the
            // caller only ever learns that the probe failed.
            return StorageValidationOutcome.Unreachable;
        }
    }

    private bool IsPermitted(Uri endpoint, out string code)
    {
        code = "storage.endpoint_rejected";
        bool trusted = IsExplicitlyTrusted(endpoint.Host);
        if (endpoint.Scheme == Uri.UriSchemeHttp &&
            !(trusted && (_media.Storage.S3.AllowInsecureHttp ||
                _media.Storage.Azure.EmulatorMode)))
        {
            code = "storage.insecure_endpoint";
            return false;
        }

        if (trusted)
        {
            return true;
        }

        if (IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out IPAddress? literal))
        {
            if (StorageValidationEndpoint.IsBlockedAddress(literal))
            {
                code = "storage.blocked_endpoint";
                return false;
            }

            return true;
        }

        IPAddress[] resolved;
        try
        {
            resolved = Dns.GetHostAddresses(endpoint.Host);
        }
        catch (SocketException)
        {
            code = "storage.unresolvable_endpoint";
            return false;
        }
        catch (ArgumentException)
        {
            code = "storage.unresolvable_endpoint";
            return false;
        }

        if (resolved.Length == 0)
        {
            code = "storage.unresolvable_endpoint";
            return false;
        }

        if (resolved.Any(StorageValidationEndpoint.IsBlockedAddress))
        {
            code = "storage.blocked_endpoint";
            return false;
        }

        return true;
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
/// The shipped probe. A filesystem candidate is checked directly; a remote
/// candidate is checked with one bounded request through the shared client.
/// No credential is ever accepted, so none can be sent, stored, or logged.
/// </summary>
internal sealed class PlatformStorageValidationProbe(IHttpClientFactory clients)
    : IStorageValidationProbe
{
    internal const string HttpClientName = "Vistara.StorageValidation";

    public async ValueTask<StorageValidationOutcome> ProbeAsync(
        StorageValidationTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == StorageCandidateKind.Filesystem)
        {
            return ProbeFilesystem(target.RootPath!);
        }

        HttpClient client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Head, target.Endpoint);
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                StorageValidationOutcome.Denied,
            _ when (int)response.StatusCode >= 500 =>
                StorageValidationOutcome.Unreachable,
            _ => StorageValidationOutcome.Reached,
        };
    }

    private static StorageValidationOutcome ProbeFilesystem(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return new StorageValidationOutcome(
                false,
                "storage.path_missing",
                "The directory does not exist.");
        }

        string probe = Path.Combine(
            rootPath,
            $".vistara-validate-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            return StorageValidationOutcome.Reached;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            return new StorageValidationOutcome(
                false,
                "storage.path_not_writable",
                "The directory is not writable.");
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception failure) when (
                failure is IOException or UnauthorizedAccessException)
            {
                // The probe file is best effort; nothing else depends on it.
            }
        }
    }
}
