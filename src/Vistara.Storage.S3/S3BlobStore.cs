using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.S3;

public sealed class S3BlobStore : IBlobStore, IAsyncDisposable
{
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
        S3ReadResult read;
        try
        {
            read = await _transport.GetAsync(
                new S3GetCommand(source.Value, null, sourceConditions),
                cancellationToken);
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }

        await using Stream content = read.Content;
        BlobHead sourceHead = ToHead(read.Descriptor, source);
        if (read.ContentRange is not null ||
            sourceHead.Properties.ContentLength > _options.Profile.MaxSinglePutBytes)
        {
            throw Unsupported(
                "The S3 profile cannot stream this source through one atomic create-only request.");
        }

        BlobMetadata metadata =
            options.ReplacementMetadata ?? sourceHead.Properties.Metadata;
        ReadOnlyCollection<S3WireChecksum> checksums =
            TranslateChecksums(sourceHead.Properties.Checksums.Where(
                checksum => Capabilities.NativeChecksumAlgorithms.Contains(
                    checksum.Algorithm)));
        if (checksums.Count == 0)
        {
            throw Unsupported(
                "The S3 source lacks a native checksum required for streamed create-only publication.");
        }

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
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ValidateKey(request.Key);
        if (!Capabilities.SupportsMultipartUpload)
        {
            throw Unsupported("The configured S3 profile does not support multipart upload.");
        }

        ValidateObjectLength(request.ContentLength);
        ValidateMetadata(request.Metadata);
        _ = TranslateMultipartConditions(request.Conditions);
        BlobChecksumAlgorithm? checksumAlgorithm = request.Checksum?.Algorithm;
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

        DateTimeOffset expiresAt = ValidateMultipartLifetimes(request);
        try
        {
            string uploadId = await _transport.BeginMultipartAsync(
                new S3BeginMultipartCommand(
                    request.Key.Value,
                    request.ContentType.Value,
                    request.Metadata.AsReadOnly(),
                    checksumAlgorithm),
                cancellationToken);
            BlobStoreLimits limits = Capabilities.Limits;
            return new MultipartSession(
                uploadId,
                request.Key,
                expiresAt,
                request.ContentLength,
                request.Conditions,
                limits.MaxMultipartParts,
                limits.MinMultipartPartBytes,
                limits.MaxMultipartPartBytes,
                request.PartPlanLifetime,
                request.ContentType,
                request.Checksum,
                request.Metadata,
                ProviderState(uploadId));
        }
        catch (S3TransportException error)
        {
            throw Map(error);
        }
    }

    public async ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
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
        S3Conditions completionConditions =
            ValidateSession(session, requireActive: false);
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
                        completionConditions,
                        checksum),
                    cancellationToken);
            return new MultipartCompletion(ToHead(descriptor, session.Key));
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
        _ = ValidateSession(session, requireActive: false);
        try
        {
            await _transport.AbortMultipartAsync(
                session.Key.Value,
                session.UploadId,
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

    private S3Conditions ValidateSession(
        MultipartSession session,
        bool requireActive = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateKey(session.Key);
        ValidateObjectLength(session.ContentLength);
        ValidateMetadata(session.Metadata);
        if (!string.Equals(
                session.ProviderState,
                ProviderState(session.UploadId),
                StringComparison.Ordinal) ||
            session.MaxParts != Capabilities.Limits.MaxMultipartParts ||
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

        return TranslateMultipartConditions(session.CompletionConditions);
    }

    private static string ProviderState(string uploadId) => $"s3:v1:{uploadId}";

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
        _ = key.Value;
    }

    private static void ValidatePrefix(string? prefix)
    {
        if (prefix is null or "")
        {
            return;
        }

        if (prefix.Length > 1_024 ||
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
}
