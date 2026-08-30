using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.ConformanceTests.Fixtures;

public sealed class InMemoryBlobStoreFixture : IBlobStoreFixture
{
    private readonly InMemoryBlobStore _store;

    private InMemoryBlobStoreFixture(InMemoryBlobStoreFault fault, bool conditionalCreate)
    {
        _store = new InMemoryBlobStore(fault, conditionalCreate);
    }

    public IBlobStore Store => _store;

    public static DateTimeOffset ContractTimestamp => InMemoryBlobStore.Timestamp;

    public static InMemoryBlobStoreFixture Create() =>
        new(InMemoryBlobStoreFault.None, conditionalCreate: true);

    public static InMemoryBlobStoreFixture CreateWithoutConditionalCreate() =>
        new(InMemoryBlobStoreFault.None, conditionalCreate: false);

    internal static InMemoryBlobStoreFixture CreateAdversarial(
        InMemoryBlobStoreFault fault,
        bool conditionalCreate = true) =>
        new(fault, conditionalCreate);

    public BlobKey Key(string suffix) => new($"contract/{suffix}");

    public IReplayableBlobContent Content(string value) =>
        new TrackingReplayableContent(value);

    public TrackingReplayableContent TrackingContent(string value) => new(value);

    public async ValueTask SeedAsync(BlobKey key, string value)
    {
        await _store.PutAsync(
            key,
            Content(value),
            new BlobWriteOptions(contentType: new BlobMediaType("text/plain")),
            CancellationToken.None);
    }

    public async ValueTask<string> ReadTextAsync(BlobKey key)
    {
        await using BlobReadHandle handle = await _store.OpenReadAsync(
            key,
            BlobReadOptions.Full,
            CancellationToken.None);
        using StreamReader reader = new(handle.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    public DirectUploadRequest DirectRequest(BlobKey key) =>
        new(
            key,
            8,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(10),
            BlobMetadata.Empty);

    public MultipartRequest MultipartRequest(BlobKey key) =>
        new(
            key,
            8,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(10),
            BlobMetadata.Empty);

    public MultipartSession Session(BlobKey key) =>
        new(
            "session-cancel",
            key,
            InMemoryBlobStore.Timestamp.AddMinutes(10),
            8,
            BlobRequestConditions.CreateOnly,
            10,
            1,
            8);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal enum InMemoryBlobStoreFault
{
    None,
    IgnorePreconditions,
    ReuseContentStream,
    IncorrectRange,
    IncorrectChecksum,
    FallbackWhenUnsupported,
    ReorderMultipartParts,
    IgnoreCancellation,
}

internal sealed class InMemoryBlobStore : IBlobStore
{
    private readonly Dictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _multipartSessions = new(StringComparer.Ordinal);
    private readonly InMemoryBlobStoreFault _fault;
    private byte[]? _reusedContent;
    private long _version;

    public InMemoryBlobStore(InMemoryBlobStoreFault fault, bool conditionalCreate)
    {
        _fault = fault;
        Capabilities = new BlobStoreCapabilities
        {
            SupportsDirectUpload = true,
            SupportsMultipartUpload = true,
            SupportsRangeReads = true,
            SupportsConditionalRead = true,
            SupportsConditionalCreate = conditionalCreate,
            SupportsConditionalReplace = true,
            SupportsConditionalCopy = true,
            SupportsConditionalDelete = true,
            SupportsConditionalMultipartCompletion = true,
            SupportsServerSideCopy = true,
            SupportsObjectVersioning = true,
            SupportsSignedRead = true,
            ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
            ListAfterWriteConsistency = BlobConsistencyModel.Strong,
            NativeChecksumAlgorithms = [BlobChecksumAlgorithm.Sha256],
            Limits = new BlobStoreLimits(1_048_576, 1_024, 10, 1, 1_048_576),
        };
    }

    public static DateTimeOffset Timestamp { get; } =
        new(2026, 8, 28, 21, 0, 0, TimeSpan.Zero);

    public string Name => "contract-memory";

    public BlobStoreCapabilities Capabilities { get; }

    public ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(key);
        return ValueTask.FromResult(
            _blobs.TryGetValue(key.Value, out StoredBlob? stored)
                ? CreateHead(key, stored)
                : null);
    }

    public ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key,
        BlobReadOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        StoredBlob stored = Get(key);
        CheckConditions(
            key,
            options.EffectiveConditions,
            Capabilities.SupportsConditionalRead);
        BlobRange? requested = options.Range;
        if (requested is not null && !Capabilities.SupportsRangeReads)
        {
            throw Unsupported("Range reads are not supported.");
        }

        byte[] content = stored.Content;
        BlobContentRange? returnedRange = null;
        if (requested is not null &&
            _fault != InMemoryBlobStoreFault.IncorrectRange)
        {
            if (requested.Offset >= content.LongLength ||
                checked(requested.Offset + requested.Length) > content.LongLength)
            {
                throw new BlobStoreException(
                    BlobStoreErrorCode.InvalidRange,
                    "The requested range is outside the object.");
            }

            content = content
                .AsSpan((int)requested.Offset, (int)requested.Length)
                .ToArray();
            returnedRange = new BlobContentRange(
                requested.Offset,
                requested.Length,
                stored.Content.LongLength);
        }
        else if (requested is not null)
        {
            returnedRange = new BlobContentRange(0, content.LongLength, content.LongLength);
        }

        return ValueTask.FromResult(
            new BlobReadHandle(
                new MemoryStream(content, writable: false),
                CreateHead(key, stored),
                returnedRange));
    }

    public async ValueTask<BlobWriteResult> PutAsync(
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        CheckConditions(
            key,
            options.Conditions,
            CapabilityForWrite(options.Conditions));

        byte[] bytes;
        if (_fault == InMemoryBlobStoreFault.ReuseContentStream &&
            _reusedContent is not null)
        {
            bytes = _reusedContent.ToArray();
        }
        else
        {
            await using Stream source = await content.OpenReadAsync(cancellationToken);
            using MemoryStream destination = new();
            await source.CopyToAsync(destination, cancellationToken);
            bytes = destination.ToArray();
            if (_fault == InMemoryBlobStoreFault.ReuseContentStream)
            {
                _reusedContent = bytes.ToArray();
            }
        }

        if (bytes.LongLength != content.Length)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "Replayable content length does not match the bytes read.");
        }

