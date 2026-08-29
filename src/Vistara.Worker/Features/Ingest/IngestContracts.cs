using System.Collections.ObjectModel;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Assets;

namespace Vistara.Worker.Features.Ingest;

public readonly record struct IngestFence(
    Guid TenantId,
    Guid UploadSessionId,
    long Version);

public readonly record struct IngestPromotionToken(string Value);

public readonly record struct IngestCleanupToken(string Value);

public enum IngestLoadDisposition
{
    Ready,
    Activated,
    Rejected,
    Completed,
    NotFound,
    Retry,
}

public sealed class IngestWorkItem
{
    private readonly IReadOnlyDictionary<string, string> _requiredMetadata;

    public IngestWorkItem(
        IngestFence fence,
        Guid actorId,
        Guid reservationId,
        BlobKey stagingKey,
        BlobVersion expectedStagingVersion,
        long expectedSizeBytes,
        Sha256Checksum expectedSha256,
        MediaContentType declaredContentType,
        string storageContainer,
        IReadOnlyDictionary<string, string> requiredMetadata)
    {
        EnsureUuid7(fence.TenantId, nameof(fence));
        EnsureUuid7(fence.UploadSessionId, nameof(fence));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fence.Version);
        EnsureUuid7(actorId, nameof(actorId));
        EnsureUuid7(reservationId, nameof(reservationId));
        ArgumentNullException.ThrowIfNull(stagingKey);
        ArgumentNullException.ThrowIfNull(expectedStagingVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSizeBytes);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(declaredContentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageContainer);
        ArgumentNullException.ThrowIfNull(requiredMetadata);

        Fence = fence;
        ActorId = actorId;
        ReservationId = reservationId;
        StagingKey = stagingKey;
        ExpectedStagingVersion = expectedStagingVersion;
        ExpectedSizeBytes = expectedSizeBytes;
        ExpectedSha256 = expectedSha256;
        DeclaredContentType = declaredContentType;
        StorageContainer = storageContainer.Trim();
        _requiredMetadata = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(requiredMetadata, StringComparer.Ordinal));
    }

    public IngestFence Fence { get; }

    public Guid ActorId { get; }

    public Guid ReservationId { get; }

    public BlobKey StagingKey { get; }

    public BlobVersion ExpectedStagingVersion { get; }

    public long ExpectedSizeBytes { get; }

    public Sha256Checksum ExpectedSha256 { get; }

    public MediaContentType DeclaredContentType { get; }

    public string StorageContainer { get; }

    public IReadOnlyDictionary<string, string> RequiredMetadata => _requiredMetadata;

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The value must be a UUIDv7.", parameterName);
        }
    }
}

public sealed record IngestCleanup(
    IngestCleanupToken Token,
    BlobKey StagingKey,
    BlobVersion ExpectedStagingVersion);

public sealed class IngestLoadResult
{
    private IngestLoadResult(
        IngestLoadDisposition disposition,
        IngestWorkItem? work,
        IngestCleanup? cleanup)
    {
        Disposition = disposition;
        Work = work;
        Cleanup = cleanup;
    }

    public IngestLoadDisposition Disposition { get; }

    public IngestWorkItem? Work { get; }

    public IngestCleanup? Cleanup { get; }

    public static IngestLoadResult Ready(IngestWorkItem work) =>
        new(IngestLoadDisposition.Ready, work, null);

    public static IngestLoadResult Activated(IngestCleanup cleanup) =>
        new(IngestLoadDisposition.Activated, null, cleanup);

    public static IngestLoadResult Rejected() =>
        new(IngestLoadDisposition.Rejected, null, null);

    public static IngestLoadResult Completed() =>
        new(IngestLoadDisposition.Completed, null, null);

    public static IngestLoadResult NotFound() =>
        new(IngestLoadDisposition.NotFound, null, null);

    public static IngestLoadResult Retry() =>
        new(IngestLoadDisposition.Retry, null, null);
}

public enum IngestPromotionMode
{
    PromoteCreateOnly,
    ExistingExactBlob,
}

public sealed record IngestPromotionPlan(
    IngestPromotionToken Token,
    IngestPromotionMode Mode,
    BlobKey CanonicalKey);

public sealed record NormalizedIngestMedia(
    string DetectedFormat,
    MediaContentType ContentType,
    int Width,
    int Height,
    int FrameCount,
    ImageOrientation Orientation,
    bool HasExif,
    bool HasGps,
    bool HasXmp,
    bool HasIptc,
    bool HasComments,
    bool HasEmbeddedThumbnail,
    bool HasEmbeddedFileName);

public sealed record VerifiedIngestObject(
    BlobIdentity SourceIdentity,
    long SizeBytes,
    Sha256Checksum Sha256,
    NormalizedIngestMedia Media);

public sealed record IngestActivation(
    IngestFence Fence,
    Guid ActorId,
    Guid ReservationId,
    string StorageProvider,
    string StorageContainer,
    IngestPromotionPlan Plan,
    VerifiedIngestObject Verified,
    BlobHead? CanonicalHead,
    DateTimeOffset ActivatedAtUtc,
    bool ConsumeReservation,
    bool EnqueueStandardDerivatives,
    bool EnqueueOutbox);

public enum IngestRejectionCode
{
    ObjectMissing,
    ObjectChanged,
    SizeMismatch,
    ChecksumMismatch,
    ContentTypeMismatch,
    MetadataMismatch,
    MalformedImage,
    UnsupportedImage,
    DecodeLimitExceeded,
    CanonicalConflict,
}

public sealed record IngestRejection(
    IngestFence Fence,
    IngestRejectionCode Code,
    DateTimeOffset RejectedAtUtc,
    bool QuarantineStaging,
    bool ReleaseReservation);

public interface IIngestTransactionPort
{
    ValueTask<IngestLoadResult> LoadAndFenceAsync(
        Guid tenantId,
        Guid uploadSessionId,
        CancellationToken cancellationToken);

    ValueTask<IngestPromotionPlan> PlanPromotionAsync(
        IngestFence fence,
        VerifiedIngestObject verified,
        CancellationToken cancellationToken);

    ValueTask RecordPromotionOutcomeUnknownAsync(
        IngestFence fence,
        IngestPromotionPlan plan,
        CancellationToken cancellationToken);

    ValueTask ActivateAsync(
        IngestActivation activation,
        CancellationToken cancellationToken);

    ValueTask RejectAsync(
        IngestRejection rejection,
        CancellationToken cancellationToken);

    ValueTask CompleteCleanupAsync(
        IngestCleanupToken cleanupToken,
        CancellationToken cancellationToken);
}

public enum IngestCheckpoint
{
    UploadFenced,
    PromotionPlanned,
    PromotionStored,
    ActivationCommitted,
    RejectionCommitted,
    StagingDeleted,
    CleanupCommitted,
}

public interface IIngestCheckpointObserver
{
    ValueTask ReachedAsync(
        IngestCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

public sealed class NullIngestCheckpointObserver : IIngestCheckpointObserver
{
    public static NullIngestCheckpointObserver Instance { get; } = new();

    private NullIngestCheckpointObserver()
    {
    }

    public ValueTask ReachedAsync(
        IngestCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
