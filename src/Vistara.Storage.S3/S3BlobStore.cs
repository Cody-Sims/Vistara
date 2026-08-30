using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Amazon.Runtime;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.S3;

public sealed class S3BlobStore :
    IBlobStore,
    IDurableMultipartBlobStore,
    IAsyncDisposable
{
    private const string VerifiedSha256MetadataKey = "vistara-sha256";
    private readonly S3ValidatedOptions _options;
    private readonly IS3Transport _transport;
    private readonly TimeProvider _timeProvider;

    internal S3BlobStore(
        S3ValidatedOptions options,
        IS3Transport transport,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    private void ValidateSinglePutLength(long contentLength)
    {
        if (contentLength > _options.Profile.MaxSinglePutBytes)
        {
            throw Invalid(
                "The declared S3 object exceeds the provider single-request upload limit.");
        }
    }

    public S3BlobStore(
        S3BlobStoreOptions options,
        AWSCredentials credentials,
        TimeProvider? timeProvider = null)
        : this(CreateDependencies(options, credentials, timeProvider))
    {
    }

    private S3BlobStore(Dependencies dependencies)
        : this(
            dependencies.Options,
            dependencies.Transport,
            dependencies.TimeProvider)
    {
    }

    public string Name => _options.Profile.Name;

    public BlobStoreCapabilities Capabilities => _options.Profile.Capabilities;

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    public async ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        try
        {
            S3ObjectDescriptor? descriptor = await _transport.HeadAsync(
                key.Value,
                cancellationToken);
            return descriptor is null ? null : ToHead(descriptor, key);
        }
        catch (S3TransportException error)
        {
            if (error.Error == S3TransportError.NotFound)
            {
                return null;
            }

            throw Map(error);
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
        if (options.Range is not null && !Capabilities.SupportsRangeReads)
        {
            throw Unsupported("The configured S3 profile does not support range reads.");
        }

        S3Conditions conditions = TranslateReadConditions(options.EffectiveConditions);
        string? range = options.Range is null
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"bytes={options.Range.Offset}-{checked(options.Range.Offset + options.Range.Length - 1)}");
        try
        {
            S3ReadResult result = await _transport.GetAsync(
                new S3GetCommand(key.Value, range, conditions),
                cancellationToken);
            try
            {
                BlobHead head = ToHead(result.Descriptor, key);
                return new BlobReadHandle(
                    result.Content,
                    head,
                    result.ContentRange);
            }
            catch
            {
                await result.Content.DisposeAsync();
                throw;
            }
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

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
        ValidateObjectLength(content.Length);
        ValidateSinglePutLength(content.Length);
        ValidateMetadata(options.Metadata);
        S3Conditions conditions = TranslateWriteConditions(options.Conditions);
        IReadOnlyList<S3WireChecksum> checksums = TranslateChecksums(options.Checksums);
        await using Stream stream = await content.OpenReadAsync(cancellationToken);
        if (!stream.CanRead)
        {
            throw Invalid("Replayable S3 content must provide a readable stream.");
        }

        try
        {
            S3ObjectDescriptor descriptor = await _transport.PutAsync(
                new S3PutCommand(
                    key.Value,
                    stream,
                    content.Length,
                    (options.ContentType ??
                        new BlobMediaType("application/octet-stream")).Value,
                    options.Metadata.AsReadOnly(),
                    checksums,
                    conditions),
                cancellationToken);
            return new BlobWriteResult(
                ToHead(descriptor, key),
                options.Conditions.RequireMissing);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
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
        if (options.EffectiveDestinationConditions.HasPrecondition)
        {
            if (!options.EffectiveDestinationConditions.RequireMissing ||
                options.EffectiveDestinationConditions.IfMatch is not null ||
                options.EffectiveDestinationConditions.IfEntityTagMatch is not null)
            {
                throw Unsupported(
                    "The S3 adapter only supports a create-only destination precondition.");
            }

            if (!Capabilities.SupportsConditionalCreate)
            {
                throw Unsupported(
                    "The configured S3 profile cannot guarantee an atomic create-only destination.");
            }

            return await CopyCreateOnlyByStreamingAsync(
                source,
                destination,
                options,
                cancellationToken);
        }

        if (options.ReplacementMetadata is not null)
        {
            ValidateMetadata(options.ReplacementMetadata);
        }

        BlobRequestConditions sourceConditions =
            options.EffectiveSourceConditions;
        if (sourceConditions.HasPrecondition &&
            !_options.Profile.SupportsConditionalCopySource)
        {
            throw Unsupported(
                "The configured S3 profile cannot guarantee an atomic conditional copy source.");
        }

        string? sourceIfMatch = TranslateMatchOnlyConditions(
            sourceConditions,
            "S3 copy source");
        try
        {
            S3CopyResult result = await _transport.CopyAsync(
                new S3CopyCommand(
                    source.Value,
                    destination.Value,
                    sourceIfMatch,
                    options.ReplacementMetadata?.AsReadOnly()),
                cancellationToken);
            return new BlobCopyResult(
                ToHead(result.Destination, destination),
                ToHead(result.Source, source).Identity);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    private async ValueTask<BlobCopyResult> CopyCreateOnlyByStreamingAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ReplacementMetadata is not null)
        {
            ValidateMetadata(options.ReplacementMetadata);
        }

        S3Conditions sourceConditions =
            TranslateReadConditions(options.EffectiveSourceConditions);
        S3ReadResult verificationRead;
        try
        {
            verificationRead = await _transport.GetAsync(
                new S3GetCommand(source.Value, null, sourceConditions),
                cancellationToken);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }

        BlobHead sourceHead;
        BlobMetadata metadata;
        ReadOnlyCollection<S3WireChecksum> checksums;
        await using (Stream verificationContent = verificationRead.Content)
        {
            sourceHead = ValidateCopySource(verificationRead, source);
            metadata =
                options.ReplacementMetadata ?? sourceHead.Properties.Metadata;
            checksums =
                TranslateChecksums(sourceHead.Properties.Checksums.Where(
                    checksum => Capabilities.NativeChecksumAlgorithms.Contains(
                        checksum.Algorithm)));
            if (checksums.Count > 0)
            {
                EnsureNativeChecksumMatchesVerification(
                    sourceHead,
                    options.ReplacementMetadata);
                return await PutCopiedContentAsync(
                    destination,
                    verificationContent,
                    sourceHead,
                    metadata,
                    checksums,
                    cancellationToken);
            }

            BlobChecksum verifiedSha256 =
                GetVerifiedPromotionChecksum(options.ReplacementMetadata) ??
                throw Unsupported(
                    "The S3 source lacks a native or independently verified checksum required for create-only publication.");
            if (!Capabilities.NativeChecksumAlgorithms.Contains(
                    BlobChecksumAlgorithm.Sha256))
            {
                throw Unsupported(
                    "The S3 profile cannot enforce the independently verified checksum during create-only publication.");
            }

            await VerifySha256Async(
                verificationContent,
                sourceHead.Properties.ContentLength,
                verifiedSha256,
                cancellationToken);
        }

        S3ReadResult publicationRead;
        try
        {
            publicationRead = await _transport.GetAsync(
                new S3GetCommand(
                    source.Value,
                    null,
                    new S3Conditions(
                        sourceHead.Properties.EntityTag.Value,
                        RequireMissing: false)),
                cancellationToken);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }

        await using Stream publicationContent = publicationRead.Content;
        BlobHead publicationHead = ValidateCopySource(publicationRead, source);
        if (publicationHead.Identity.Version != sourceHead.Identity.Version ||
            publicationHead.Properties.ContentLength !=
            sourceHead.Properties.ContentLength ||
            publicationHead.Properties.ContentType !=
            sourceHead.Properties.ContentType)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The S3 copy source changed after independent verification.");
        }

        BlobChecksum verifiedChecksum =
            GetVerifiedPromotionChecksum(options.ReplacementMetadata)!;
        ReadOnlyCollection<S3WireChecksum> destinationChecksums =
            TranslateChecksums([verifiedChecksum]);
        return await PutCopiedContentAsync(
            destination,
            publicationContent,
            sourceHead,
            metadata,
            destinationChecksums,
            cancellationToken);
    }

    private BlobHead ValidateCopySource(
        S3ReadResult read,
        BlobKey source)
    {
        BlobHead sourceHead = ToHead(read.Descriptor, source);
        if (read.ContentRange is not null ||
            sourceHead.Properties.ContentLength > _options.Profile.MaxSinglePutBytes)
        {
            throw Unsupported(
                "The S3 profile cannot stream this source through one atomic create-only request.");
        }

        return sourceHead;
    }

    private async ValueTask<BlobCopyResult> PutCopiedContentAsync(
        BlobKey destination,
        Stream content,
        BlobHead sourceHead,
        BlobMetadata metadata,
        IReadOnlyList<S3WireChecksum> checksums,
        CancellationToken cancellationToken)
    {
        try
        {
            S3ObjectDescriptor descriptor = await _transport.PutAsync(
                new S3PutCommand(
                    destination.Value,
                    content,
                    sourceHead.Properties.ContentLength,
                    sourceHead.Properties.ContentType.Value,
                    metadata.AsReadOnly(),
                    checksums,
                    new S3Conditions(null, RequireMissing: true)),
                cancellationToken);
            return new BlobCopyResult(
                ToHead(descriptor, destination),
                sourceHead.Identity);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    private static BlobChecksum? GetVerifiedPromotionChecksum(
        BlobMetadata? replacementMetadata)
    {
        if (replacementMetadata is null ||
            !replacementMetadata.TryGetValue(
                VerifiedSha256MetadataKey,
                out string? value))
        {
            return null;
        }

        try
        {
            return new BlobChecksum(BlobChecksumAlgorithm.Sha256, value!);
        }
        catch (ArgumentException error)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The independently verified promotion checksum is invalid.",
                error);
        }
    }

    private static void EnsureNativeChecksumMatchesVerification(
        BlobHead sourceHead,
        BlobMetadata? replacementMetadata)
    {
        BlobChecksum? verified = GetVerifiedPromotionChecksum(replacementMetadata);
        BlobChecksum? native = sourceHead.Properties.Checksums.SingleOrDefault(
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);
        if (verified is null ||
            native is null ||
            FixedTimeEquals(native.Value, verified.Value))
        {
            return;
        }

        throw new BlobStoreException(
            BlobStoreErrorCode.IntegrityMismatch,
            "The S3 native checksum did not match the independently verified checksum.");
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));

    private static async ValueTask VerifySha256Async(
        Stream content,
        long expectedLength,
        BlobChecksum expectedChecksum,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            int read = await content.ReadAsync(
                buffer.AsMemory(
                    0,
                    checked((int)Math.Min(buffer.Length, remaining))),
                cancellationToken);
            if (read == 0)
            {
                throw new BlobStoreException(
                    BlobStoreErrorCode.IntegrityMismatch,
                    "The S3 copy source ended before its declared length.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        if (await content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The S3 copy source exceeded its declared length.");
        }

        byte[] expected = Convert.FromHexString(expectedChecksum.Value);
        byte[] actual = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The S3 copy source did not match its independently verified checksum.");
        }
    }

    public async ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(options);
        BlobRequestConditions conditions = options.EffectiveConditions;
        if (conditions.HasPrecondition && !Capabilities.SupportsConditionalDelete)
        {
            throw Unsupported(
                "The configured S3 profile cannot guarantee an atomic conditional delete.");
        }

        string? ifMatch = TranslateMatchOnlyConditions(
            conditions,
            "S3 delete");
        try
        {
            S3DeleteResult result = await _transport.DeleteAsync(
                new S3DeleteCommand(key.Value, ifMatch),
                cancellationToken);
            return new BlobDeleteResult(
                result.Deleted,
                result.DeletedObject is null
                    ? null
                    : ToHead(result.DeletedObject, key).Identity);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    public async IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ValidatePrefix(options.Prefix);
        if (options.IncludeVersions && !Capabilities.SupportsObjectVersioning)
        {
            throw Unsupported(
                "The configured S3 profile does not support object version listing.");
        }

        IAsyncEnumerable<S3ObjectDescriptor> entries;
        try
        {
            entries = _transport.ListAsync(
                options.Prefix,
                options.IncludeVersions,
                cancellationToken);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }

        await using IAsyncEnumerator<S3ObjectDescriptor> enumerator =
            entries.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            S3ObjectDescriptor descriptor;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    yield break;
                }

                descriptor = enumerator.Current;
            }
            catch (S3TransportException error)
            {
                throw Map(error);
            }

            if (S3DurableMultipartState.IsControlKey(descriptor.Key))
            {
                continue;
            }

            yield return ToHead(descriptor);
        }
    }

    public async ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ValidateKey(request.Key);
        if (!Capabilities.SupportsDirectUpload)
        {
            throw Unsupported("The configured S3 profile does not support direct upload.");
        }

        ValidateObjectLength(request.ContentLength);
        ValidateSinglePutLength(request.ContentLength);
        ValidateMetadata(request.Metadata);
        DateTimeOffset expiresAt = ValidateLifetime(request.Lifetime);
        S3Conditions conditions = TranslateWriteConditions(request.Conditions);
        S3WireChecksum? checksum = request.Checksum is null
            ? null
            : TranslateChecksums([request.Checksum]).Single();
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = request.ContentType.Value,
            ["Content-Length"] = request.ContentLength.ToString(CultureInfo.InvariantCulture),
        };
        AddConditionHeaders(headers, conditions);
        if (checksum is not null)
        {
            headers[ChecksumHeader(checksum.Algorithm)] = checksum.WireValue;
        }

        foreach ((string name, string value) in request.Metadata.AsReadOnly())
        {
            headers[$"x-amz-meta-{name}"] = value;
        }

        Uri url = await PresignAsync(
            new S3PresignCommand(
                HttpMethodKind.Put,
                request.Key.Value,
                expiresAt,
                ReadOnly(headers),
                EmptyParameters),
            cancellationToken);
        return new DirectUploadPlan(
            request.Key,
            new SignedHttpRequest(HttpMethodKind.Put, url, headers),
            expiresAt,
            request.Conditions,
            request.Checksum);
    }

    public async ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request,
        CancellationToken cancellationToken)
    {
        return await GetOrCreateMultipartCoreAsync(
            $"begin-{Guid.CreateVersion7():N}",
            request,
            recoverUnmarkedUpload: false,
            cancellationToken);
    }

    public ValueTask<MultipartSession> GetOrCreateMultipartAsync(
        string issuanceId,
        MultipartRequest request,
        CancellationToken cancellationToken) =>
        GetOrCreateMultipartCoreAsync(
            issuanceId,
            request,
            recoverUnmarkedUpload: true,
            cancellationToken);

    public async ValueTask<MultipartInventory> InspectMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> claimedParts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(claimedParts);
        ValidatedS3Session validated = ValidateSession(
            session,
            requireActive: false);
        if (claimedParts.Count > session.MaxParts)
        {
            throw Invalid("The claimed multipart part count exceeds the session limit.");
        }

        S3MarkerHandle marker = await RequireMarkerAsync(
            validated.Identity,
            cancellationToken);
        BlobHead? completed = await HeadAsync(session.Key, cancellationToken);
        if (completed is not null)
        {
            ValidateCompletedInventory(session, completed);
            return new MultipartInventory(
                MultipartInventoryState.Completed,
                [],
                completed);
        }

        if (marker.Marker.Status == S3DurableMultipartState.Aborted)
        {
            return new MultipartInventory(
                MultipartInventoryState.Aborted,
                []);
        }

        if (marker.Marker.Status == S3DurableMultipartState.Completed)
        {
            return new MultipartInventory(
                MultipartInventoryState.Missing,
                []);
        }

        try
        {
            IReadOnlyList<S3UploadedPartDescriptor> parts =
                await _transport.ListPartsAsync(
                    session.Key.Value,
                    session.UploadId,
                    cancellationToken);
            return new MultipartInventory(
                MultipartInventoryState.Active,
                TranslateUploadedParts(session, parts));
        }
        catch (S3TransportException error) when (
            error.Error == S3TransportError.NotFound)
        {
            completed = await HeadAsync(session.Key, cancellationToken);
            if (completed is not null)
            {
                ValidateCompletedInventory(session, completed);
                return new MultipartInventory(
                    MultipartInventoryState.Completed,
                    [],
                    completed);
            }

            return new MultipartInventory(
                MultipartInventoryState.Missing,
                []);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    private async ValueTask<MultipartSession> GetOrCreateMultipartCoreAsync(
        string issuanceId,
        MultipartRequest request,
        bool recoverUnmarkedUpload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset expiresAt = ValidateMultipartRequest(request);
        string issuanceHash =
            S3DurableMultipartState.IssuanceHash(issuanceId);
        string markerKey = S3DurableMultipartState.ControlKey(issuanceHash);
        S3MarkerHandle? existing = await ReadMarkerAsync(
            markerKey,
            cancellationToken);
        if (existing is not null)
        {
            S3DurableMultipartState.ValidateConfiguration(
                existing.Marker.Identity,
                _options);
            S3DurableMultipartState.ValidateRequest(
                existing.Marker.Identity,
                request);
            return CreateSession(existing.Marker.Identity, request);
        }

        IReadOnlyList<S3MultipartUploadDescriptor> activeUploads = [];
        if (recoverUnmarkedUpload)
        {
            try
            {
                activeUploads = await _transport.ListMultipartUploadsAsync(
                    request.Key.Value,
                    cancellationToken);
            }
            catch (S3TransportException error)
            {
                throw Map(error);
            }
        }

        if (activeUploads.Any(upload =>
                !string.Equals(
                    upload.Key,
                    request.Key.Value,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(upload.UploadId) ||
                upload.UploadId.Length > 1_024 ||
                upload.UploadId.Any(char.IsControl) ||
                upload.InitiatedAtUtc.Offset != TimeSpan.Zero ||
                upload.InitiatedAtUtc < DateTimeOffset.UnixEpoch))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The S3 provider returned invalid active multipart uploads.");
        }

        string uploadId;
        S3MultipartUploadDescriptor? recovered = activeUploads
            .OrderBy(upload => upload.InitiatedAtUtc)
            .ThenBy(upload => upload.UploadId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (recovered is not null)
        {
            uploadId = recovered.UploadId;
        }
        else
        {
            try
            {
                uploadId = await _transport.BeginMultipartAsync(
                    new S3BeginMultipartCommand(
                        request.Key.Value,
                        request.ContentType.Value,
                        request.Metadata.AsReadOnly(),
                        request.Checksum?.Algorithm),
                    cancellationToken);
            }
            catch (S3TransportException error)
            {
                throw Map(error);
            }
        }

        S3MultipartIdentity identity =
            S3DurableMultipartState.CreateIdentity(
                _options,
                issuanceHash,
                request,
                uploadId,
                expiresAt);
        var marker = new S3MultipartMarker(
            identity,
            S3DurableMultipartState.Active);
        try
        {
            _ = await WriteMarkerAsync(
                marker,
                new S3Conditions(null, RequireMissing: true),
                cancellationToken);
            return CreateSession(identity, request);
        }
        catch (BlobStoreException error)
            when (error.Code is BlobStoreErrorCode.PreconditionFailed or
                BlobStoreErrorCode.OutcomeUnknown)
        {
            S3MarkerHandle? winner = await ReadMarkerAsync(
                markerKey,
                cancellationToken);
            if (winner is null)
            {
                throw;
            }

            S3DurableMultipartState.ValidateConfiguration(
                winner.Marker.Identity,
                _options);
            S3DurableMultipartState.ValidateRequest(
                winner.Marker.Identity,
                request);
            if (!string.Equals(
                    winner.Marker.Identity.UploadId,
                    uploadId,
                    StringComparison.Ordinal))
            {
                try
                {
                    await _transport.AbortMultipartAsync(
                        request.Key.Value,
                        uploadId,
                        cancellationToken);
                }
                catch (S3TransportException abortError)
                {
                    throw Map(abortError);
                }
            }

            return CreateSession(winner.Marker.Identity, request);
        }
    }

    private DateTimeOffset ValidateMultipartRequest(MultipartRequest request)
    {
        ValidateKey(request.Key);
        if (!Capabilities.SupportsMultipartUpload)
        {
            throw Unsupported(
                "The configured S3 profile does not support multipart upload.");
        }

        ValidateObjectLength(request.ContentLength);
        ValidateMetadata(request.Metadata);
        _ = TranslateMultipartConditions(request.Conditions);
        if (request.Checksum is not null)
        {
            _ = TranslateChecksums([request.Checksum]);
            if (!_options.Profile.MultipartFullObjectChecksumAlgorithms.Contains(
                    request.Checksum.Algorithm))
            {
                throw Unsupported(
                    "The configured S3 profile cannot validate that checksum as a full multipart object checksum.");
            }
        }

        return ValidateMultipartLifetimes(request);
    }

    private static MultipartSession CreateSession(
        S3MultipartIdentity identity,
        MultipartRequest request) =>
        new(
            identity.UploadId,
            request.Key,
            S3DurableMultipartState.ExpiresAt(identity),
            request.ContentLength,
            request.Conditions,
            identity.MaxParts,
            identity.MinPartBytes,
            identity.MaxPartBytes,
            request.PartPlanLifetime,
            request.ContentType,
            request.Checksum,
            request.Metadata,
            S3DurableMultipartState.Encode(identity));

    public async ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatedS3Session validated = ValidateSession(session);
        S3MarkerHandle marker = await RequireMarkerAsync(
            validated.Identity,
            cancellationToken);
        EnsureMarkerActive(marker.Marker);
        if (partNumber < 1 || partNumber > session.MaxParts)
        {
            throw Invalid("The multipart part number is outside the provider limit.");
        }

        DateTimeOffset expiresAt = PartPlanExpiry(session);
        Uri url = await PresignAsync(
            new S3PresignCommand(
                HttpMethodKind.Put,
                session.Key.Value,
                expiresAt,
                EmptyHeaders,
                EmptyParameters,
                session.UploadId,
                partNumber),
            cancellationToken);
        return new MultipartPartPlan(
            session.UploadId,
            partNumber,
            new SignedHttpRequest(HttpMethodKind.Put, url),
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
        ArgumentNullException.ThrowIfNull(parts);
        ValidatedS3Session validated = ValidateSession(
            session,
            requireActive: false);
        S3MarkerHandle marker = await RequireMarkerAsync(
            validated.Identity,
            cancellationToken);
        if (marker.Marker.Status == S3DurableMultipartState.Completed)
        {
            BlobHead? existing = await HeadAsync(
                session.Key,
                cancellationToken);
            if (existing is null)
            {
                throw OutcomeUnknown(
                    "The S3 multipart completion marker exists without its object.");
            }

            ValidateCompletedInventory(session, existing);
            return new MultipartCompletion(existing);
        }

        EnsureMarkerActive(marker.Marker);
        ValidateParts(session, parts);
        S3WireChecksum? checksum = session.Checksum is null
            ? null
            : TranslateChecksums([session.Checksum]).Single();
        List<S3CompletedPart> translated = new(parts.Count);
        foreach (UploadedPart part in parts)
        {
            if (part.Checksum is not null &&
                (session.Checksum is null ||
                 part.Checksum.Algorithm != session.Checksum.Algorithm))
            {
                throw Invalid(
                    "Multipart part checksums must match the session checksum algorithm.");
            }

            S3WireChecksum? partChecksum = part.Checksum is null
                ? null
                : TranslateChecksums([part.Checksum]).Single();
            translated.Add(
                new S3CompletedPart(
                    part.PartNumber,
                    part.EntityTag.Value,
                    partChecksum,
                    part.SizeBytes));
        }

        try
        {
            S3ObjectDescriptor descriptor =
                await _transport.CompleteMultipartAsync(
                    new S3CompleteMultipartCommand(
                        session.Key.Value,
                        session.UploadId,
                        translated.AsReadOnly(),
                        validated.CompletionConditions,
                        checksum),
                    cancellationToken);
            BlobHead head = ToHead(descriptor, session.Key);
            ValidateCompletedInventory(session, head);
            await UpdateMarkerStatusAsync(
                marker,
                S3DurableMultipartState.Completed,
                cancellationToken);
            return new MultipartCompletion(head);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    public async ValueTask AbortMultipartAsync(
        MultipartSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatedS3Session validated = ValidateSession(
            session,
            requireActive: false);
        S3MarkerHandle marker = await RequireMarkerAsync(
            validated.Identity,
            cancellationToken);
        if (marker.Marker.Status == S3DurableMultipartState.Aborted)
        {
            return;
        }

        EnsureMarkerActive(marker.Marker);
        try
        {
            await _transport.AbortMultipartAsync(
                session.Key.Value,
                session.UploadId,
                cancellationToken);
            await UpdateMarkerStatusAsync(
                marker,
                S3DurableMultipartState.Aborted,
                cancellationToken);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    public async ValueTask<SignedAccessPlan> CreateReadGrantAsync(
        BlobKey key,
        ReadGrantOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(options);
        if (!Capabilities.SupportsSignedRead)
        {
            throw Unsupported("The configured S3 profile does not support signed reads.");
        }

        DateTimeOffset expiresAt = ValidateLifetime(options.Lifetime);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        if (options.Range is not null)
        {
            headers["Range"] = string.Create(
                CultureInfo.InvariantCulture,
                $"bytes={options.Range.Offset}-{checked(options.Range.Offset + options.Range.Length - 1)}");
        }

        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        if (options.DownloadFileName is not null)
        {
            parameters["response-content-disposition"] =
                $"attachment; filename=\"{EscapeFileName(options.DownloadFileName)}\"";
        }

        Uri url = await PresignAsync(
            new S3PresignCommand(
                HttpMethodKind.Get,
                key.Value,
                expiresAt,
                ReadOnly(headers),
                ReadOnly(parameters)),
            cancellationToken);
        return new SignedAccessPlan(
            key,
            new SignedHttpRequest(HttpMethodKind.Get, url, headers),
            expiresAt,
            options.Range);
    }

    private async ValueTask<Uri> PresignAsync(
        S3PresignCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.PresignAsync(command, cancellationToken);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    private S3Conditions TranslateWriteConditions(BlobRequestConditions conditions)
    {
        if (conditions.HasPrecondition &&
            !(Capabilities.SupportsConditionalCreate &&
              Capabilities.SupportsConditionalReplace))
        {
            throw Unsupported(
                "The configured S3 profile cannot guarantee atomic conditional writes.");
        }

        return TranslateConditions(conditions, allowRequireMissing: true);
    }

    private S3Conditions TranslateMultipartConditions(BlobRequestConditions conditions)
    {
        if (conditions.HasPrecondition &&
            !Capabilities.SupportsConditionalMultipartCompletion)
        {
            throw Unsupported(
                "The configured S3 profile cannot guarantee conditional multipart completion.");
        }

        return TranslateConditions(conditions, allowRequireMissing: true);
    }

    private static S3Conditions TranslateReadConditions(
        BlobRequestConditions conditions) =>
        TranslateConditions(conditions, allowRequireMissing: false);

    private static S3Conditions TranslateConditions(
        BlobRequestConditions conditions,
        bool allowRequireMissing)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.RequireMissing && !allowRequireMissing)
        {
            throw Unsupported("This S3 operation cannot atomically require a missing object.");
        }

        string? ifMatch = MatchValue(conditions);
        return new S3Conditions(ifMatch, conditions.RequireMissing);
    }

    private static string? TranslateMatchOnlyConditions(
        BlobRequestConditions conditions,
        string operation)
    {
        if (conditions.RequireMissing)
        {
            throw Unsupported($"{operation} cannot atomically require a missing object.");
        }

        return MatchValue(conditions);
    }

    private static string? MatchValue(BlobRequestConditions conditions)
    {
        if (conditions.IfMatch is not null &&
            conditions.IfEntityTagMatch is not null &&
            !string.Equals(
                conditions.IfMatch.Value,
                conditions.IfEntityTagMatch.Value,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "S3 version and entity-tag preconditions must identify the same object.");
        }

        return conditions.IfEntityTagMatch?.Value ?? conditions.IfMatch?.Value;
    }

    private ReadOnlyCollection<S3WireChecksum> TranslateChecksums(
        IEnumerable<BlobChecksum> checksums)
    {
        List<S3WireChecksum> translated = [];
        foreach (BlobChecksum checksum in checksums)
        {
            if (!Capabilities.NativeChecksumAlgorithms.Contains(checksum.Algorithm))
            {
                throw Unsupported(
                    $"Checksum algorithm '{checksum.Algorithm}' is not supported by the configured S3 profile.");
            }

            translated.Add(
                new S3WireChecksum(
                    checksum.Algorithm,
                    ToWireChecksum(checksum)));
        }

        return translated.AsReadOnly();
    }

    private static string ToWireChecksum(BlobChecksum checksum)
    {
        if (checksum.Algorithm == BlobChecksumAlgorithm.Sha256)
        {
            return Convert.ToBase64String(Convert.FromHexString(checksum.Value));
        }

        if (checksum.Algorithm == BlobChecksumAlgorithm.Md5 &&
            checksum.Value.Length == 32 &&
            checksum.Value.All(Uri.IsHexDigit))
        {
            return Convert.ToBase64String(Convert.FromHexString(checksum.Value));
        }

        int expectedBytes = checksum.Algorithm switch
        {
            BlobChecksumAlgorithm.Md5 => 16,
            BlobChecksumAlgorithm.Crc32 or BlobChecksumAlgorithm.Crc32C => 4,
            BlobChecksumAlgorithm.Crc64Nvme => 8,
            _ => throw Unsupported("The checksum cannot be represented by the S3 API."),
        };
        try
        {
            byte[] bytes = Convert.FromBase64String(checksum.Value);
            if (bytes.Length != expectedBytes)
            {
                throw new FormatException();
            }

            return checksum.Value;
        }
        catch (FormatException error)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The checksum value is not valid for the selected S3 algorithm.",
                error);
        }
    }

    private static string ChecksumHeader(BlobChecksumAlgorithm algorithm) =>
        algorithm switch
        {
            BlobChecksumAlgorithm.Sha256 => "x-amz-checksum-sha256",
            BlobChecksumAlgorithm.Md5 => "Content-MD5",
            BlobChecksumAlgorithm.Crc32 => "x-amz-checksum-crc32",
            BlobChecksumAlgorithm.Crc32C => "x-amz-checksum-crc32c",
            BlobChecksumAlgorithm.Crc64Nvme => "x-amz-checksum-crc64nvme",
            _ => throw Unsupported("The checksum cannot be represented by the S3 API."),
        };

    private static void AddConditionHeaders(
        Dictionary<string, string> headers,
        S3Conditions conditions)
    {
        if (conditions.RequireMissing)
        {
            headers["If-None-Match"] = "*";
        }

        if (conditions.IfMatch is not null)
        {
            headers["If-Match"] = conditions.IfMatch;
        }
    }

    private DateTimeOffset ValidateLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero ||
            lifetime > _options.MaximumPresignLifetime ||
            lifetime > TimeSpan.FromDays(7))
        {
            throw Invalid("The signed request lifetime exceeds the configured limit.");
        }

        return _timeProvider.GetUtcNow().Add(lifetime);
    }

    private DateTimeOffset ValidateMultipartLifetimes(MultipartRequest request)
    {
        if (request.SessionLifetime > TimeSpan.FromDays(7))
        {
            throw Invalid("The multipart upload session lifetime exceeds seven days.");
        }

        _ = ValidateLifetime(request.PartPlanLifetime);
        return _timeProvider.GetUtcNow().Add(request.SessionLifetime);
    }

    private DateTimeOffset PartPlanExpiry(MultipartSession session)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now.Add(session.PartPlanLifetime);
        if (expiresAt > session.ExpiresAtUtc)
        {
            expiresAt = session.ExpiresAtUtc;
        }

        if (expiresAt <= now)
        {
            throw Invalid("The multipart session has expired.");
        }

        return expiresAt;
    }

    private void ValidateObjectLength(long contentLength)
    {
        if (contentLength <= 0 || contentLength > Capabilities.Limits.MaxObjectBytes)
        {
            throw Invalid("The declared S3 object length is outside the provider limit.");
        }
    }

    private ValidatedS3Session ValidateSession(
        MultipartSession session,
        bool requireActive = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateKey(session.Key);
        ValidateObjectLength(session.ContentLength);
        ValidateMetadata(session.Metadata);
        S3MultipartIdentity identity =
            S3DurableMultipartState.Decode(session.ProviderState);
        S3DurableMultipartState.ValidateConfiguration(identity, _options);
        S3DurableMultipartState.ValidateSession(identity, session);
        if (session.MaxParts != Capabilities.Limits.MaxMultipartParts ||
            session.MinPartBytes != Capabilities.Limits.MinMultipartPartBytes ||
            session.MaxPartBytes != Capabilities.Limits.MaxMultipartPartBytes ||
            (requireActive &&
             (session.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
              session.PartPlanLifetime > _options.MaximumPresignLifetime ||
              session.PartPlanLifetime > TimeSpan.FromDays(7))))
        {
            throw Invalid("The multipart session is expired or inconsistent.");
        }

        if (session.Checksum is not null)
        {
            _ = TranslateChecksums([session.Checksum]);
            if (!_options.Profile.MultipartFullObjectChecksumAlgorithms.Contains(
                    session.Checksum.Algorithm))
            {
                throw Unsupported(
                    "The configured S3 profile cannot validate the multipart session checksum.");
            }
        }

        return new ValidatedS3Session(
            identity,
            TranslateMultipartConditions(session.CompletionConditions));
    }

    private async ValueTask<S3MarkerHandle> RequireMarkerAsync(
        S3MultipartIdentity identity,
        CancellationToken cancellationToken)
    {
        S3MarkerHandle? handle = await ReadMarkerAsync(
            identity.MarkerKey,
            cancellationToken);
        if (handle is null)
        {
            throw Invalid(
                "The durable S3 multipart provider state is no longer recognized.");
        }

        S3DurableMultipartState.ValidateMarkerIdentity(
            identity,
            handle.Marker.Identity);
        return handle;
    }

    private async ValueTask<S3MarkerHandle?> ReadMarkerAsync(
        string markerKey,
        CancellationToken cancellationToken)
    {
        S3ReadResult result;
        try
        {
            result = await _transport.GetAsync(
                new S3GetCommand(markerKey, null, S3Conditions.None),
                cancellationToken);
        }
        catch (S3TransportException error) when (
            error.Error == S3TransportError.NotFound)
        {
            return null;
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }

        await using Stream content = result.Content;
        if (result.ContentRange is not null ||
            !string.Equals(
                result.Descriptor.Key,
                markerKey,
                StringComparison.Ordinal) ||
            result.Descriptor.ContentLength is <= 0 or > 16 * 1024)
        {
            throw Invalid(
                "The durable S3 multipart control record is invalid.");
        }

        byte[] bytes = new byte[checked((int)result.Descriptor.ContentLength)];
        try
        {
            await content.ReadExactlyAsync(bytes, cancellationToken);
            if (await content.ReadAsync(
                    new byte[1],
                    cancellationToken) != 0)
            {
                throw Invalid(
                    "The durable S3 multipart control record is invalid.");
            }
        }
        catch (EndOfStreamException error)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The durable S3 multipart control record was truncated.",
                error);
        }

        return new S3MarkerHandle(
            S3DurableMultipartState.DecodeMarker(bytes),
            result.Descriptor.EntityTag);
    }

    private async ValueTask<S3MarkerHandle> WriteMarkerAsync(
        S3MultipartMarker marker,
        S3Conditions conditions,
        CancellationToken cancellationToken)
    {
        byte[] bytes = S3DurableMultipartState.EncodeMarker(marker);
        using MemoryStream content = new(bytes, writable: false);
        try
        {
            S3ObjectDescriptor descriptor = await _transport.PutAsync(
                new S3PutCommand(
                    marker.Identity.MarkerKey,
                    content,
                    bytes.LongLength,
                    "application/vnd.vistara.multipart-state+json",
                    new ReadOnlyDictionary<string, string>(
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)),
                    [],
                    conditions),
                cancellationToken);
            if (!string.Equals(
                    descriptor.Key,
                    marker.Identity.MarkerKey,
                    StringComparison.Ordinal))
            {
                throw new BlobStoreException(
                    BlobStoreErrorCode.IntegrityMismatch,
                    "The S3 provider returned a different multipart control key.");
            }

            return new S3MarkerHandle(marker, descriptor.EntityTag);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    private async ValueTask UpdateMarkerStatusAsync(
        S3MarkerHandle current,
        string status,
        CancellationToken cancellationToken)
    {
        if (current.Marker.Status == status)
        {
            return;
        }

        if (current.Marker.Status != S3DurableMultipartState.Active)
        {
            throw Invalid(
                "The durable S3 multipart control record is not active.");
        }

        var updated = current.Marker with
        {
            Status = status,
        };
        try
        {
            _ = await WriteMarkerAsync(
                updated,
                new S3Conditions(current.EntityTag, RequireMissing: false),
                cancellationToken);
        }
        catch (BlobStoreException error)
            when (error.Code == BlobStoreErrorCode.PreconditionFailed)
        {
            S3MarkerHandle observed;
            try
            {
                observed = await RequireMarkerAsync(
                    current.Marker.Identity,
                    cancellationToken);
            }
            catch (BlobStoreException lookupError)
            {
                throw OutcomeUnknown(
                    "The durable S3 multipart status update could not be reconciled.",
                    lookupError);
            }

            if (observed.Marker.Status != status)
            {
                throw OutcomeUnknown(
                    "The durable S3 multipart status update raced another operation.",
                    error);
            }
        }
    }

    private static void EnsureMarkerActive(S3MultipartMarker marker)
    {
        if (marker.Status != S3DurableMultipartState.Active)
        {
            throw Invalid(
                "The durable S3 multipart session is no longer active.");
        }
    }

    private static ReadOnlyCollection<UploadedPart> TranslateUploadedParts(
        MultipartSession session,
        IReadOnlyList<S3UploadedPartDescriptor> descriptors)
    {
        var parts = new List<UploadedPart>(descriptors.Count);
        int previous = 0;
        foreach (S3UploadedPartDescriptor descriptor in descriptors)
        {
            if (descriptor.PartNumber <= previous ||
                descriptor.PartNumber > session.MaxParts ||
                descriptor.SizeBytes <= 0 ||
                descriptor.SizeBytes > session.MaxPartBytes)
            {
                throw new BlobStoreException(
                    BlobStoreErrorCode.IntegrityMismatch,
                    "The S3 provider returned invalid multipart inventory.");
            }

            previous = descriptor.PartNumber;
            try
            {
                BlobChecksum? checksum = descriptor.Checksums.Count == 0
                    ? null
                    : new BlobChecksum(
                        descriptor.Checksums[0].Algorithm,
                        descriptor.Checksums[0].Value);
                parts.Add(new UploadedPart(
                    descriptor.PartNumber,
                    new BlobEntityTag(descriptor.EntityTag),
                    checksum,
                    descriptor.SizeBytes));
            }
            catch (ArgumentException error)
            {
                throw new BlobStoreException(
                    BlobStoreErrorCode.IntegrityMismatch,
                    "The S3 provider returned invalid multipart inventory.",
                    error);
            }
        }

        return parts.AsReadOnly();
    }

    private static void ValidateCompletedInventory(
        MultipartSession session,
        BlobHead head)
    {
        if (head.Identity.Key != session.Key ||
            head.Properties.ContentLength != session.ContentLength ||
            head.Properties.ContentType != session.ContentType ||
            session.Metadata.AsReadOnly().Any(pair =>
                !head.Properties.Metadata.TryGetValue(
                    pair.Key,
                    out string? value) ||
                !string.Equals(value, pair.Value, StringComparison.Ordinal)) ||
            (session.Checksum is not null &&
             !head.Properties.Checksums.Contains(session.Checksum)))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The completed S3 multipart object does not match its session.");
        }
    }

    private void ValidateParts(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts)
    {
        if (parts.Count == 0 || parts.Count > session.MaxParts)
        {
            throw Invalid("The multipart completion part count is invalid.");
        }

        long total = 0;
        int previous = 0;
        long? uniformPartSize = null;
        for (int index = 0; index < parts.Count; index++)
        {
            UploadedPart part = parts[index];
            if (part.PartNumber <= previous ||
                part.PartNumber > session.MaxParts ||
                part.SizeBytes > session.MaxPartBytes ||
                (index < parts.Count - 1 && part.SizeBytes < session.MinPartBytes))
            {
                throw Invalid(
                    "Multipart parts must be ordered and satisfy provider size limits.");
            }

            previous = part.PartNumber;
            total = checked(total + part.SizeBytes);
            if (_options.Profile.RequiresUniformMultipartParts &&
                index < parts.Count - 1)
            {
                uniformPartSize ??= part.SizeBytes;
                if (part.SizeBytes != uniformPartSize)
                {
                    throw Invalid(
                        "The configured S3 profile requires equal non-final multipart part sizes.");
                }
            }
        }

        if (_options.Profile.RequiresUniformMultipartParts &&
            uniformPartSize is not null &&
            parts[^1].SizeBytes > uniformPartSize)
        {
            throw Invalid(
                "The configured S3 profile requires the final multipart part to be no larger than preceding parts.");
        }

        if (total != session.ContentLength)
        {
            throw Invalid(
                "Multipart part sizes must exactly match the declared object length.");
        }
    }

    private static BlobHead ToHead(
        S3ObjectDescriptor descriptor,
        BlobKey? expectedKey = null)
    {
        try
        {
            BlobKey key = new(descriptor.Key);
            if (expectedKey is not null && key != expectedKey)
            {
                throw new ArgumentException(
                    "The S3 response key did not match the requested key.");
            }

            BlobEntityTag entityTag = new(descriptor.EntityTag);
            BlobVersion version = new(descriptor.EntityTag);
            BlobMetadata metadata = new(descriptor.Metadata);
            BlobChecksum[] checksums = descriptor.Checksums
                .Select(checksum => new BlobChecksum(
                    checksum.Algorithm,
                    checksum.Value))
                .ToArray();
            BlobProperties properties = new(
                descriptor.ContentLength,
                new BlobMediaType(descriptor.ContentType),
                descriptor.LastModifiedUtc.ToUniversalTime(),
                version,
                entityTag,
                checksums,
                metadata);
            return new BlobHead(new BlobIdentity(key, version), properties);
        }
        catch (Exception error) when (
            error is ArgumentException or OverflowException)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The S3 provider returned invalid object metadata.",
                error);
        }
    }

    private static void ValidateKey(BlobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (S3DurableMultipartState.IsControlKey(key.Value))
        {
            throw Invalid("The S3 blob key uses a reserved internal prefix.");
        }
    }

    private static void ValidatePrefix(string? prefix)
    {
        if (prefix is null or "")
        {
            return;
        }

        if (prefix.Length > 1_024 ||
            S3DurableMultipartState.IsControlKey(prefix) ||
            prefix[0] == '/' ||
            prefix.Contains("//", StringComparison.Ordinal) ||
            prefix.Split('/').Any(segment => segment is "." or "..") ||
            prefix.Any(character =>
                character > 127 ||
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '/' or '.' or '_' or '-')))
        {
            throw Invalid("The S3 listing prefix is invalid.");
        }
    }

    private static void ValidateMetadata(BlobMetadata metadata)
    {
        foreach (KeyValuePair<string, string> pair in metadata.AsReadOnly())
        {
            if (pair.Value.Any(character =>
                    character is '\r' or '\n' or '\0' ||
                    char.IsControl(character)))
            {
                throw Invalid(
                    "S3 metadata values cannot contain control characters.");
            }
        }
    }

    private static string EscapeFileName(string value) =>
        value.Replace("\\", "_", StringComparison.Ordinal)
            .Replace("\"", "_", StringComparison.Ordinal)
            .Replace("\r", "_", StringComparison.Ordinal)
            .Replace("\n", "_", StringComparison.Ordinal);

    private static ReadOnlyDictionary<string, string> ReadOnly(
        Dictionary<string, string> values) =>
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(values, values.Comparer));

    private static IReadOnlyDictionary<string, string> EmptyHeaders { get; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> EmptyParameters { get; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static BlobStoreException Map(S3TransportException error) =>
        new(
            error.Error switch
            {
                S3TransportError.Unsupported => BlobStoreErrorCode.Unsupported,
                S3TransportError.NotFound => BlobStoreErrorCode.NotFound,
                S3TransportError.PreconditionFailed =>
                    BlobStoreErrorCode.PreconditionFailed,
                S3TransportError.InvalidRange => BlobStoreErrorCode.InvalidRange,
                S3TransportError.IntegrityMismatch =>
                    BlobStoreErrorCode.IntegrityMismatch,
                S3TransportError.InvalidRequest => BlobStoreErrorCode.InvalidRequest,
                S3TransportError.OutcomeUnknown => BlobStoreErrorCode.OutcomeUnknown,
                _ => BlobStoreErrorCode.OutcomeUnknown,
            },
            "The S3 provider could not complete the requested storage operation.",
            error);

    private static BlobStoreException Unsupported(string message) =>
        new(BlobStoreErrorCode.Unsupported, message);

    private static BlobStoreException Invalid(string message) =>
        new(BlobStoreErrorCode.InvalidRequest, message);

    private static BlobStoreException OutcomeUnknown(
        string message,
        Exception? error = null) =>
        new(BlobStoreErrorCode.OutcomeUnknown, message, error);

    private static Dependencies CreateDependencies(
        S3BlobStoreOptions options,
        AWSCredentials credentials,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        S3ValidatedOptions validated = options.Validate();
        return new Dependencies(
            validated,
            AwsS3Transport.Create(validated, credentials),
            timeProvider ?? TimeProvider.System);
    }

    private sealed record Dependencies(
        S3ValidatedOptions Options,
        IS3Transport Transport,
        TimeProvider TimeProvider);

    private sealed record ValidatedS3Session(
        S3MultipartIdentity Identity,
        S3Conditions CompletionConditions);

    private sealed record S3MarkerHandle(
        S3MultipartMarker Marker,
        string EntityTag);
}
