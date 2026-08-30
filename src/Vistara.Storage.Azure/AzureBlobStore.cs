using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Vistara.Application.Common.Storage;
using TokenCredential = global::Azure.Core.TokenCredential;

namespace Vistara.Storage.Azure;

public sealed class AzureBlobStore : IBlobStore
{
    private const string Sha256MetadataKey = "vistara_sha256";
    private const string UserMetadataPrefix = "vistara_m_";
    private const int MaximumBlocks = 50_000;
    private const long MaximumBlockBytes = 4_000_000_000;
    private readonly IAzureBlobClient _client;
    private readonly AzureBlobStoreOptions _options;

    public AzureBlobStore(AzureBlobStoreOptions options)
        : this(options, new AzureSdkBlobClientFactory())
    {
    }

    public AzureBlobStore(
        AzureBlobStoreOptions options,
        IAzureBlobClientFactory clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clientFactory);
        options.Validate();
        _options = options;
        _client = options.CredentialMode switch
        {
            AzureBlobCredentialMode.TokenCredential =>
                clientFactory.CreateWithTokenCredential(
                    options.ServiceUri,
                    options.AccountName,
                    options.ContainerName,
                    options.TokenCredential ?? new DefaultAzureCredential(),
                    options.EmulatorMode),
            AzureBlobCredentialMode.ConnectionString =>
                clientFactory.CreateWithConnectionString(
                    options.ConnectionString!,
                    options.ServiceUri,
                    options.AccountName,
                    options.ContainerName,
                    options.EmulatorMode),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        Capabilities = new BlobStoreCapabilities
        {
            SupportsDirectUpload = true,
            SupportsMultipartUpload = true,
            SupportsRangeReads = true,
            SupportsConditionalRead = true,
            SupportsConditionalCreate = true,
            SupportsConditionalReplace = true,
            SupportsConditionalCopy = true,
            SupportsConditionalDelete = true,
            SupportsConditionalMultipartCompletion = true,
            SupportsServerSideCopy = true,
            SupportsObjectVersioning = false,
            SupportsSignedRead = true,
            ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
            ListAfterWriteConsistency = BlobConsistencyModel.Strong,
            NativeChecksumAlgorithms =
            [
                BlobChecksumAlgorithm.Md5,
                BlobChecksumAlgorithm.Sha256,
            ],
            Limits = new BlobStoreLimits(
                checked((long)options.TransferBlockBytes * MaximumBlocks),
                1_024,
                MaximumBlocks,
                1,
                MaximumBlockBytes),
        };
    }

    public string Name => "azure";

    public BlobStoreCapabilities Capabilities { get; }

