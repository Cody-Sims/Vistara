using System.Collections.Concurrent;
using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Worker.Features.Derivatives;

namespace Vistara.IntegrationTests.DerivativeWorker;

internal sealed class MutableClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    internal void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
}

internal sealed class FakeDerivativeStatePort(DerivativeWorkItem work)
    : IDerivativeStatePort
{
    private readonly object _gate = new();
    private DerivativeFence? _fence;
    private long _fenceVersion;
    private DerivativeStagedOutput? _staged;

    internal DerivativeWorkItem Work { get; } = work;

    internal bool IsReady { get; private set; }

    internal bool CleanupCompleted { get; private set; }

    internal bool PublishOutcomeUnknownRecorded { get; private set; }

    internal int ReadyCommitCount { get; private set; }

    internal DerivativeFailure? LastFailure { get; private set; }

    public ValueTask<DerivativeAcquireResult> AcquireAsync(
        DerivativeAcquireRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (request.RequestId != Work.RequestId ||
                request.TenantId != Work.Generation.Source.TenantId ||
                request.Payload.AssetId != Work.Generation.Source.AssetId ||
                request.Payload.RevisionId != Work.Generation.Source.RevisionId ||
                request.Payload.Preset != Work.Generation.Preset.Id.Name ||
                request.PipelineFingerprint != Work.Generation.PipelineFingerprint)
            {
                return ValueTask.FromResult(DerivativeAcquireResult.NotFound());
            }

            if (IsReady && CleanupCompleted)
            {
                return ValueTask.FromResult(DerivativeAcquireResult.Completed());
            }

            if (_fence is not null && request.NowUtc < _fence.Value.ExpiresAtUtc)
            {
                return ValueTask.FromResult(DerivativeAcquireResult.Busy());
            }

            _fenceVersion++;
            _fence = new DerivativeFence(
                Work.Generation.Source.TenantId,
                Work.RequestId,
                _fenceVersion,
                request.NowUtc.Add(request.OwnershipDuration),
                request.JobLease);
            return ValueTask.FromResult(
                IsReady
                    ? DerivativeAcquireResult.Ready(_fence.Value, Work, _staged)
                    : DerivativeAcquireResult.Acquired(_fence.Value, Work, _staged));
        }
    }

    public ValueTask<DerivativeStateWriteResult> RecordStagedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!Owns(fence))
            {
                return ValueTask.FromResult(DerivativeStateWriteResult.Stale);
            }

            _staged ??= staged;
            return ValueTask.FromResult(DerivativeStateWriteResult.Applied);
        }
    }

    public ValueTask<DerivativeStateWriteResult> RecordPublishOutcomeUnknownAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!Owns(fence))
            {
                return ValueTask.FromResult(DerivativeStateWriteResult.Stale);
            }

            PublishOutcomeUnknownRecorded = true;
            return ValueTask.FromResult(DerivativeStateWriteResult.Applied);
        }
    }

    public async ValueTask<DerivativePublicationOutcome> PublishIfOwnedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        DerivativePublicationOperation publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        DerivativeStateWriteResult authorized = await RecordStagedAsync(
            fence,
            staged,
            cancellationToken);
        if (authorized == DerivativeStateWriteResult.Stale)
        {
            return DerivativePublicationOutcome.Stale;
        }

        DerivativePublicationAttemptOutcome attempt = await publish(cancellationToken);
        if (attempt == DerivativePublicationAttemptOutcome.OutcomeUnknown)
        {
            _ = await RecordPublishOutcomeUnknownAsync(fence, cancellationToken);
        }

        return attempt switch
        {
            DerivativePublicationAttemptOutcome.Published =>
                DerivativePublicationOutcome.Published,
            DerivativePublicationAttemptOutcome.OutcomeUnknown =>
                DerivativePublicationOutcome.OutcomeUnknown,
            DerivativePublicationAttemptOutcome.Retry =>
                DerivativePublicationOutcome.Retry,
            _ => throw new InvalidOperationException(
                "The derivative publication outcome is invalid."),
        };
    }

    public ValueTask<DerivativeStateWriteResult> MarkReadyAsync(
        DerivativeReadyOutput ready,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!Owns(ready.Fence))
            {
                return ValueTask.FromResult(DerivativeStateWriteResult.Stale);
            }

            if (!IsReady)
            {
                IsReady = true;
                ReadyCommitCount++;
            }

            return ValueTask.FromResult(DerivativeStateWriteResult.Applied);
        }
    }

    public ValueTask<DerivativeStateWriteResult> MarkFailedAsync(
        DerivativeFailure failure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!Owns(failure.Fence))
            {
                return ValueTask.FromResult(DerivativeStateWriteResult.Stale);
            }

            LastFailure = failure;
            _staged = null;
            _fence = null;
            return ValueTask.FromResult(DerivativeStateWriteResult.Applied);
        }
    }

    public ValueTask<DerivativeStateWriteResult> CompleteCleanupAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!Owns(fence))
            {
                return ValueTask.FromResult(DerivativeStateWriteResult.Stale);
            }

            CleanupCompleted = true;
            _staged = null;
            _fence = null;
            return ValueTask.FromResult(DerivativeStateWriteResult.Applied);
        }
    }

    internal void ExpireAndStealFence()
    {
        lock (_gate)
        {
            DateTimeOffset expiresAtUtc = _fence?.ExpiresAtUtc ??
                DateTimeOffset.MaxValue.ToUniversalTime();
            _fenceVersion++;
            _fence = new DerivativeFence(
                Work.Generation.Source.TenantId,
                Work.RequestId,
                _fenceVersion,
                expiresAtUtc,
                _fence?.JobLease ??
                    throw new InvalidOperationException("No derivative fence is active."));
        }
    }

    private bool Owns(DerivativeFence fence) => _fence == fence;
}

