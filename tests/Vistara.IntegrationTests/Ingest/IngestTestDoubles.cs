using System.Collections.Concurrent;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Assets;
using Vistara.Worker.Features.Ingest;

namespace Vistara.IntegrationTests.Ingest;

internal sealed class FakeIngestStatePort(IngestWorkItem work) : IIngestTransactionPort
{
    private readonly IngestCleanup _cleanup = new(
        new IngestCleanupToken(Guid.CreateVersion7().ToString("D")),
        work.StagingKey,
        work.ExpectedStagingVersion);

    internal IngestWorkItem Work { get; } = work;

    internal IngestWorkItem ReturnedWork { get; set; } = work;

    internal bool UseExactDuplicate { get; set; }

    internal bool IsActivated { get; private set; }

    internal bool IsRejected { get; private set; }

    internal bool ReservationConsumed { get; private set; }

    internal bool ReservationReleased { get; private set; }

    internal bool DerivativesEnqueued { get; private set; }

    internal bool OutboxEnqueued { get; private set; }

    internal bool PromotionOutcomeUnknownRecorded { get; private set; }

    internal bool StagingDeletionRecorded { get; private set; }

    internal int ActivationCount { get; private set; }

    internal int RejectionCount { get; private set; }

    internal IngestActivation? Activation { get; private set; }

    internal IngestRejection? Rejection { get; private set; }

    internal BlobKey CanonicalKey { get; } =
        new($"originals/aa/{work.Fence.TenantId:D}/{work.Fence.UploadSessionId:D}/1/original.png");

    public ValueTask<IngestLoadResult> LoadAndFenceAsync(
        Guid tenantId,
        Guid uploadSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tenantId != Work.Fence.TenantId ||
            uploadSessionId != Work.Fence.UploadSessionId)
        {
            return ValueTask.FromResult(IngestLoadResult.NotFound());
        }

        if (IsRejected)
        {
            return ValueTask.FromResult(IngestLoadResult.Rejected());
        }

        if (IsActivated)
        {
            return ValueTask.FromResult(
                StagingDeletionRecorded
                    ? IngestLoadResult.Completed()
                    : IngestLoadResult.Activated(_cleanup));
        }

