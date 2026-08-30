using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Lifecycle;

public sealed class LifecyclePurgeService(
    ILifecycleWorkerStore store,
    IBlobStore blobStore,
    IClock clock)
{
    public static readonly TimeSpan TombstoneBackupWindow = TimeSpan.FromDays(90);

    private readonly ILifecycleWorkerStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IBlobStore _blobStore =
        blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    public async ValueTask<JobHandlerResult> ProcessAsync(
        Guid tenantId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = UtcNow();
        LifecyclePurgeBatchWork batch = await _store.StartPurgeBatchAsync(
            tenantId,
            batchId,
            now,
            cancellationToken);
        if (batch.Status == LifecyclePurgeBatchWorkStatus.Completed)
        {
            return JobHandlerResult.Success();
        }

        if (batch.Status != LifecyclePurgeBatchWorkStatus.Ready)
        {
            return Failed(JobFailureReason.ProcessingFailed);
        }

        foreach (Guid assetId in batch.AssetIds)
        {
            LifecyclePurgeAssetPreparation preparation =
                await _store.PreparePurgeAssetAsync(
                    tenantId,
                    batchId,
                    assetId,
                    _blobStore.Name,
                    UtcNow(),
                    cancellationToken);
            if (preparation.Status ==
                LifecyclePurgeAssetPreparationStatus.AlreadyPurged)
            {
                continue;
            }

            if (preparation.Status is
                LifecyclePurgeAssetPreparationStatus.Blocked or
                LifecyclePurgeAssetPreparationStatus.Failed)
            {
                await RecordItemAsync(
                    tenantId,
                    batchId,
                    assetId,
                    preparation.Status == LifecyclePurgeAssetPreparationStatus.Blocked
                        ? LifecyclePurgeItemOutcome.Blocked
                        : LifecyclePurgeItemOutcome.Failed,
                    preparation.ErrorCode ?? "purge.preparation_failed",
                    cancellationToken);
                continue;
            }

            LifecyclePurgeAssetWork work = preparation.Work ??
                throw new InvalidOperationException(
                    "A ready purge preparation must include work.");
            bool stopAsset = false;
            foreach (LifecyclePurgeProviderAction action in work.Actions)
            {
                ProviderActionResult deletion = await DeleteAsync(
                    work.Fence,
                    action,
                    cancellationToken);
                switch (deletion.Status)
                {
                    case ProviderActionStatus.Deleted:
                        continue;
                    case ProviderActionStatus.Retry:
                        return Failed(JobFailureReason.ProviderUnavailable);
                    case ProviderActionStatus.Blocked:
                        await RecordItemAsync(
                            tenantId,
                            batchId,
                            assetId,
                            LifecyclePurgeItemOutcome.Blocked,
                            deletion.ErrorCode ?? "purge.blocked",
                            cancellationToken);
                        stopAsset = true;
                        break;
                    case ProviderActionStatus.Failed:
                        await RecordItemAsync(
                            tenantId,
                            batchId,
                            assetId,
                            LifecyclePurgeItemOutcome.Failed,
                            deletion.ErrorCode ?? "purge.provider_failure",
                            cancellationToken);
                        stopAsset = true;
                        break;
                    default:
                        throw new InvalidOperationException(
                            "The provider deletion result is invalid.");
                }

                if (stopAsset)
                {
                    break;
                }
            }

            if (stopAsset)
            {
                continue;
            }

            DateTimeOffset purgedAt = UtcNow();
            Result<long> completed = await _store.CompletePurgeAssetAsync(
                work.Fence,
                purgedAt,
                purgedAt.Add(TombstoneBackupWindow),
                cancellationToken);
            if (completed.IsFailure)
            {
                await RecordItemAsync(
                    tenantId,
                    batchId,
                    assetId,
                    LifecyclePurgeItemOutcome.Blocked,
                    completed.Error?.Code ?? "purge.completion_blocked",
                    cancellationToken);
            }
        }

        Result completedBatch = await _store.CompletePurgeBatchAsync(
            tenantId,
            batchId,
            UtcNow(),
            cancellationToken);
        return completedBatch.IsSuccess
            ? JobHandlerResult.Success()
            : Failed(JobFailureReason.ProcessingFailed);
    }

    private async ValueTask<ProviderActionResult> DeleteAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        CancellationToken cancellationToken)
    {
        BlobHead? head;
        try
        {
            head = await _blobStore.HeadAsync(action.Key, cancellationToken);
        }
        catch (BlobStoreException exception)
        {
            return exception.Code == BlobStoreErrorCode.NotFound
                ? await RecordAbsentAsync(fence, action, cancellationToken)
                : IsPermanentProviderFailure(exception.Code)
                    ? ProviderActionResult.Failed("purge.provider_contract_failure")
                    : ProviderActionResult.Retry();
        }

        if (head is null)
        {
            return await RecordAbsentAsync(fence, action, cancellationToken);
        }

        if (head.Identity.Version != action.ExpectedVersion)
        {
            return ProviderActionResult.Blocked("purge.provider_version_changed");
        }

        LifecyclePurgeActionCheck check = await _store.RecheckPurgeActionAsync(
            fence,
            action,
            UtcNow(),
            cancellationToken);
        if (check.Status == LifecyclePurgeActionCheckStatus.AlreadyDeleted)
        {
            return await RecordAbsentAsync(fence, action, cancellationToken);
        }

        if (check.Status != LifecyclePurgeActionCheckStatus.Allowed)
        {
            return check.Status == LifecyclePurgeActionCheckStatus.Blocked
                ? ProviderActionResult.Blocked(check.ErrorCode)
                : ProviderActionResult.Blocked(
                    check.ErrorCode ?? "purge.action_stale");
        }

        try
        {
            BlobDeleteResult result = await _blobStore.DeleteAsync(
                action.Key,
                new BlobDeleteOptions(
                    new BlobRequestConditions(ifMatch: head.Identity.Version)),
                cancellationToken);
            if (!result.Deleted ||
                result.DeletedIdentity is null ||
                result.DeletedIdentity != head.Identity)
            {
                return await ReconcileAsync(fence, action, cancellationToken);
            }

            return await RecordAbsentAsync(fence, action, cancellationToken);
        }
        catch (BlobStoreException exception)
        {
            return exception.Code switch
            {
                BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed or
                BlobStoreErrorCode.OutcomeUnknown =>
                    await ReconcileAsync(fence, action, cancellationToken),
                _ when IsPermanentProviderFailure(exception.Code) =>
                    ProviderActionResult.Failed(
                        "purge.provider_contract_failure"),
                _ => ProviderActionResult.Retry(),
            };
        }
    }

    private async ValueTask<ProviderActionResult> ReconcileAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            BlobHead? current = await _blobStore.HeadAsync(
                action.Key,
                cancellationToken);
            if (current is null)
            {
                return await RecordAbsentAsync(
                    fence,
                    action,
                    cancellationToken);
            }

            return current.Identity.Version == action.ExpectedVersion
                ? ProviderActionResult.Retry()
                : ProviderActionResult.Blocked(
                    "purge.provider_version_changed");
        }
        catch (BlobStoreException exception)
        {
            return exception.Code == BlobStoreErrorCode.NotFound
                ? await RecordAbsentAsync(fence, action, cancellationToken)
                : IsPermanentProviderFailure(exception.Code)
                    ? ProviderActionResult.Failed(
                        "purge.provider_contract_failure")
                    : ProviderActionResult.Retry();
        }
    }

    private async ValueTask<ProviderActionResult> RecordAbsentAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        CancellationToken cancellationToken)
    {
        Result recorded = await _store.RecordPurgeActionDeletedAsync(
            fence,
            action,
            UtcNow(),
            cancellationToken);
        return recorded.IsSuccess
            ? ProviderActionResult.Deleted()
            : ProviderActionResult.Blocked(recorded.Error?.Code);
    }

    private async ValueTask RecordItemAsync(
        Guid tenantId,
        Guid batchId,
        Guid assetId,
        LifecyclePurgeItemOutcome outcome,
        string errorCode,
        CancellationToken cancellationToken)
    {
        Result recorded = await _store.RecordPurgeItemResultAsync(
            tenantId,
            batchId,
            assetId,
            outcome,
            errorCode,
            UtcNow(),
            cancellationToken);
        if (recorded.IsFailure)
        {
            throw new InvalidOperationException(
                "The purge item result could not be persisted.");
        }
    }

    private DateTimeOffset UtcNow()
    {
        DateTimeOffset now = _clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The lifecycle clock must return UTC.");
        }

        return now;
    }

    private static bool IsPermanentProviderFailure(BlobStoreErrorCode code) =>
        code is BlobStoreErrorCode.Unsupported or
            BlobStoreErrorCode.InvalidRange or
            BlobStoreErrorCode.IntegrityMismatch or
            BlobStoreErrorCode.InvalidRequest;

    private static JobHandlerResult Failed(JobFailureReason reason) =>
        JobHandlerResult.Failed(new JobFailure(reason));

    private enum ProviderActionStatus
    {
        Deleted,
        Retry,
        Blocked,
        Failed,
    }

    private sealed record ProviderActionResult(
        ProviderActionStatus Status,
        string? ErrorCode)
    {
        internal static ProviderActionResult Deleted() =>
            new(ProviderActionStatus.Deleted, null);

        internal static ProviderActionResult Retry() =>
            new(ProviderActionStatus.Retry, null);

        internal static ProviderActionResult Blocked(string? errorCode) =>
            new(ProviderActionStatus.Blocked, errorCode);

        internal static ProviderActionResult Failed(string errorCode) =>
            new(ProviderActionStatus.Failed, errorCode);
    }
}