internal enum DerivativeCopyBehavior
{
    Success,
    OutcomeUnknownAfterCopy,
    PreconditionFailedWithCorruptDestination,
}

internal sealed record DerivativeBlobObject(
    byte[] Bytes,
    BlobVersion Version,
    BlobMediaType ContentType,
    BlobMetadata Metadata,
    IReadOnlyList<BlobChecksum> Checksums);

internal sealed class DerivativeBlobStore : IBlobStore
{
    private static readonly DateTimeOffset ModifiedAtUtc =
        new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
    private readonly ConcurrentDictionary<BlobKey, DerivativeBlobObject> _objects = new();
    private int _version;

    public string Name => "fake";

    public BlobStoreCapabilities Capabilities { get; } = new()
    {
        SupportsConditionalRead = true,
        SupportsConditionalCreate = true,
        SupportsConditionalCopy = true,
        SupportsConditionalDelete = true,
        SupportsServerSideCopy = true,
        ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
    };

    internal DerivativeCopyBehavior CopyBehavior { get; set; }

    internal int DestinationCreateCount { get; private set; }

    internal BlobWriteOptions? LastPutOptions { get; private set; }

    internal BlobCopyOptions? LastCopyOptions { get; private set; }

    internal List<BlobKey> OpenedKeys { get; } = [];

    internal IEnumerable<BlobKey> Keys => _objects.Keys;

    internal bool Contains(BlobKey key) => _objects.ContainsKey(key);

    internal DerivativeBlobObject Get(BlobKey key) => _objects[key];