    public async ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        try
        {
            AzureBlobObject? blob = await _client.HeadAsync(
                key.Value,
                new AzureBlobConditions(),
                cancellationToken);
            return blob is null ? null : ToHead(key, blob);
        }
        catch (AzureBlobClientException error)
        {
            if (error.Code == AzureBlobClientErrorCode.NotFound)
            {
                return null;
            }

            throw Map(error, "Azure Blob head failed.");
        }
    }

    public async ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key,
        BlobReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(options);
        AzureBlobRange? range = options.Range is null
            ? null
            : new AzureBlobRange(options.Range.Offset, options.Range.Length);
        try
        {
            AzureBlobDownload download = await _client.DownloadAsync(
                key.Value,
                range,
                ToAzureConditions(options.EffectiveConditions),
                cancellationToken);
            try
            {
                BlobHead head = ToHead(key, download.Blob);
                BlobContentRange? contentRange = null;
                if (options.Range is not null)
                {
                    AzureBlobRange? returnedRange = download.Range;
                    if (returnedRange is null ||
                        returnedRange != range ||
                        download.TotalLength <= 0 ||
                        checked(returnedRange.Offset + returnedRange.Length) >
                        download.TotalLength)
                    {
                        throw new BlobStoreException(
                            BlobStoreErrorCode.IntegrityMismatch,
                            "Azure Blob returned an unexpected content range.");
                    }

                    contentRange = new BlobContentRange(
                        returnedRange.Offset,
                        returnedRange.Length,
                        download.TotalLength);
                }

                return new BlobReadHandle(download.Content, head, contentRange);
            }
            catch
            {
                await download.Content.DisposeAsync();
                throw;
            }
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob download failed.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Azure Blob MD5 values are transport integrity checks, not security primitives.")]
    public async ValueTask<BlobWriteResult> PutAsync(
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ValidateMetadata(options.Metadata);
        ValidateChecksums(options.Checksums);
        if (content.Length < 0 ||
            content.Length > Capabilities.Limits.MaxObjectBytes)
        {
            throw InvalidRequest("The declared Azure Blob length exceeds adapter limits.");
        }

        bool created = options.Conditions.RequireMissing;
        if (!options.Conditions.HasPrecondition)
        {
            created = await HeadAsync(key, cancellationToken) is null;
        }

        List<string> blockIds = [];
        using IncrementalHash sha256 = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
#pragma warning disable CA5351 // Azure Blob uses MD5 only as a service integrity checksum.
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.TransferBlockBytes);
        long observedLength = 0;
        try
        {
            await using Stream source = await content.OpenReadAsync(cancellationToken);
            if (source is null || !source.CanRead)
            {
                throw InvalidRequest(
                    "Replayable Azure Blob content must provide a readable stream.");
            }

            int blockNumber = 0;
            while (observedLength < content.Length)
            {
                int requested = checked((int)Math.Min(
                    _options.TransferBlockBytes,
                    content.Length - observedLength));
                int read = await ReadBlockAsync(
                    source,
                    buffer.AsMemory(0, requested),
                    cancellationToken);
                if (read == 0)
                {
                    throw IntegrityMismatch(
                        "The Azure Blob stream ended before its declared length.");
                }

                observedLength = checked(observedLength + read);
                sha256.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
                string blockId = CreateBlockId(Guid.NewGuid().ToString("N"), ++blockNumber);
                blockIds.Add(blockId);
                using MemoryStream block = new(buffer, 0, read, writable: false);
                try
                {
                    await _client.StageBlockAsync(
                        key.Value,
                        blockId,
                        block,
                        MD5.HashData(buffer.AsSpan(0, read)),
                        cancellationToken);
                }
                catch (AzureBlobClientException error)
                {
                    throw Map(error, "Azure Blob block staging failed.");
                }
            }

            byte[] extra = new byte[1];
            if (await source.ReadAsync(extra, cancellationToken) != 0)
            {
                throw IntegrityMismatch(
                    "The Azure Blob stream exceeded its declared length.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        string sha256Value = Convert.ToHexStringLower(sha256.GetHashAndReset());
        byte[] md5Value = md5.GetHashAndReset();
        ValidateObservedChecksums(options.Checksums, sha256Value, md5Value);
        Dictionary<string, string> metadata = CopyMetadata(options.Metadata);
        metadata[Sha256MetadataKey] = sha256Value;
        AzureBlobCommitOptions commit = new(
            (options.ContentType ?? new BlobMediaType("application/octet-stream")).Value,
            md5Value,
            metadata,
            ToAzureConditions(options.Conditions));
        try
        {
            AzureBlobObject result = await _client.CommitBlockListAsync(
                key.Value,
                blockIds,
                commit,
                cancellationToken);
            return new BlobWriteResult(ToHead(key, result), created);
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob block commit failed.");
        }
    }

    public async ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(source);
        ValidateKey(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ReplacementMetadata is not null)
        {
            ValidateMetadata(options.ReplacementMetadata);
        }

        AzureBlobObject sourceBlob;
        try
        {
            sourceBlob = await _client.HeadAsync(
                    source.Value,
                    ToAzureConditions(options.EffectiveSourceConditions),
                    cancellationToken) ??
                throw new BlobStoreException(
                    BlobStoreErrorCode.NotFound,
                    "The Azure Blob copy source was not found.");
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob copy source lookup failed.");
        }

        AzureBlobConditions sourceConditions =
            options.EffectiveSourceConditions.HasPrecondition
                ? ToAzureConditions(options.EffectiveSourceConditions)
                : new AzureBlobConditions(sourceBlob.EntityTag);
        AzureBlobCopyOptions copyOptions = new(
            sourceConditions,
            ToAzureConditions(options.EffectiveDestinationConditions),
            options.ReplacementMetadata is null
                ? null
                : EncodeMetadata(options.ReplacementMetadata));
        AzureBlobCopyState state;
        try
        {
            state = await _client.StartCopyAsync(
                source.Value,
                destination.Value,
                copyOptions,
                cancellationToken);
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob copy could not be started.");
        }

        for (int attempt = 0; attempt < _options.MaximumCopyPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (state.Status)
            {
                case AzureBlobCopyStatus.Success when state.Blob is not null:
                    return new BlobCopyResult(
                        ToHead(destination, state.Blob),
                        ToHead(source, sourceBlob).Identity);
                case AzureBlobCopyStatus.Failed:
                case AzureBlobCopyStatus.Aborted:
                    throw InvalidRequest("Azure Blob server-side copy failed.");
                case AzureBlobCopyStatus.Pending:
                    break;
                default:
                    throw OutcomeUnknown(
                        "Azure Blob returned an ambiguous copy status.");
            }

            if (attempt + 1 == _options.MaximumCopyPollAttempts)
            {
                break;
            }

            if (_options.CopyPollInterval > TimeSpan.Zero)
            {
                await Task.Delay(
                    _options.CopyPollInterval,
                    _options.TimeProvider,
                    cancellationToken);
            }

            try
            {
                state = await _client.GetCopyStateAsync(
                    destination.Value,
                    cancellationToken);
            }
            catch (AzureBlobClientException error)
            {
                throw Map(error, "Azure Blob copy status is ambiguous.");
            }
        }

        throw OutcomeUnknown(
            "Azure Blob copy remained pending after the bounded status check.");
    }

    public async ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            AzureBlobDeleteResult result = await _client.DeleteAsync(
                key.Value,
                ToAzureConditions(options.EffectiveConditions),
                cancellationToken);
            return new BlobDeleteResult(
                result.Deleted,
                result.DeletedBlob is null
                    ? null
                    : ToHead(key, result.DeletedBlob).Identity);
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob delete failed.");
        }
    }

    public async IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ValidatePrefix(options.Prefix);
        if (options.IncludeVersions)
        {
            throw Unsupported(
                "Azure Blob version enumeration is not exposed by this adapter.");
        }

        IAsyncEnumerable<AzureBlobObject> entries;
        try
        {
            entries = _client.ListAsync(
                options.Prefix,
                includeVersions: false,
                cancellationToken);
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob listing failed.");
        }

        await using IAsyncEnumerator<AzureBlobObject> enumerator =
            entries.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            AzureBlobObject blob;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    yield break;
                }

                blob = enumerator.Current;
            }
            catch (AzureBlobClientException error)
            {
                throw Map(error, "Azure Blob listing failed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            BlobKey key;
            try
            {
                key = new BlobKey(blob.Key);
            }
            catch (ArgumentException)
            {
                throw new BlobStoreException(
                    BlobStoreErrorCode.IntegrityMismatch,
                    "Azure Blob listing returned an invalid key.");
            }

            yield return ToHead(key, blob);
        }
    }

    public async ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ValidateKey(request.Key);
        ValidateMetadata(request.Metadata);
        ValidateChecksum(request.Checksum);
        ValidateLifetime(request.Lifetime);
        if (request.ContentLength > Capabilities.Limits.MaxObjectBytes)
        {
            throw InvalidRequest("The direct upload exceeds Azure Blob adapter limits.");
        }

        DateTimeOffset expiresAt = _options.TimeProvider.GetUtcNow() + request.Lifetime;
        AzureBlobSasAccess access = request.Conditions.RequireMissing
            ? AzureBlobSasAccess.Create
            : AzureBlobSasAccess.Write;
        Uri uri = await CreateValidatedSasUriAsync(
            new AzureBlobSasRequest(
                request.Key.Value,
                access,
                _options.TimeProvider.GetUtcNow() - TimeSpan.FromMinutes(5),
                expiresAt,
                !_options.EmulatorMode),
            cancellationToken);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Length"] = request.ContentLength.ToString(
                CultureInfo.InvariantCulture),
            ["Content-Type"] = request.ContentType.Value,
            ["x-ms-blob-type"] = "BlockBlob",
        };
        AddConditionHeaders(headers, request.Conditions);
        foreach ((string name, string value) in EncodeMetadata(request.Metadata))
        {
            headers[$"x-ms-meta-{name}"] = value;
        }

        if (request.Checksum is not null)
        {
            if (request.Checksum.Algorithm == BlobChecksumAlgorithm.Sha256)
            {
                headers[$"x-ms-meta-{Sha256MetadataKey}"] = request.Checksum.Value;
            }
            else
            {
                headers["Content-MD5"] = NormalizeMd5(request.Checksum.Value);
            }
        }

        return new DirectUploadPlan(
            request.Key,
            new SignedHttpRequest(HttpMethodKind.Put, uri, headers),
            expiresAt,
            request.Conditions,
            request.Checksum);
    }

    public ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ValidateKey(request.Key);
        ValidateMetadata(request.Metadata);
        ValidateChecksum(request.Checksum);
        ValidateMultipartLifetimes(request);
        if (request.ContentLength > Capabilities.Limits.MaxObjectBytes)
        {
            throw InvalidRequest("The multipart upload exceeds Azure Blob adapter limits.");
        }

        DateTimeOffset expiresAt =
            _options.TimeProvider.GetUtcNow() + request.SessionLifetime;
        string uploadId = Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(new MultipartSession(
            uploadId,
            request.Key,
            expiresAt,
            request.ContentLength,
            request.Conditions,
            MaximumBlocks,
            1,
            MaximumBlockBytes,
            request.PartPlanLifetime,
            request.ContentType,
            request.Checksum,
            request.Metadata,
            ProviderState(uploadId)));
    }

    public async ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        if (partNumber > session.MaxParts)
        {
            throw InvalidRequest("The Azure Blob block number exceeds the session limit.");
        }

        DateTimeOffset expiresAt = PartPlanExpiry(session);
        string blockId = CreateBlockId(session.UploadId, partNumber);
        Uri uri = await CreateValidatedSasUriAsync(
            new AzureBlobSasRequest(
                session.Key.Value,
                AzureBlobSasAccess.WriteBlock,
                _options.TimeProvider.GetUtcNow() - TimeSpan.FromMinutes(5),
                expiresAt,
                !_options.EmulatorMode,
                BlockId: blockId),
            cancellationToken);
        return new MultipartPartPlan(
            session.UploadId,
            partNumber,
            new SignedHttpRequest(HttpMethodKind.Put, uri),
            session.MinPartBytes,
            session.MaxPartBytes,
            expiresAt);
    }

    public async ValueTask<MultipartCompletion> CompleteMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session, requireActive: false);
        ArgumentNullException.ThrowIfNull(parts);
        ValidateParts(session, parts);
        List<string> blockIds = parts
            .Select(part => CreateBlockId(session.UploadId, part.PartNumber))
            .ToList();
        Dictionary<string, string> metadata = CopyMetadata(session.Metadata);
        byte[] contentMd5 = [];
        if (session.Checksum is not null)
        {
            if (session.Checksum.Algorithm == BlobChecksumAlgorithm.Sha256)
            {
                metadata[Sha256MetadataKey] = session.Checksum.Value;
            }
            else
            {
                contentMd5 = Convert.FromBase64String(
                    NormalizeMd5(session.Checksum.Value));
            }
        }

        try
        {
            AzureBlobObject result = await _client.CommitBlockListAsync(
                session.Key.Value,
                blockIds,
                new AzureBlobCommitOptions(
                    session.ContentType.Value,
                    contentMd5,
                    metadata,
                    ToAzureConditions(session.CompletionConditions)),
                cancellationToken);
            return new MultipartCompletion(ToHead(session.Key, result));
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob multipart commit failed.");
        }
    }

    public ValueTask AbortMultipartAsync(
        MultipartSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session, requireActive: false);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<SignedAccessPlan> CreateReadGrantAsync(
        BlobKey key,
        ReadGrantOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(options);
        ValidateLifetime(options.Lifetime);
        if (options.DownloadFileName is { } fileName &&
            (fileName.Length > 255 ||
             fileName.Contains('\r', StringComparison.Ordinal) ||
             fileName.Contains('\n', StringComparison.Ordinal)))
        {
            throw InvalidRequest("The Azure Blob download filename is invalid.");
        }

        DateTimeOffset expiresAt = _options.TimeProvider.GetUtcNow() + options.Lifetime;
        string? disposition = options.DownloadFileName is null
            ? null
            : $"attachment; filename=\"{options.DownloadFileName.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        Uri uri = await CreateValidatedSasUriAsync(
            new AzureBlobSasRequest(
                key.Value,
                AzureBlobSasAccess.Read,
                _options.TimeProvider.GetUtcNow() - TimeSpan.FromMinutes(5),
                expiresAt,
                !_options.EmulatorMode,
                disposition),
            cancellationToken);
        KeyValuePair<string, string>[] headers = options.Range is null
            ? []
            :
            [
                new KeyValuePair<string, string>(
                    "Range",
                    $"bytes={options.Range.Offset}-{checked(options.Range.Offset + options.Range.Length - 1)}"),
            ];
        return new SignedAccessPlan(
            key,
            new SignedHttpRequest(HttpMethodKind.Get, uri, headers),
            expiresAt,
            options.Range);
    }

    private void ValidateSession(
        MultipartSession session,
        bool requireActive = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateKey(session.Key);
        ValidateMetadata(session.Metadata);
        ValidateChecksum(session.Checksum);
        if (!string.Equals(
                session.ProviderState,
                ProviderState(session.UploadId),
                StringComparison.Ordinal) ||
            session.ContentLength > Capabilities.Limits.MaxObjectBytes ||
            session.MaxParts != MaximumBlocks ||
            session.MinPartBytes != 1 ||
            session.MaxPartBytes != MaximumBlockBytes ||
            (requireActive &&
             (session.PartPlanLifetime > _options.MaximumGrantLifetime ||
              session.PartPlanLifetime > TimeSpan.FromDays(7))))
        {
            throw InvalidRequest("The Azure Blob multipart session is inconsistent.");
        }

        if (requireActive &&
            _options.TimeProvider.GetUtcNow() >= session.ExpiresAtUtc)
        {
            throw InvalidRequest("The Azure Blob multipart session has expired.");
        }
    }

    private DateTimeOffset PartPlanExpiry(MultipartSession session)
    {
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now + session.PartPlanLifetime;
        return expiresAt < session.ExpiresAtUtc
            ? expiresAt
            : session.ExpiresAtUtc;
    }

    private static void ValidateParts(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts)
    {
        if (parts.Count is < 1 or > MaximumBlocks)
        {
            throw InvalidRequest("The Azure Blob multipart part count is invalid.");
        }

        long total = 0;
        for (int index = 0; index < parts.Count; index++)
        {
            UploadedPart part = parts[index];
            if (part.PartNumber != index + 1 ||
                part.SizeBytes > session.MaxPartBytes)
            {
                throw InvalidRequest(
                    "Azure Blob multipart parts must be contiguous and canonically ordered.");
            }

            total = checked(total + part.SizeBytes);
        }

        if (total != session.ContentLength)
        {
            throw InvalidRequest(
                "Azure Blob multipart parts do not match the declared length.");
        }
    }

    private async ValueTask<Uri> CreateValidatedSasUriAsync(
        AzureBlobSasRequest request,
        CancellationToken cancellationToken)
    {
        Uri signed;
        try
        {
            signed = await _client.CreateSasUriAsync(request, cancellationToken);
        }
        catch (AzureBlobClientException error)
        {
            throw Map(error, "Azure Blob could not issue a signed grant.");
        }

        Uri expected = _client.GetBlobUri(request.Key);
        if (!signed.IsAbsoluteUri ||
            !string.Equals(signed.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(signed.Host, expected.Host, StringComparison.OrdinalIgnoreCase) ||
            signed.Port != expected.Port ||
            !string.Equals(
                signed.GetComponents(UriComponents.Path, UriFormat.UriEscaped),
                expected.GetComponents(UriComponents.Path, UriFormat.UriEscaped),
                StringComparison.Ordinal) ||
            string.IsNullOrEmpty(signed.Query))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "Azure Blob returned a signed URL outside the requested blob scope.");
        }

        return signed;
    }

    private static BlobHead ToHead(BlobKey requestedKey, AzureBlobObject blob)
    {
        if (!string.Equals(requestedKey.Value, blob.Key, StringComparison.Ordinal))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "Azure Blob returned a different blob key than requested.");
        }

        try
        {
            List<BlobChecksum> checksums = [];
            if (blob.ContentMd5 is { Length: > 0 })
            {
                checksums.Add(new BlobChecksum(
                    BlobChecksumAlgorithm.Md5,
                    Convert.ToHexStringLower(blob.ContentMd5)));
            }

            Dictionary<string, string> metadata = DecodeMetadata(blob.Metadata);
            if (blob.Metadata.TryGetValue(Sha256MetadataKey, out string? sha256))
            {
                checksums.Add(new BlobChecksum(
                    BlobChecksumAlgorithm.Sha256,
                    sha256));
            }

            BlobVersion version = new(blob.Version);
            BlobProperties properties = new(
                blob.ContentLength,
                new BlobMediaType(blob.ContentType),
                blob.LastModifiedUtc.ToUniversalTime(),
                version,
                new BlobEntityTag(blob.EntityTag),
                checksums,
                new BlobMetadata(metadata));
            return new BlobHead(
                new BlobIdentity(requestedKey, version),
                properties);
        }
        catch (ArgumentException error)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "Azure Blob returned invalid object properties.",
                error);
        }
    }

    private static AzureBlobConditions ToAzureConditions(
        BlobRequestConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        string? version = conditions.IfMatch?.Value;
        string? entityTag = conditions.IfEntityTagMatch?.Value;
        if (version is not null &&
            entityTag is not null &&
            !string.Equals(version, entityTag, StringComparison.Ordinal))
        {
            throw InvalidRequest(
                "Azure Blob cannot apply different version and entity-tag match conditions.");
        }

        return new AzureBlobConditions(
            version ?? entityTag,
            conditions.RequireMissing);
    }

    private static void AddConditionHeaders(
        Dictionary<string, string> headers,
        BlobRequestConditions conditions)
    {
        AzureBlobConditions azure = ToAzureConditions(conditions);
        if (azure.RequireMissing)
        {
            headers["If-None-Match"] = "*";
        }
        else if (azure.IfMatch is not null)
        {
            headers["If-Match"] = azure.IfMatch;
        }
    }

    private static async ValueTask<int> ReadBlockAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static string CreateBlockId(string uploadId, int partNumber) =>
        Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{uploadId}:{partNumber:D5}"));

    private static Dictionary<string, string> CopyMetadata(BlobMetadata metadata) =>
        EncodeMetadata(metadata);

    private static Dictionary<string, string> EncodeMetadata(BlobMetadata metadata) =>
        metadata.AsReadOnly().ToDictionary(
            pair => string.Concat(
                UserMetadataPrefix,
                Convert.ToHexStringLower(Encoding.UTF8.GetBytes(pair.Key))),
            pair => pair.Value,
            StringComparer.Ordinal);

    private static Dictionary<string, string> DecodeMetadata(
        IReadOnlyDictionary<string, string> metadata)
    {
        Dictionary<string, string> decoded = new(StringComparer.Ordinal);
        foreach ((string key, string value) in metadata)
        {
            if (!key.StartsWith(UserMetadataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string encoded = key[UserMetadataPrefix.Length..];
            try
            {
                string applicationKey = Encoding.UTF8.GetString(
                    Convert.FromHexString(encoded));
                if (!decoded.TryAdd(applicationKey, value))
                {
                    throw new FormatException();
                }
            }
            catch (FormatException)
            {
                throw new BlobStoreException(
                    BlobStoreErrorCode.IntegrityMismatch,
                    "Azure Blob returned invalid encoded application metadata.");
            }
        }

        return decoded;
    }

    private static void ValidateMetadata(BlobMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.AsReadOnly().Values.Any(value =>
                value.Any(char.IsControl)))
        {
            throw InvalidRequest(
                "Azure Blob metadata values cannot contain control characters.");
        }
    }

    private static void ValidateChecksums(IReadOnlyList<BlobChecksum> checksums)
    {
        foreach (BlobChecksum checksum in checksums)
        {
            ValidateChecksum(checksum);
        }
    }

    private static void ValidateChecksum(BlobChecksum? checksum)
    {
        if (checksum is not null &&
            checksum.Algorithm is not (
                BlobChecksumAlgorithm.Sha256 or BlobChecksumAlgorithm.Md5))
        {
            throw Unsupported(
                "Azure Blob adapter validates only SHA-256 and MD5 checksums.");
        }
    }

    private static void ValidateObservedChecksums(
        IReadOnlyList<BlobChecksum> checksums,
        string sha256,
        byte[] md5)
    {
        foreach (BlobChecksum checksum in checksums)
        {
            bool matches = checksum.Algorithm switch
            {
                BlobChecksumAlgorithm.Sha256 =>
                    FixedTimeEquals(checksum.Value, sha256),
                BlobChecksumAlgorithm.Md5 =>
                    CryptographicOperations.FixedTimeEquals(
                        Convert.FromBase64String(NormalizeMd5(checksum.Value)),
                        md5),
                _ => false,
            };
            if (!matches)
            {
                throw IntegrityMismatch(
                    "The Azure Blob bytes did not match the required checksum.");
            }
        }
    }

    private static string NormalizeMd5(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 32 && trimmed.All(Uri.IsHexDigit))
        {
            return Convert.ToBase64String(Convert.FromHexString(trimmed));
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(trimmed);
            if (decoded.Length != 16)
            {
                throw new FormatException();
            }

            return Convert.ToBase64String(decoded);
        }
        catch (FormatException)
        {
            throw InvalidRequest("The Azure Blob MD5 checksum is invalid.");
        }
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private void ValidateLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero ||
            lifetime > _options.MaximumGrantLifetime)
        {
            throw InvalidRequest(
                "The requested Azure Blob grant lifetime is outside configured bounds.");
        }
    }

    private void ValidateMultipartLifetimes(MultipartRequest request)
    {
        if (request.SessionLifetime > TimeSpan.FromDays(7))
        {
            throw InvalidRequest(
                "The Azure Blob multipart session lifetime exceeds seven days.");
        }

        ValidateLifetime(request.PartPlanLifetime);
    }

    private static string ProviderState(string uploadId) =>
        $"azure-block:v1:{uploadId}";

    private static void ValidateKey(BlobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Encoding.UTF8.GetByteCount(key.Value) > 1_024)
        {
            throw InvalidRequest("The Azure Blob key exceeds 1,024 UTF-8 bytes.");
        }
    }

    private static void ValidatePrefix(string? prefix)
    {
        if (prefix is null)
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(prefix) > 1_024 ||
            (prefix.Length > 0 && prefix[0] == '/') ||
            prefix.Contains("//", StringComparison.Ordinal) ||
            prefix.Split('/').Any(segment => segment is "." or "..") ||
            prefix.Any(character =>
                character > 127 ||
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '/' or '.' or '_' or '-')))
        {
            throw InvalidRequest("The Azure Blob listing prefix is invalid.");
        }
    }

    private static BlobStoreException Map(
        AzureBlobClientException error,
        string message) =>
        new(
            error.Code switch
            {
                AzureBlobClientErrorCode.NotFound => BlobStoreErrorCode.NotFound,
                AzureBlobClientErrorCode.PreconditionFailed =>
                    BlobStoreErrorCode.PreconditionFailed,
                AzureBlobClientErrorCode.InvalidRange => BlobStoreErrorCode.InvalidRange,
                AzureBlobClientErrorCode.InvalidRequest => BlobStoreErrorCode.InvalidRequest,
                AzureBlobClientErrorCode.IntegrityMismatch =>
                    BlobStoreErrorCode.IntegrityMismatch,
                AzureBlobClientErrorCode.OutcomeUnknown =>
                    BlobStoreErrorCode.OutcomeUnknown,
                _ => BlobStoreErrorCode.OutcomeUnknown,
            },
            message);

    private static BlobStoreException Unsupported(string message) =>
        new(BlobStoreErrorCode.Unsupported, message);

    private static BlobStoreException InvalidRequest(string message) =>
        new(BlobStoreErrorCode.InvalidRequest, message);

    private static BlobStoreException IntegrityMismatch(string message) =>
        new(BlobStoreErrorCode.IntegrityMismatch, message);

    private static BlobStoreException OutcomeUnknown(string message) =>
        new(BlobStoreErrorCode.OutcomeUnknown, message);

}

internal sealed class AzureSdkBlobClientFactory : IAzureBlobClientFactory
{
    public IAzureBlobClient CreateWithTokenCredential(
        Uri serviceUri,
        string accountName,
        string containerName,
        TokenCredential credential,
        bool emulatorMode) =>
        new AzureSdkBlobClient(
            serviceUri,
            accountName,
            containerName,
            credential,
            emulatorMode);

    public IAzureBlobClient CreateWithConnectionString(
        string connectionString,
        Uri serviceUri,
        string accountName,
        string containerName,
        bool emulatorMode) =>
        new AzureSdkBlobClient(
            connectionString,
            serviceUri,
            accountName,
            containerName,
            emulatorMode);
}