        BlobChecksum actualChecksum = Sha256(bytes);
        BlobChecksum? expectedChecksum = options.Checksums.SingleOrDefault(
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);
        if (expectedChecksum is not null && expectedChecksum != actualChecksum)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The uploaded bytes do not match the expected checksum.");
        }

        bool created = !_blobs.ContainsKey(key.Value);
        StoredBlob stored = new(
            bytes,
            options.ContentType ?? new BlobMediaType("application/octet-stream"),
            options.Metadata,
            NextVersion());
        _blobs[key.Value] = stored;
        return new BlobWriteResult(CreateHead(key, stored), created);
    }

    public ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (!Capabilities.SupportsServerSideCopy)
        {
            throw Unsupported("Server-side copy is not supported.");
        }

        StoredBlob sourceBlob = Get(source);
        CheckConditions(
            source,
            options.EffectiveSourceConditions,
            Capabilities.SupportsConditionalCopy);
        CheckConditions(
            destination,
            options.EffectiveDestinationConditions,
            Capabilities.SupportsConditionalCopy);
        StoredBlob destinationBlob = new(
            sourceBlob.Content.ToArray(),
            sourceBlob.ContentType,
            options.ReplacementMetadata ?? sourceBlob.Metadata,
            NextVersion());
        _blobs[destination.Value] = destinationBlob;
        return ValueTask.FromResult(
            new BlobCopyResult(
                CreateHead(destination, destinationBlob),
                CreateHead(source, sourceBlob).Identity));
    }

    public ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        CheckConditions(
            key,
            options.EffectiveConditions,
            Capabilities.SupportsConditionalDelete);
        if (!_blobs.Remove(key.Value, out StoredBlob? stored))
        {
            return ValueTask.FromResult(new BlobDeleteResult(false, null));
        }

        return ValueTask.FromResult(
            new BlobDeleteResult(true, CreateHead(key, stored).Identity));
    }

    public async IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(options);
        foreach ((string value, StoredBlob stored) in _blobs
                     .Where(pair =>
                         options.Prefix is null ||
                         pair.Key.StartsWith(options.Prefix, StringComparison.Ordinal))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ThrowIfCancelled(cancellationToken);
            await Task.Yield();
            yield return CreateHead(new BlobKey(value), stored);
        }
    }

    public ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        if (!Capabilities.SupportsDirectUpload)
        {
            throw Unsupported("Direct uploads are not supported.");
        }

        CheckConditions(
            request.Key,
            request.Conditions,
            CapabilityForWrite(request.Conditions));
        return ValueTask.FromResult(
            new DirectUploadPlan(
                request.Key,
                Signed(HttpMethodKind.Put, request.Key),
                Timestamp.Add(request.Lifetime),
                request.Conditions,
                request.Checksum));
    }

    public ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        if (!Capabilities.SupportsMultipartUpload)
        {
            throw Unsupported("Multipart uploads are not supported.");
        }

        CheckConditions(
            request.Key,
            request.Conditions,
            CapabilityForWrite(request.Conditions));
        string uploadId = $"upload-{_multipartSessions.Count + 1}";
        _multipartSessions.Add(uploadId);
        return ValueTask.FromResult(
            new MultipartSession(
                uploadId,
                request.Key,
                Timestamp.Add(request.Lifetime),
                request.ContentLength,
                request.Conditions,
                Capabilities.Limits.MaxMultipartParts,
                Capabilities.Limits.MinMultipartPartBytes,
                Capabilities.Limits.MaxMultipartPartBytes));
    }

    public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(session);
        EnsureSession(session);
        if (partNumber < 1 || partNumber > session.MaxParts)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The part number is outside the session limits.");
        }

        return ValueTask.FromResult(
            new MultipartPartPlan(
                session.UploadId,
                partNumber,
                Signed(HttpMethodKind.Put, session.Key),
                session.MinPartBytes,
                session.MaxPartBytes,
                session.ExpiresAtUtc));
    }

    public ValueTask<MultipartCompletion> CompleteMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parts);
        EnsureSession(session);
        if (!Capabilities.SupportsConditionalMultipartCompletion &&
            session.CompletionConditions.HasPrecondition)
        {
            throw Unsupported("Conditional multipart completion is not supported.");
        }

        bool ordered = parts.Count > 0 &&
            parts.Select((part, index) => part.PartNumber == index + 1).All(value => value);
        if (!ordered && _fault != InMemoryBlobStoreFault.ReorderMultipartParts)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "Multipart parts must be contiguous and ordered from one.");
        }

        IReadOnlyList<UploadedPart> effectiveParts =
            _fault == InMemoryBlobStoreFault.ReorderMultipartParts
                ? parts.OrderBy(part => part.PartNumber).ToArray()
                : parts;
        if (effectiveParts.Sum(part => part.SizeBytes) != session.ContentLength)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "Multipart part sizes do not match the declared object size.");
        }

        CheckConditions(
            session.Key,
            session.CompletionConditions,
            Capabilities.SupportsConditionalMultipartCompletion);
        StoredBlob stored = new(
            new byte[checked((int)session.ContentLength)],
            new BlobMediaType("application/octet-stream"),
            BlobMetadata.Empty,
            NextVersion());
        _blobs[session.Key.Value] = stored;
        _multipartSessions.Remove(session.UploadId);
        return ValueTask.FromResult(
            new MultipartCompletion(CreateHead(session.Key, stored)));
    }

    public ValueTask AbortMultipartAsync(
        MultipartSession session,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(session);
        _multipartSessions.Remove(session.UploadId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
        BlobKey key,
        ReadGrantOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        if (!Capabilities.SupportsSignedRead)
        {
            throw Unsupported("Signed reads are not supported.");
        }

        _ = Get(key);
        return ValueTask.FromResult(
            new SignedAccessPlan(
                key,
                Signed(HttpMethodKind.Get, key),
                Timestamp.Add(options.Lifetime),
                options.Range));
    }

    private bool CapabilityForWrite(BlobRequestConditions conditions) =>
        conditions.RequireMissing
            ? Capabilities.SupportsConditionalCreate
            : conditions.IfMatch is not null || conditions.IfEntityTagMatch is not null
                ? Capabilities.SupportsConditionalReplace
                : true;

    private void CheckConditions(
        BlobKey key,
        BlobRequestConditions conditions,
        bool capability)
    {
        if (!conditions.HasPrecondition)
        {
            return;
        }

        if (!capability &&
            _fault != InMemoryBlobStoreFault.FallbackWhenUnsupported)
        {
            throw Unsupported("The requested condition is not supported.");
        }

        if (_fault is InMemoryBlobStoreFault.IgnorePreconditions or
            InMemoryBlobStoreFault.FallbackWhenUnsupported)
        {
            return;
        }

        bool exists = _blobs.TryGetValue(key.Value, out StoredBlob? stored);
        if (conditions.RequireMissing && exists)
        {
            throw PreconditionFailed();
        }

        if (conditions.IfMatch is not null &&
            (!exists || stored!.Version != conditions.IfMatch))
        {
            throw PreconditionFailed();
        }

        if (conditions.IfEntityTagMatch is not null &&
            (!exists ||
             new BlobEntityTag($"etag-{stored!.Version.Value}") !=
             conditions.IfEntityTagMatch))
        {
            throw PreconditionFailed();
        }
    }

    private BlobHead CreateHead(BlobKey key, StoredBlob stored)
    {
        BlobChecksum checksum = _fault == InMemoryBlobStoreFault.IncorrectChecksum
            ? new BlobChecksum(BlobChecksumAlgorithm.Sha256, new string('0', 64))
            : Sha256(stored.Content);
        BlobProperties properties = new(
            stored.Content.LongLength,
            stored.ContentType,
            Timestamp,
            stored.Version,
            new BlobEntityTag($"etag-{stored.Version.Value}"),
            [checksum],
            stored.Metadata);
        return new BlobHead(new BlobIdentity(key, stored.Version), properties);
    }

    private StoredBlob Get(BlobKey key) =>
        _blobs.TryGetValue(key.Value, out StoredBlob? stored)
            ? stored
            : throw new BlobStoreException(
                BlobStoreErrorCode.NotFound,
                "The object was not found.");

    private void EnsureSession(MultipartSession session)
    {
        if (!_multipartSessions.Contains(session.UploadId))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The multipart session is not active.");
        }
    }

    private BlobVersion NextVersion() =>
        new($"version-{Interlocked.Increment(ref _version)}");

    private void ThrowIfCancelled(CancellationToken cancellationToken)
    {
        if (_fault != InMemoryBlobStoreFault.IgnoreCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static BlobChecksum Sha256(byte[] content) =>
        new(
            BlobChecksumAlgorithm.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(content)));

    private static SignedHttpRequest Signed(HttpMethodKind method, BlobKey key) =>
        new(
            method,
            new Uri($"https://storage.invalid/{key.Value}?signature=redacted"),
            new Dictionary<string, string>
            {
                ["x-vistara-contract"] = "required",
            });

    private static BlobStoreException Unsupported(string message) =>
        new(BlobStoreErrorCode.Unsupported, message);

    private static BlobStoreException PreconditionFailed() =>
        new(
            BlobStoreErrorCode.PreconditionFailed,
            "The object did not satisfy the required precondition.");

    private sealed record StoredBlob(
        byte[] Content,
        BlobMediaType ContentType,
        BlobMetadata Metadata,
        BlobVersion Version);
}
