using System.Buffers;
using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Assets;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Ingest;

public sealed class IngestService
{
    private const int HashBufferSize = 64 * 1024;
    private readonly IIngestTransactionPort _transactions;
    private readonly IBlobStore _blobStore;
    private readonly IImageProcessor _imageProcessor;
    private readonly IClock _clock;
    private readonly ImageDecodeLimits _limits;
    private readonly IIngestCheckpointObserver _checkpoints;

    public IngestService(
        IIngestTransactionPort transactions,
        IBlobStore blobStore,
        IImageProcessor imageProcessor,
        IClock clock,
        ImageDecodeLimits limits,
        IIngestCheckpointObserver? checkpoints = null)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _checkpoints = checkpoints ?? NullIngestCheckpointObserver.Instance;
    }

    public async ValueTask<JobHandlerResult> ProcessAsync(
        Guid tenantId,
        Guid uploadSessionId,
        CancellationToken cancellationToken)
    {
        IngestLoadResult loaded = await _transactions.LoadAndFenceAsync(
            tenantId,
            uploadSessionId,
            cancellationToken);
        switch (loaded.Disposition)
        {
            case IngestLoadDisposition.Activated:
                await CleanupAsync(
                    loaded.Cleanup
                        ?? throw new InvalidOperationException("Activated ingest lacks cleanup."),
                    cancellationToken);
                return JobHandlerResult.Success();
            case IngestLoadDisposition.Rejected:
            case IngestLoadDisposition.Completed:
            case IngestLoadDisposition.NotFound:
                return JobHandlerResult.Success();
            case IngestLoadDisposition.Retry:
                return Retry(JobFailureReason.ProcessingFailed);
            case IngestLoadDisposition.Ready:
                break;
            default:
                throw new InvalidOperationException("The ingest load disposition is invalid.");
        }

        IngestWorkItem work = loaded.Work
            ?? throw new InvalidOperationException("Ready ingest lacks work.");
        if (work.Fence.TenantId != tenantId ||
            work.Fence.UploadSessionId != uploadSessionId)
        {
            return Retry(JobFailureReason.ProcessingFailed);
        }

        await CheckpointAsync(IngestCheckpoint.UploadFenced, cancellationToken);

        BlobHead? stagingHead;
        try
        {
            stagingHead = await _blobStore.HeadAsync(work.StagingKey, cancellationToken);
        }
        catch (BlobStoreException)
        {
            return Retry(JobFailureReason.ProviderUnavailable);
        }

        if (stagingHead is null)
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.ObjectMissing,
                cancellationToken);
        }

        IngestRejectionCode? headFailure = ValidateStagingHead(work, stagingHead);
        if (headFailure.HasValue)
        {
            return await RejectAsync(work.Fence, headFailure.Value, cancellationToken);
        }

        HashResult stagingHash;
        try
        {
            stagingHash = await HashAsync(
                work.StagingKey,
                work.ExpectedStagingVersion,
                cancellationToken);
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed)
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.ObjectChanged,
                cancellationToken);
        }
        catch (BlobStoreException)
        {
            return Retry(JobFailureReason.ProviderUnavailable);
        }

        if (stagingHash.BytesRead != work.ExpectedSizeBytes)
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.SizeMismatch,
                cancellationToken);
        }

        if (stagingHash.Sha256 != work.ExpectedSha256)
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.ChecksumMismatch,
                cancellationToken);
        }

        ImageInspection inspection;
        try
        {
            inspection = await _imageProcessor.InspectAsync(
                new BlobImageSource(
                    _blobStore,
                    work.StagingKey,
                    work.ExpectedStagingVersion,
                    work.ExpectedSizeBytes),
                _limits,
                cancellationToken);
        }
        catch (ImageProcessorException exception)
        {
            IngestRejectionCode? code = exception.Code switch
            {
                ImageProcessorErrorCode.MalformedImage =>
                    IngestRejectionCode.MalformedImage,
                ImageProcessorErrorCode.UnsupportedFormat =>
                    IngestRejectionCode.UnsupportedImage,
                ImageProcessorErrorCode.DecodeLimitExceeded =>
                    IngestRejectionCode.DecodeLimitExceeded,
                _ => null,
            };
            return code.HasValue
                ? await RejectAsync(work.Fence, code.Value, cancellationToken)
                : Retry(JobFailureReason.ProcessingFailed);
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed)
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.ObjectChanged,
                cancellationToken);
        }

        if (inspection.EncodedBytes != work.ExpectedSizeBytes)
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.SizeMismatch,
                cancellationToken);
        }

        if (!string.Equals(
                inspection.ContentType.Value,
                work.DeclaredContentType.Value,
                StringComparison.Ordinal))
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.ContentTypeMismatch,
                cancellationToken);
        }

        var verified = new VerifiedIngestObject(
            stagingHead.Identity,
            work.ExpectedSizeBytes,
            work.ExpectedSha256,
            Normalize(inspection));
        IngestPromotionPlan plan = await _transactions.PlanPromotionAsync(
            work.Fence,
            verified,
            cancellationToken);
        await CheckpointAsync(IngestCheckpoint.PromotionPlanned, cancellationToken);

        BlobHead? canonicalHead = null;
        if (plan.Mode == IngestPromotionMode.PromoteCreateOnly)
        {
            JobHandlerResult? promotionFailure = await PromoteAsync(
                work,
                verified,
                plan,
                cancellationToken);
            if (promotionFailure is not null)
            {
                return promotionFailure;
            }

            await CheckpointAsync(IngestCheckpoint.PromotionStored, cancellationToken);
        }

        CanonicalVerification canonical = await VerifyCanonicalAsync(
            work,
            plan.CanonicalKey,
            requireUploadOwnership:
                plan.Mode == IngestPromotionMode.PromoteCreateOnly,
            cancellationToken);
        if (canonical.Missing &&
            plan.Mode == IngestPromotionMode.ExistingExactBlob)
        {
            JobHandlerResult? repairFailure = await PromoteAsync(
                work,
                verified,
                plan,
                cancellationToken);
            if (repairFailure is not null)
            {
                return repairFailure;
            }

            await CheckpointAsync(IngestCheckpoint.PromotionStored, cancellationToken);
            canonical = await VerifyCanonicalAsync(
                work,
                plan.CanonicalKey,
                requireUploadOwnership: true,
                cancellationToken);
        }

        if (canonical.Retry)
        {
            return Retry(JobFailureReason.ProviderUnavailable);
        }

        if (!canonical.IsValid)
        {
            return await RejectAsync(
                work.Fence,
                IngestRejectionCode.CanonicalConflict,
                cancellationToken);
        }

        canonicalHead = canonical.Head;

        await _transactions.ActivateAsync(
            new IngestActivation(
                work.Fence,
                work.ActorId,
                work.ReservationId,
                _blobStore.Name,
                work.StorageContainer,
                plan,
                verified,
                canonicalHead,
                _clock.UtcNow,
                ConsumeReservation: true,
                EnqueueStandardDerivatives: true,
                EnqueueOutbox: true),
            cancellationToken);
        await CheckpointAsync(IngestCheckpoint.ActivationCommitted, cancellationToken);

        await CleanupAsync(
            new IngestCleanup(
                new IngestCleanupToken(plan.Token.Value),
                work.StagingKey,
                work.ExpectedStagingVersion),
            cancellationToken);
        return JobHandlerResult.Success();
    }

    private async ValueTask<JobHandlerResult?> PromoteAsync(
        IngestWorkItem work,
        VerifiedIngestObject verified,
        IngestPromotionPlan plan,
        CancellationToken cancellationToken)
    {
        BlobMetadata metadata = CreateCanonicalMetadata(work, verified);
        try
        {
            _ = await _blobStore.CopyAsync(
                work.StagingKey,
                plan.CanonicalKey,
                new BlobCopyOptions(
                    SourceConditions: new BlobRequestConditions(
                        ifMatch: work.ExpectedStagingVersion),
                    DestinationConditions: BlobRequestConditions.CreateOnly,
                    ReplacementMetadata: metadata),
                cancellationToken);
            return null;
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.OutcomeUnknown)
        {
            await _transactions.RecordPromotionOutcomeUnknownAsync(
                work.Fence,
                plan,
                cancellationToken);
            return null;
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.PreconditionFailed)
        {
            BlobHead? destination;
            try
            {
                destination = await _blobStore.HeadAsync(plan.CanonicalKey, cancellationToken);
            }
            catch (BlobStoreException)
            {
                return Retry(JobFailureReason.ProviderUnavailable);
            }

            if (destination is not null)
            {
                return null;
            }

            BlobHead? source = await _blobStore.HeadAsync(work.StagingKey, cancellationToken);
            if (source is null || source.Identity.Version != work.ExpectedStagingVersion)
            {
                return await RejectAsync(
                    work.Fence,
                    IngestRejectionCode.ObjectChanged,
                    cancellationToken);
            }

            return Retry(JobFailureReason.ProviderUnavailable);
        }
        catch (BlobStoreException)
        {
            return Retry(JobFailureReason.ProviderUnavailable);
        }
    }

    private async ValueTask<CanonicalVerification> VerifyCanonicalAsync(
        IngestWorkItem work,
        BlobKey canonicalKey,
        bool requireUploadOwnership,
        CancellationToken cancellationToken)
    {
        BlobHead? head;
        try
        {
            head = await _blobStore.HeadAsync(canonicalKey, cancellationToken);
        }
        catch (BlobStoreException)
        {
            return CanonicalVerification.RetryLater();
        }

        if (head is null)
        {
            return CanonicalVerification.NotFound();
        }

        if (head.Properties.ContentLength != work.ExpectedSizeBytes ||
            !string.Equals(
                head.Properties.ContentType.Value,
                work.DeclaredContentType.Value,
                StringComparison.Ordinal) ||
            !HasCanonicalOwnershipMetadata(
                head.Properties.Metadata,
                work,
                requireUploadOwnership) ||
            !head.Properties.Metadata.TryGetValue("vistara-sha256", out string? metadataSha) ||
            !string.Equals(metadataSha, work.ExpectedSha256.Value, StringComparison.Ordinal) ||
            !head.Properties.Metadata.TryGetValue(
                "vistara-media-type",
                out string? mediaType) ||
            !string.Equals(
                mediaType,
                work.DeclaredContentType.Value,
                StringComparison.Ordinal))
        {
            return CanonicalVerification.Invalid();
        }

        try
        {
            HashResult hash = await HashAsync(
                canonicalKey,
                head.Identity.Version,
                cancellationToken);
            return hash.BytesRead == work.ExpectedSizeBytes &&
                hash.Sha256 == work.ExpectedSha256
                    ? CanonicalVerification.Valid(head)
                    : CanonicalVerification.Invalid();
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed)
        {
            return CanonicalVerification.RetryLater();
        }
        catch (BlobStoreException)
        {
            return CanonicalVerification.RetryLater();
        }
    }

    private async ValueTask CleanupAsync(
        IngestCleanup cleanup,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _blobStore.DeleteAsync(
                cleanup.StagingKey,
                new BlobDeleteOptions(
                    new BlobRequestConditions(ifMatch: cleanup.ExpectedStagingVersion)),
                cancellationToken);
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.NotFound)
        {
        }

        await CheckpointAsync(IngestCheckpoint.StagingDeleted, cancellationToken);
        await _transactions.CompleteCleanupAsync(cleanup.Token, cancellationToken);
        await CheckpointAsync(IngestCheckpoint.CleanupCommitted, cancellationToken);
    }

    private async ValueTask<JobHandlerResult> RejectAsync(
        IngestFence fence,
        IngestRejectionCode code,
        CancellationToken cancellationToken)
    {
        await _transactions.RejectAsync(
            new IngestRejection(
                fence,
                code,
                _clock.UtcNow,
                QuarantineStaging: true,
                ReleaseReservation: false),
            cancellationToken);
        await CheckpointAsync(IngestCheckpoint.RejectionCommitted, cancellationToken);
        return JobHandlerResult.Success();
    }

    private async ValueTask<HashResult> HashAsync(
        BlobKey key,
        BlobVersion version,
        CancellationToken cancellationToken)
    {
        await using BlobReadHandle handle = await _blobStore.OpenReadAsync(
            key,
            new BlobReadOptions(
                Conditions: new BlobRequestConditions(ifMatch: version)),
            cancellationToken);
        if (handle.Head.Identity.Key != key ||
            handle.Head.Identity.Version != version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "The blob changed while it was opened.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        long bytesRead = 0;
        try
        {
            int read;
            while ((read = await handle.Content.ReadAsync(
                       buffer.AsMemory(0, HashBufferSize),
                       cancellationToken)) > 0)
            {
                bytesRead = checked(bytesRead + read);
                hash.AppendData(buffer, 0, read);
            }

            return new HashResult(
                bytesRead,
                new Sha256Checksum(Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static IngestRejectionCode? ValidateStagingHead(
        IngestWorkItem work,
        BlobHead head)
    {
        if (head.Identity.Key != work.StagingKey ||
            head.Identity.Version != work.ExpectedStagingVersion)
        {
            return IngestRejectionCode.ObjectChanged;
        }

        if (head.Properties.ContentLength != work.ExpectedSizeBytes)
        {
            return IngestRejectionCode.SizeMismatch;
        }

        if (!string.Equals(
                head.Properties.ContentType.Value,
                work.DeclaredContentType.Value,
                StringComparison.Ordinal))
        {
            return IngestRejectionCode.ContentTypeMismatch;
        }

        if (!HasRequiredMetadata(head.Properties.Metadata, work.RequiredMetadata))
        {
            return IngestRejectionCode.MetadataMismatch;
        }

        BlobChecksum? sha256 = head.Properties.Checksums.SingleOrDefault(
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);
        return sha256 is not null &&
            !string.Equals(
                sha256.Value,
                work.ExpectedSha256.Value,
                StringComparison.Ordinal)
                ? IngestRejectionCode.ChecksumMismatch
                : null;
    }

    private static bool HasRequiredMetadata(
        BlobMetadata observed,
        IReadOnlyDictionary<string, string> required) =>
        required.All(pair =>
            observed.TryGetValue(pair.Key, out string? value) &&
            string.Equals(value, pair.Value, StringComparison.Ordinal));

    private static bool HasCanonicalOwnershipMetadata(
        BlobMetadata observed,
        IngestWorkItem work,
        bool requireUploadOwnership)
    {
        if (!work.RequiredMetadata.TryGetValue(
                "vistara-tenant-id",
                out string? expectedTenantId) ||
            !observed.TryGetValue("vistara-tenant-id", out string? tenantId) ||
            !string.Equals(tenantId, expectedTenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return !requireUploadOwnership ||
            HasRequiredMetadata(observed, work.RequiredMetadata);
    }

    private static BlobMetadata CreateCanonicalMetadata(
        IngestWorkItem work,
        VerifiedIngestObject verified)
    {
        var values = new Dictionary<string, string>(
            work.RequiredMetadata,
            StringComparer.Ordinal)
        {
            ["vistara-sha256"] = verified.Sha256.Value,
            ["vistara-media-type"] = verified.Media.ContentType.Value,
        };
        return new BlobMetadata(values);
    }

    private static NormalizedIngestMedia Normalize(ImageInspection inspection) =>
        new(
            inspection.Format.ToString().ToLowerInvariant(),
            new MediaContentType(inspection.ContentType.Value),
            inspection.Width,
            inspection.Height,
            inspection.FrameCount,
            inspection.Orientation,
            inspection.Privacy.HasExif,
            inspection.Privacy.HasGps,
            inspection.Privacy.HasXmp,
            inspection.Privacy.HasIptc,
            inspection.Privacy.HasComments,
            inspection.Privacy.HasEmbeddedThumbnail,
            inspection.Privacy.HasEmbeddedFileName);

    private ValueTask CheckpointAsync(
        IngestCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        _checkpoints.ReachedAsync(checkpoint, cancellationToken);

    private static JobHandlerResult Retry(JobFailureReason reason) =>
        JobHandlerResult.Failed(new JobFailure(reason));

    private sealed record HashResult(long BytesRead, Sha256Checksum Sha256);

    private sealed class CanonicalVerification
    {
        private CanonicalVerification(
            bool isValid,
            bool retry,
            bool missing,
            BlobHead? head)
        {
            IsValid = isValid;
            Retry = retry;
            Missing = missing;
            Head = head;
        }

        internal bool IsValid { get; }

        internal bool Retry { get; }

        internal bool Missing { get; }

        internal BlobHead? Head { get; }

        internal static CanonicalVerification Valid(BlobHead head) =>
            new(true, false, false, head);

        internal static CanonicalVerification Invalid() =>
            new(false, false, false, null);

        internal static CanonicalVerification NotFound() =>
            new(false, false, true, null);

        internal static CanonicalVerification RetryLater() =>
            new(false, true, false, null);
    }

    private sealed class BlobImageSource(
        IBlobStore blobStore,
        BlobKey key,
        BlobVersion version,
        long length) : IReplayableImageSource
    {
        public long? Length { get; } = length;

        public bool OpensSeekableStreams => false;

        public async ValueTask<Stream> OpenReadAsync(
            CancellationToken cancellationToken)
        {
            BlobReadHandle handle = await blobStore.OpenReadAsync(
                key,
                new BlobReadOptions(
                    Conditions: new BlobRequestConditions(ifMatch: version)),
                cancellationToken);
            if (handle.Head.Identity.Key != key ||
                handle.Head.Identity.Version != version)
            {
                await handle.DisposeAsync();
                throw new BlobStoreException(
                    BlobStoreErrorCode.PreconditionFailed,
                    "The blob changed while it was opened.");
            }

            return new OwnedBlobReadStream(handle);
        }
    }

    private sealed class OwnedBlobReadStream(BlobReadHandle handle) : Stream
    {
        private bool _disposed;

        public override bool CanRead => handle.Content.CanRead;

        public override bool CanSeek => handle.Content.CanSeek;

        public override bool CanWrite => false;

        public override long Length => handle.Content.Length;

        public override long Position
        {
            get => handle.Content.Position;
            set => handle.Content.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            handle.Content.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            handle.Content.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            handle.Content.Seek(offset, origin);

        public override void Flush() => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await handle.DisposeAsync();
                _disposed = true;
            }

            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