    internal void AddSource(
        BlobKey key,
        byte[] bytes,
        BlobVersion version,
        ImageSha256 sha256) =>
        _objects[key] = new DerivativeBlobObject(
            bytes,
            version,
            new BlobMediaType("image/png"),
            BlobMetadata.Empty,
            [new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256.Value)]);

    internal void AddDerivative(
        BlobKey key,
        byte[] bytes,
        DerivativeGenerationRequest generation,
        string version)
    {
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _objects[key] = new DerivativeBlobObject(
            bytes,
            new BlobVersion(version),
            new BlobMediaType(generation.Output.ContentType),
            DerivativeMetadata(generation, sha),
            [new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha)]);
    }

    internal void ReplaceVersion(BlobKey key, BlobVersion version) =>
        _objects.AddOrUpdate(
            key,
            _ => throw new InvalidOperationException("Missing source."),
            (_, value) => value with { Version = version });

    internal void ReplaceBytesPreservingIdentity(BlobKey key, byte[] bytes) =>
        _objects.AddOrUpdate(
            key,
            _ => throw new InvalidOperationException("Missing object."),
            (_, value) => value with { Bytes = bytes });

    public ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _objects.TryGetValue(key, out DerivativeBlobObject? value)
                ? Head(key, value)
                : null);
    }

    public ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key,
        BlobReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(key, out DerivativeBlobObject? value))
        {
            throw new BlobStoreException(BlobStoreErrorCode.NotFound, "missing");
        }

        BlobRequestConditions conditions = options.EffectiveConditions;
        if (conditions.IfMatch is not null && conditions.IfMatch != value.Version)
        {
            throw new BlobStoreException(BlobStoreErrorCode.PreconditionFailed, "changed");
        }

        OpenedKeys.Add(key);
        return ValueTask.FromResult(new BlobReadHandle(
            new MemoryStream(value.Bytes, writable: false),
            Head(key, value)));
    }

    public async ValueTask<BlobWriteResult> PutAsync(
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPutOptions = options;
        if (options.Conditions.RequireMissing && _objects.ContainsKey(key))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "destination exists");
        }

        await using Stream stream = await content.OpenReadAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();
        var value = new DerivativeBlobObject(
            bytes,
            NewVersion("staging"),
            options.ContentType ?? new BlobMediaType("application/octet-stream"),
            options.Metadata,
            options.Checksums);
        if (!_objects.TryAdd(key, value))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "destination exists");
        }

        return new BlobWriteResult(Head(key, value), Created: true);
    }

    public ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastCopyOptions = options;
        DerivativeBlobObject sourceObject = _objects[source];
        if (options.EffectiveSourceConditions.IfMatch != sourceObject.Version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "source changed");
        }

        if (_objects.ContainsKey(destination))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "destination exists");
        }

        DerivativeBlobObject copied = sourceObject with
        {
            Version = NewVersion("destination"),
            Metadata = options.ReplacementMetadata ?? sourceObject.Metadata,
        };
        _objects[destination] = copied;
        DestinationCreateCount++;
        if (CopyBehavior == DerivativeCopyBehavior.OutcomeUnknownAfterCopy)
        {
            throw new BlobStoreException(BlobStoreErrorCode.OutcomeUnknown, "ambiguous");
        }

        if (CopyBehavior ==
            DerivativeCopyBehavior.PreconditionFailedWithCorruptDestination)
        {
            _objects[destination] = copied with
            {
                Bytes = "partial-corrupt-output"u8.ToArray(),
            };
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "destination exists");
        }

        return ValueTask.FromResult(new BlobCopyResult(
            Head(destination, copied),
            new BlobIdentity(source, sourceObject.Version)));
    }

    public ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(key, out DerivativeBlobObject? value))
        {
            return ValueTask.FromResult(new BlobDeleteResult(false, null));
        }

        if (options.EffectiveConditions.IfMatch is not null &&
            options.EffectiveConditions.IfMatch != value.Version)
        {
            throw new BlobStoreException(BlobStoreErrorCode.PreconditionFailed, "changed");
        }

        _objects.TryRemove(key, out _);
        return ValueTask.FromResult(new BlobDeleteResult(
            true,
            new BlobIdentity(key, value.Version)));
    }

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

    private BlobVersion NewVersion(string prefix) =>
        new($"{prefix}-v{Interlocked.Increment(ref _version)}");

    private static BlobHead Head(BlobKey key, DerivativeBlobObject value) =>
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

    private static BlobMetadata DerivativeMetadata(
        DerivativeGenerationRequest generation,
        string representationSha256) =>
        new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vistara-derivative-id"] = generation.Identity.Value,
                ["vistara-pipeline-fingerprint"] = generation.PipelineFingerprint.Value,
                ["vistara-recipe-sha256"] = generation.Recipe.Fingerprint,
                ["vistara-representation-sha256"] = representationSha256,
            });
}

