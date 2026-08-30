using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;

namespace Vistara.Storage.Azure;

internal sealed class AzureSdkBlobClient : AzureBlobClientBase
{
    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;
    private readonly string _accountName;
    private readonly AzureBlobSasMode _sasMode;

    public AzureSdkBlobClient(
        Uri serviceUri,
        string accountName,
        string containerName,
        TokenCredential credential,
        bool emulatorMode)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(credential);
        _service = new BlobServiceClient(serviceUri, credential);
        _container = _service.GetBlobContainerClient(containerName);
        _accountName = accountName;
        _sasMode = AzureBlobSasMode.UserDelegation;
    }

    public AzureSdkBlobClient(
        string connectionString,
        Uri serviceUri,
        string accountName,
        string containerName,
        bool emulatorMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(serviceUri);
        _service = new BlobServiceClient(connectionString);
        if (!SameEndpoint(_service.Uri, serviceUri))
        {
            throw new ArgumentException(
                "The Azure Blob connection string endpoint does not match the configured service endpoint.",
                nameof(connectionString));
        }

        _container = new BlobContainerClient(connectionString, containerName);
        if (!_container.GetBlobClient("vistara-credential-check").CanGenerateSasUri)
        {
            throw new ArgumentException(
                "The Azure Blob connection string must contain an account shared key.",
                nameof(connectionString));
        }

        _accountName = accountName;
        _sasMode = AzureBlobSasMode.SharedKey;
    }

    public override async ValueTask<AzureBlobObject?> HeadAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken)
    {
        BlobClient client = _container.GetBlobClient(key);
        try
        {
            Response<BlobProperties> response = await client.GetPropertiesAsync(
                ToConditions(conditions),
                cancellationToken);
            return ToObject(key, response.Value);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async ValueTask<AzureBlobDownload> DownloadAsync(
        string key,
        AzureBlobRange? range,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken)
    {
        BlobClient client = _container.GetBlobClient(key);
        try
        {
            Response<BlobProperties> propertiesResponse =
                await client.GetPropertiesAsync(
                    ToConditions(conditions),
                    cancellationToken);
            BlobProperties properties = propertiesResponse.Value;
            BlobRequestConditions downloadConditions = ToConditions(conditions);
            if (conditions.IfMatch is null && !conditions.RequireMissing)
            {
                downloadConditions.IfMatch = properties.ETag;
            }

            Response<BlobDownloadStreamingResult> response =
                await client.DownloadStreamingAsync(
                    new BlobDownloadOptions
                    {
                        Range = range is null
                            ? default
                            : new HttpRange(range.Offset, range.Length),
                        Conditions = downloadConditions,
                    },
                    cancellationToken);
            return new AzureBlobDownload(
                response.Value.Content,
                ToObject(key, properties),
                range,
                properties.ContentLength);
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async ValueTask StageBlockAsync(
        string key,
        string blockId,
        Stream content,
        byte[] contentMd5,
        CancellationToken cancellationToken)
    {
        BlockBlobClient client = _container.GetBlockBlobClient(key);
        try
        {
            await client.StageBlockAsync(
                blockId,
                content,
                contentMd5,
                conditions: null,
                progressHandler: null,
                cancellationToken);
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async ValueTask<AzureBlobBlockList> GetBlockListAsync(
        string key,
        CancellationToken cancellationToken)
    {
        BlockBlobClient client = _container.GetBlockBlobClient(key);
        try
        {
            Response<BlockList> response = await client.GetBlockListAsync(
                BlockListTypes.All,
                cancellationToken: cancellationToken);
            return new AzureBlobBlockList(
                response.Value.CommittedBlocks
                    .Select(block => new AzureBlobBlock(
                        block.Name,
                        block.Size))
                    .ToArray(),
                response.Value.UncommittedBlocks
                    .Select(block => new AzureBlobBlock(
                        block.Name,
                        block.Size))
                    .ToArray());
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async ValueTask<AzureBlobObject> CommitBlockListAsync(
        string key,
        IReadOnlyList<string> blockIds,
        AzureBlobCommitOptions options,
        CancellationToken cancellationToken)
    {
        BlockBlobClient client = _container.GetBlockBlobClient(key);
        try
        {
            Response<BlobContentInfo> committed = await client.CommitBlockListAsync(
                blockIds,
                new CommitBlockListOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = options.ContentType,
                        ContentHash = options.ContentMd5.Length == 0
                            ? null
                            : options.ContentMd5,
                    },
                    Metadata = new Dictionary<string, string>(
                        options.Metadata,
                        StringComparer.Ordinal),
                    Conditions = ToConditions(options.Conditions),
                },
                cancellationToken);
            Response<BlobProperties> properties = await client.GetPropertiesAsync(
                new BlobRequestConditions
                {
                    IfMatch = committed.Value.ETag,
                },
                cancellationToken);
            return ToObject(key, properties.Value);
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async ValueTask<AzureBlobCopyState> StartCopyAsync(
        string sourceKey,
        string destinationKey,
        AzureBlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        BlobClient source = _container.GetBlobClient(sourceKey);
        BlobClient destination = _container.GetBlobClient(destinationKey);
        try
        {
            await destination.StartCopyFromUriAsync(
                source.Uri,
                new BlobCopyFromUriOptions
                {
                    SourceConditions = ToConditions(options.SourceConditions),
                    DestinationConditions = ToConditions(options.DestinationConditions),
                    Metadata = options.ReplacementMetadata is null
                        ? null
                        : new Dictionary<string, string>(
                            options.ReplacementMetadata,
                            StringComparer.Ordinal),
                },
                cancellationToken);
            return await ReadCopyStateAsync(
                destinationKey,
                destination,
                cancellationToken);
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async ValueTask<AzureBlobCopyState> GetCopyStateAsync(
        string destinationKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCopyStateAsync(
                destinationKey,
                _container.GetBlobClient(destinationKey),
                cancellationToken);
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async ValueTask<AzureBlobDeleteResult> DeleteAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken)
    {
        BlobClient client = _container.GetBlobClient(key);
        try
        {
            Response<BlobProperties> properties;
            try
            {
                properties = await client.GetPropertiesAsync(
                    ToConditions(conditions),
                    cancellationToken);
            }
            catch (RequestFailedException error) when (
                error.Status == 404 && conditions.IfMatch is null)
            {
                return new AzureBlobDeleteResult(false, null);
            }

            BlobRequestConditions deleteConditions = ToConditions(conditions);
            if (conditions.IfMatch is null && !conditions.RequireMissing)
            {
                deleteConditions.IfMatch = properties.Value.ETag;
            }

            Response<bool> deleted = await client.DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                deleteConditions,
                cancellationToken);
            return new AzureBlobDeleteResult(
                deleted.Value,
                deleted.Value ? ToObject(key, properties.Value) : null);
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override async IAsyncEnumerable<AzureBlobObject> ListAsync(
        string? prefix,
        bool includeVersions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GetBlobsOptions options = new()
        {
            Prefix = prefix,
            Traits = BlobTraits.Metadata,
            States = includeVersions ? BlobStates.Version : BlobStates.None,
        };
        AsyncPageable<BlobItem> entries = _container.GetBlobsAsync(
            options,
            cancellationToken);
        IAsyncEnumerator<BlobItem> enumerator =
            entries.GetAsyncEnumerator(cancellationToken);
        await using (enumerator)
        {
            while (true)
            {
                BlobItem item;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        yield break;
                    }

                    item = enumerator.Current;
                }
                catch (RequestFailedException error)
                {
                    throw Translate(error);
                }

                BlobItemProperties properties = item.Properties;
                string entityTag = properties.ETag?.ToString() ??
                    throw new AzureBlobClientException(
                        AzureBlobClientErrorCode.IntegrityMismatch,
                        "Azure Blob listing omitted the entity tag.");
                yield return new AzureBlobObject(
                    item.Name,
                    properties.ContentLength ?? 0,
                    properties.ContentType ?? "application/octet-stream",
                    (properties.LastModified ?? DateTimeOffset.UnixEpoch).ToUniversalTime(),
                    entityTag,
                    entityTag,
                    properties.ContentHash,
                    new Dictionary<string, string>(
                        item.Metadata,
                        StringComparer.Ordinal));
            }
        }
    }

    public override async ValueTask<Uri> CreateSasUriAsync(
        AzureBlobSasRequest request,
        CancellationToken cancellationToken)
    {
        BlobClient client = _container.GetBlobClient(request.Key);
        BlobSasBuilder builder = new()
        {
            BlobContainerName = _container.Name,
            BlobName = request.Key,
            Resource = "b",
            StartsOn = request.StartsAtUtc,
            ExpiresOn = request.ExpiresAtUtc,
            Protocol = request.HttpsOnly
                ? SasProtocol.Https
                : SasProtocol.HttpsAndHttp,
            ContentDisposition = request.ContentDisposition,
        };
        builder.SetPermissions(request.Access switch
        {
            AzureBlobSasAccess.Read => BlobSasPermissions.Read,
            AzureBlobSasAccess.Create => BlobSasPermissions.Create,
            AzureBlobSasAccess.Write => BlobSasPermissions.Write,
            AzureBlobSasAccess.WriteBlock => BlobSasPermissions.Write,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        });

        try
        {
            Uri signed;
            if (_sasMode == AzureBlobSasMode.UserDelegation)
            {
                Response<UserDelegationKey> delegation =
                    await _service.GetUserDelegationKeyAsync(
                        request.StartsAtUtc,
                        request.ExpiresAtUtc,
                        cancellationToken);
                BlobSasQueryParameters query = builder.ToSasQueryParameters(
                    delegation.Value,
                    _accountName);
                signed = AppendQuery(client.Uri, query.ToString());
            }
            else
            {
                if (!client.CanGenerateSasUri)
                {
                    throw new AzureBlobClientException(
                        AzureBlobClientErrorCode.InvalidRequest,
                        "The configured Azure Blob client cannot generate shared-key SAS.");
                }

                signed = client.GenerateSasUri(builder);
            }

            if (request.BlockId is not null)
            {
                signed = AppendQuery(
                    signed,
                    $"comp=block&blockid={Uri.EscapeDataString(request.BlockId)}");
            }

            return signed;
        }
        catch (RequestFailedException error)
        {
            throw Translate(error);
        }
    }

    public override Uri GetBlobUri(string key) =>
        _container.GetBlobClient(key).Uri;

    private static async ValueTask<AzureBlobCopyState> ReadCopyStateAsync(
        string key,
        BlobClient client,
        CancellationToken cancellationToken)
    {
        Response<BlobProperties> response = await client.GetPropertiesAsync(
            conditions: null,
            cancellationToken);
        BlobProperties properties = response.Value;
        AzureBlobCopyStatus status = properties.CopyStatus switch
        {
            CopyStatus.Pending => AzureBlobCopyStatus.Pending,
            CopyStatus.Success => AzureBlobCopyStatus.Success,
            CopyStatus.Failed => AzureBlobCopyStatus.Failed,
            CopyStatus.Aborted => AzureBlobCopyStatus.Aborted,
            _ => AzureBlobCopyStatus.Pending,
        };
        return new AzureBlobCopyState(
            status,
            status == AzureBlobCopyStatus.Success
                ? ToObject(key, properties)
                : null,
            properties.CopyStatusDescription);
    }

    private static AzureBlobObject ToObject(
        string key,
        BlobProperties properties)
    {
        string entityTag = properties.ETag.ToString();
        return new AzureBlobObject(
            key,
            properties.ContentLength,
            properties.ContentType ?? "application/octet-stream",
            properties.LastModified.ToUniversalTime(),
            entityTag,
            entityTag,
            properties.ContentHash,
            new Dictionary<string, string>(
                properties.Metadata,
                StringComparer.Ordinal));
    }

    private static BlobRequestConditions ToConditions(
        AzureBlobConditions conditions) =>
        new()
        {
            IfMatch = conditions.IfMatch is null
                ? default
                : new ETag(conditions.IfMatch),
            IfNoneMatch = conditions.RequireMissing ? ETag.All : default,
        };

    private static AzureBlobClientException Translate(
        RequestFailedException error)
    {
        AzureBlobClientErrorCode code = error.Status switch
        {
            404 => AzureBlobClientErrorCode.NotFound,
            409 or 412 => AzureBlobClientErrorCode.PreconditionFailed,
            416 => AzureBlobClientErrorCode.InvalidRange,
            >= 500 => AzureBlobClientErrorCode.OutcomeUnknown,
            _ => AzureBlobClientErrorCode.InvalidRequest,
        };
        return new AzureBlobClientException(
            code,
            "Azure Blob service request failed.");
    }

    private static Uri AppendQuery(Uri uri, string query)
    {
        UriBuilder builder = new(uri);
        string existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing)
            ? query
            : string.Concat(existing, "&", query);
        return builder.Uri;
    }

    private static bool SameEndpoint(Uri left, Uri right) =>
        string.Equals(
            left.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.UriEscaped).TrimEnd('/'),
            right.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.UriEscaped).TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
}