        return ValueTask.FromResult(IngestLoadResult.Ready(ReturnedWork));
    }

    public ValueTask<IngestPromotionPlan> PlanPromotionAsync(
        IngestFence fence,
        VerifiedIngestObject verified,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertFence(fence);
        IngestPromotionMode mode = UseExactDuplicate
            ? IngestPromotionMode.ExistingExactBlob
            : IngestPromotionMode.PromoteCreateOnly;
        return ValueTask.FromResult(new IngestPromotionPlan(
            new IngestPromotionToken(_cleanup.Token.Value),
            mode,
            CanonicalKey));
    }

    public ValueTask RecordPromotionOutcomeUnknownAsync(
        IngestFence fence,
        IngestPromotionPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertFence(fence);
        PromotionOutcomeUnknownRecorded = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(
        IngestActivation activation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertFence(activation.Fence);
        if (!IsActivated)
        {
            Activation = activation;
            IsActivated = true;
            ReservationConsumed = activation.ConsumeReservation;
            DerivativesEnqueued = activation.EnqueueStandardDerivatives;
            OutboxEnqueued = activation.EnqueueOutbox;
            ActivationCount++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RejectAsync(
        IngestRejection rejection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertFence(rejection.Fence);
        if (!IsRejected)
        {
            Rejection = rejection;
            IsRejected = true;
            ReservationReleased = rejection.ReleaseReservation;
            RejectionCount++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteCleanupAsync(
        IngestCleanupToken cleanupToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cleanupToken != _cleanup.Token)
        {
            throw new InvalidOperationException("Unexpected cleanup token.");
        }

        StagingDeletionRecorded = true;
        return ValueTask.CompletedTask;
    }

    private void AssertFence(IngestFence fence)
    {
        if (fence != Work.Fence)
        {
            throw new InvalidOperationException("The ingest fence did not match.");
        }
    }
}

internal enum FakeCopyBehavior
{
    Success,
    PreconditionFailedAfterMatchingObject,
    PreconditionFailedWithDifferentObject,
    OutcomeUnknownAfterCopy,
}

internal sealed record FakeBlobObject(
    byte[] Bytes,
    BlobVersion Version,
    BlobMediaType ContentType,
    BlobMetadata Metadata,
    IReadOnlyList<BlobChecksum> Checksums);

internal sealed class FakeBlobStore : IBlobStore
{
    private static readonly DateTimeOffset ModifiedAtUtc =
        new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
    private readonly ConcurrentDictionary<BlobKey, FakeBlobObject> _objects = new();

    public string Name => "fake";

    public BlobStoreCapabilities Capabilities { get; } = new()
    {
        SupportsConditionalRead = true,
        SupportsConditionalCopy = true,
        SupportsConditionalCreate = true,
        SupportsConditionalDelete = true,
        SupportsServerSideCopy = true,
        ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
    };

    internal FakeCopyBehavior CopyBehavior { get; set; }

    internal int CopyCalls { get; private set; }

    internal BlobCopyOptions? LastCopyOptions { get; private set; }

    internal List<BlobKey> OpenedKeys { get; } = [];

    internal void Add(
        BlobKey key,
        byte[] bytes,
        BlobVersion version,
        BlobMediaType contentType,
        IReadOnlyDictionary<string, string> metadata,
        Sha256Checksum checksum) =>
        _objects[key] = new FakeBlobObject(
            bytes,
            version,
            contentType,
            new BlobMetadata(metadata),
            [new BlobChecksum(BlobChecksumAlgorithm.Sha256, checksum.Value)]);

    internal bool Contains(BlobKey key) => _objects.ContainsKey(key);

    internal FakeBlobObject Get(BlobKey key) => _objects[key];

    internal void Replace(BlobKey key, FakeBlobObject value) => _objects[key] = value;

    internal void Remove(BlobKey key) => _objects.TryRemove(key, out _);

    public ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _objects.TryGetValue(key, out FakeBlobObject? value)
                ? Head(key, value)
                : null);
    }

    public ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key,
        BlobReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(key, out FakeBlobObject? value))
        {
            throw new BlobStoreException(BlobStoreErrorCode.NotFound, "missing");
        }

        BlobRequestConditions conditions = options.EffectiveConditions;
        if (conditions.IfMatch is not null && conditions.IfMatch != value.Version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "changed");
        }

        OpenedKeys.Add(key);
        return ValueTask.FromResult(new BlobReadHandle(
            new NonBufferingReadStream(value.Bytes),
            Head(key, value)));
    }

    public ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CopyCalls++;
        LastCopyOptions = options;
        FakeBlobObject sourceObject = _objects[source];
        if (options.EffectiveSourceConditions.IfMatch != sourceObject.Version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "source changed");
        }

        FakeBlobObject copied = sourceObject with
        {
            Version = new BlobVersion("canonical-v1"),
            Metadata = options.ReplacementMetadata ?? sourceObject.Metadata,
        };
        switch (CopyBehavior)
        {
            case FakeCopyBehavior.Success:
                if (!_objects.TryAdd(destination, copied))
                {
                    throw new BlobStoreException(
                        BlobStoreErrorCode.PreconditionFailed,
                        "destination exists");
                }

                return ValueTask.FromResult(new BlobCopyResult(
                    Head(destination, copied),
                    new BlobIdentity(source, sourceObject.Version)));
            case FakeCopyBehavior.PreconditionFailedAfterMatchingObject:
                _objects[destination] = copied;
                throw new BlobStoreException(
                    BlobStoreErrorCode.PreconditionFailed,
                    "destination exists");
            case FakeCopyBehavior.PreconditionFailedWithDifferentObject:
                _objects[destination] = copied with
                {
                    Bytes = "different-canonical-object"u8.ToArray(),
                    Checksums = [],
                };
                throw new BlobStoreException(
                    BlobStoreErrorCode.PreconditionFailed,
                    "destination exists");
            case FakeCopyBehavior.OutcomeUnknownAfterCopy:
                _objects[destination] = copied;
                throw new BlobStoreException(
                    BlobStoreErrorCode.OutcomeUnknown,
                    "ambiguous");
            default:
                throw new InvalidOperationException("The fake copy behavior is invalid.");
        }
    }

    public ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(key, out FakeBlobObject? value))
        {
            return ValueTask.FromResult(new BlobDeleteResult(false, null));
        }

        if (options.EffectiveConditions.IfMatch is not null &&
            options.EffectiveConditions.IfMatch != value.Version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "changed");
        }

        _objects.TryRemove(key, out _);
        return ValueTask.FromResult(new BlobDeleteResult(
            true,
            new BlobIdentity(key, value.Version)));
    }

    public ValueTask<BlobWriteResult> PutAsync(
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<MultipartCompletion> CompleteMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask AbortMultipartAsync(
        MultipartSession session,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
        BlobKey key,
        ReadGrantOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    private static BlobHead Head(BlobKey key, FakeBlobObject value) =>
        new(
            new BlobIdentity(key, value.Version),
            new BlobProperties(
                value.Bytes.LongLength,
                value.ContentType,
                ModifiedAtUtc,
                value.Version,
                new BlobEntityTag($"etag-{value.Version.Value}"),
                value.Checksums,
                value.Metadata));
}

internal sealed class FakeImageProcessor(long encodedBytes) : IImageProcessor
{
    internal ImageProcessorErrorCode? Error { get; set; }

    public ImageProcessorCapabilities Capabilities { get; } = new()
    {
        InputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
        MaxFrames = 1,
        StreamRequirements = new(false, false),
    };

    public ImagePipelineFingerprint PipelineFingerprint { get; } = new("fake-pipeline");

    public async ValueTask<ImageInspection> InspectAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Error.HasValue)
        {
            throw new ImageProcessorException(Error.Value, "unsafe decoder details");
        }

        await using Stream stream = await source.OpenReadAsync(cancellationToken);
        byte[] scratch = new byte[7];
        while (await stream.ReadAsync(scratch, cancellationToken) > 0)
        {
        }

        return new ImageInspection(
            ImageFormat.Png,
            new ImageMediaType("image/png"),
            width: 17,
            height: 11,
            frameCount: 1,
            aggregatePixels: 187,
            ImagePixelFormat.Rgba8,
            ImageOrientation.Normal,
            new ImagePrivacyMetadata(
                HasExif: true,
                HasGps: true,
                HasXmp: false,
                HasIptc: false,
                HasComments: false,
                HasEmbeddedThumbnail: false,
                HasEmbeddedFileName: true),
            encodedBytes,
            estimatedDecodedBytes: 748);
    }

    public ValueTask<ImageTransformResult> TransformAsync(
        IReplayableImageSource source,
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class CrashCheckpointObserver : IIngestCheckpointObserver
{
    internal IngestCheckpoint? CancelAt { get; set; }

    public ValueTask ReachedAsync(
        IngestCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CancelAt == checkpoint)
        {
            throw new OperationCanceledException(
                $"Injected cancellation at {checkpoint}.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class NonBufferingReadStream(byte[] bytes) : Stream
{
    private readonly ReadOnlyMemory<byte> _bytes = bytes;
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int remaining = _bytes.Length - _position;
        int copied = Math.Min(remaining, count);
        _bytes.Span.Slice(_position, copied).CopyTo(buffer.AsSpan(offset, copied));
        _position += copied;
        return copied;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int remaining = _bytes.Length - _position;
        int copied = Math.Min(remaining, buffer.Length);
        _bytes.Slice(_position, copied).CopyTo(buffer);
        _position += copied;
        return ValueTask.FromResult(copied);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
