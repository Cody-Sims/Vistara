using Vistara.Domain.Common;

namespace Vistara.Application.Lifecycle;

public interface ILifecycleStore
{
    ValueTask<Result<LifecycleTrashPage>> ListTrashAsync(
        LifecycleTrashQuery query,
        CancellationToken cancellationToken);

    ValueTask<Result<IReadOnlyList<LifecycleAssetMutationResult>>> TrashAsync(
        LifecycleTrashCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<LifecyclePurgeBatchSnapshot>> ConfirmPurgeAsync(
        LifecycleConfirmPurgeCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<LifecycleJobSubmission>> SubmitRestoreAsync(
        LifecycleRestoreCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<LifecyclePurgeDryRunSnapshot>> CreatePurgeDryRunAsync(
        LifecycleCreatePurgeDryRunCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<LifecyclePurgeBatchSnapshot>> GetPurgeBatchAsync(
        Guid tenantId,
        Guid actorId,
        Guid batchId,
        CancellationToken cancellationToken);

    ValueTask<Result<LifecycleHoldSnapshot>> PlaceHoldAsync(
        LifecyclePlaceHoldCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<LifecycleHoldSnapshot>> ReleaseHoldAsync(
        LifecycleReleaseHoldCommand command,
        CancellationToken cancellationToken);
}

public interface ILifecycleWorkerStore
{
    ValueTask<Result> RestoreAsync(
        LifecycleRestoreJobPayload payload,
        DateTimeOffset restoredAtUtc,
        CancellationToken cancellationToken);

    ValueTask<LifecyclePurgeBatchWork> StartPurgeBatchAsync(
        Guid tenantId,
        Guid batchId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<LifecyclePurgeAssetPreparation> PreparePurgeAssetAsync(
        Guid tenantId,
        Guid batchId,
        Guid assetId,
        string storageProvider,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<LifecyclePurgeActionCheck> RecheckPurgeActionAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<Result> RecordPurgeActionDeletedAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<Result<long>> CompletePurgeAssetAsync(
        LifecyclePurgeAssetFence fence,
        DateTimeOffset purgedAtUtc,
        DateTimeOffset backupExpiresAtUtc,
        CancellationToken cancellationToken);

    ValueTask<Result> RecordPurgeItemResultAsync(
        Guid tenantId,
        Guid batchId,
        Guid assetId,
        LifecyclePurgeItemOutcome outcome,
        string errorCode,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<Result> CompletePurgeBatchAsync(
        Guid tenantId,
        Guid batchId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
}

public enum LifecyclePurgeBatchWorkStatus
{
    Ready,
    Completed,
    NotFound,
    InvalidState,
}

public sealed record LifecyclePurgeBatchWork(
    LifecyclePurgeBatchWorkStatus Status,
    IReadOnlyList<Guid> AssetIds);

public enum LifecyclePurgeAssetPreparationStatus
{
    Ready,
    AlreadyPurged,
    Blocked,
    Failed,
}

public enum LifecyclePurgeProviderActionKind
{
    Derivative,
    Original,
}

public sealed record LifecyclePurgeAssetFence(
    Guid TenantId,
    Guid BatchId,
    Guid AssetId,
    long RevisionNumber,
    long LifecycleVersion,
    string RelationshipDigest);

public sealed record LifecyclePurgeProviderAction(
    LifecyclePurgeProviderActionKind Kind,
    Vistara.Application.Common.Storage.BlobKey Key,
    Vistara.Application.Common.Storage.BlobVersion ExpectedVersion,
    long Bytes);

public sealed record LifecyclePurgeAssetWork(
    LifecyclePurgeAssetFence Fence,
    IReadOnlyList<LifecyclePurgeProviderAction> Actions);

public sealed record LifecyclePurgeAssetPreparation(
    LifecyclePurgeAssetPreparationStatus Status,
    LifecyclePurgeAssetWork? Work,
    string? ErrorCode);

public enum LifecyclePurgeActionCheckStatus
{
    Allowed,
    AlreadyDeleted,
    Blocked,
    Stale,
}

public sealed record LifecyclePurgeActionCheck(
    LifecyclePurgeActionCheckStatus Status,
    string? ErrorCode);

public enum LifecyclePurgeItemOutcome
{
    Purged,
    Blocked,
    Failed,
}