internal sealed class DerivativeImageProcessor(
    ImagePipelineFingerprint pipelineFingerprint) : IImageProcessor
{
    internal byte[] OutputBytes { get; set; } =
        "deterministic-webp-output"u8.ToArray();

    internal ImageProcessorErrorCode? Error { get; set; }

    internal bool LeakMetadata { get; set; }

    internal bool PauseTransform { get; set; }

    internal int TransformCalls { get; private set; }

    internal TaskCompletionSource TransformStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource AllowTransform { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ImageProcessorCapabilities Capabilities { get; } = new()
    {
        InputFormats = [ImageFormat.Png],
        OutputFormats = [ImageFormat.WebP],
        MaxFrames = 1,
        SupportsAutoOrientation = true,
        SupportsColorProfileNormalization = true,
        SupportsSensitiveMetadataStripping = true,
        StreamRequirements = new(false, false),
    };

    public ImagePipelineFingerprint PipelineFingerprint { get; } = pipelineFingerprint;

    public ValueTask<ImageInspection> InspectAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async ValueTask<ImageTransformResult> TransformAsync(
        IReplayableImageSource source,
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformCalls++;
        TransformStarted.TrySetResult();
        if (PauseTransform)
        {
            await AllowTransform.Task.WaitAsync(cancellationToken);
        }

        if (Error.HasValue)
        {
            throw new ImageProcessorException(Error.Value, "private decoder details");
        }

        await using Stream input = await source.OpenReadAsync(cancellationToken);
        await input.CopyToAsync(Stream.Null, cancellationToken);
        await destination.WriteAsync(OutputBytes, cancellationToken);
        string sha = Convert.ToHexStringLower(SHA256.HashData(OutputBytes));
        return new ImageTransformResult(
            new ImageInspection(
                ImageFormat.WebP,
                new ImageMediaType("image/webp"),
                width: 256,
                height: 256,
                frameCount: 1,
                aggregatePixels: 65_536,
                ImagePixelFormat.Rgba8,
                ImageOrientation.Normal,
                new ImagePrivacyMetadata(
                    HasExif: LeakMetadata,
                    HasGps: false,
                    HasXmp: false,
                    HasIptc: false,
                    HasComments: false,
                    HasEmbeddedThumbnail: false,
                    HasEmbeddedFileName: false),
                OutputBytes.LongLength,
                estimatedDecodedBytes: 262_144),
            OutputBytes.LongLength,
            new ImageSha256(sha),
            recipe.Fingerprint,
            PipelineFingerprint);
    }
}

internal sealed class TrackingDerivativeOutputScratchFactory
    : IDerivativeOutputScratchFactory
{
    internal int CreateCount { get; private set; }

    internal int OpenReadCount { get; private set; }

    internal int DisposeCount { get; private set; }

    public ValueTask<IDerivativeOutputScratch> CreateAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCount++;
        return ValueTask.FromResult<IDerivativeOutputScratch>(
            new Scratch(this, maximumBytes));
    }

    private sealed class Scratch(
        TrackingDerivativeOutputScratchFactory owner,
        long maximumBytes) : IDerivativeOutputScratch
    {
        private readonly MemoryStream _stream = new();
        private bool _complete;
        private bool _disposed;

        public Stream Destination => !_complete && !_disposed
            ? new MaximumLengthWriteStream(_stream, maximumBytes)
            : throw new InvalidOperationException("Scratch output is not writable.");

        public long Length => _complete
            ? _stream.Length
            : throw new InvalidOperationException("Scratch output is incomplete.");

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _complete = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Length;
            owner.OpenReadCount++;
            _ = _stream.TryGetBuffer(out ArraySegment<byte> bytes);
            return ValueTask.FromResult<Stream>(
                new MemoryStream(
                    bytes.Array!,
                    bytes.Offset,
                    bytes.Count,
                    writable: false,
                    publiclyVisible: false));
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _stream.Dispose();
                _disposed = true;
                owner.DisposeCount++;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class MaximumLengthWriteStream(
        Stream inner,
        long maximumBytes) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        private void EnsureCapacity(int count)
        {
            if (inner.Length > maximumBytes - count)
            {
                throw new ImageProcessorException(
                    ImageProcessorErrorCode.DecodeLimitExceeded,
                    "output limit");
            }
        }
    }
}

internal sealed class DerivativeCheckpointObserver : IDerivativeCheckpointObserver
{
    internal DerivativeCheckpoint? CancelAt { get; set; }

    internal DerivativeCheckpoint? ActionAt { get; set; }

    internal Action? Action { get; set; }

    public ValueTask ReachedAsync(
        DerivativeCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ActionAt == checkpoint)
        {
            Action?.Invoke();
        }

        if (CancelAt == checkpoint)
        {
            throw new OperationCanceledException(
                $"Injected cancellation at {checkpoint}.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }
}
