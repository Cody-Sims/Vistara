using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Domain.Lifecycle;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Media;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Lifecycle;

public sealed class RelationalLifecycleWorkerStore(
    VistaraDbContext context,
    IUuid7Generator ids) : ILifecycleWorkerStore
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IUuid7Generator _ids =
        ids ?? throw new ArgumentNullException(nameof(ids));

    public async ValueTask<Result> RestoreAsync(
        LifecycleRestoreJobPayload payload,
        DateTimeOffset restoredAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                payload.TenantId,
                cancellationToken);
        try
        {
            string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
                _context,
                payload.ActorId,
                cancellationToken);
            foreach (LifecycleAssetTarget target in payload.Targets)
            {
                AssetRow? asset = await _context.Assets
                    .SingleOrDefaultAsync(
                        row => row.Id == target.AssetId,
                        cancellationToken);
                AssetLifecycleRow? lifecycle = await _context.AssetLifecycles
                    .SingleOrDefaultAsync(
                        row => row.AssetId == target.AssetId,
                        cancellationToken);
                TrashEntryRow? trash = await _context.TrashEntries
                    .SingleOrDefaultAsync(
                        row => row.AssetId == target.AssetId,
                        cancellationToken);
                if (asset is null || lifecycle is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(LifecycleApplicationErrors.NotFound);
                }

                if (!LifecyclePersistenceSupport.CanMutateOwnedAsset(
                        role,
                        payload.ActorId,
                        asset.OwnerId))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(LifecycleApplicationErrors.Forbidden);
                }

                if (asset.Status == "Ready" &&
                    lifecycle.State == "Ready" &&
                    lifecycle.HasBeenTrashed &&
                    trash is null)
                {
                    continue;
                }

                if (asset.Status != "Trashed" ||
                    lifecycle.State != "Trashed" ||
                    trash is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(LifecycleApplicationErrors.InvalidState);
                }

                if (asset.Version != target.Version)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(LifecycleApplicationErrors.VersionConflict);
                }

                RelationshipSnapshot relationships =
                    await LifecyclePersistenceSupport.LoadFrozenRelationshipsAsync(
                        _context,
                        asset.Id,
                        cancellationToken);
                asset.Status = "Ready";
                asset.UpdatedAtUtc = restoredAtUtc;
                asset.Version = checked(asset.Version + 1);
                lifecycle.State = "Ready";
                lifecycle.LastRestoredAtUtc = restoredAtUtc;
                lifecycle.LastRestoredByUserId = payload.ActorId;
                lifecycle.ActivePurgeBatchId = null;
                lifecycle.PurgeRequestedByUserId = null;
                lifecycle.PurgeInitiatorKind = null;
                lifecycle.PurgeEvaluatedAtUtc = null;
                lifecycle.PurgeObservedRevision = null;
                lifecycle.PurgeHasBlockingReferences = null;
                lifecycle.Version = checked(lifecycle.Version + 1);
                _context.TrashEntries.Remove(trash);
                LifecyclePersistenceSupport.AddAudit(
                    _context,
                    _ids,
                    payload.TenantId,
                    payload.ActorId,
                    "asset.restored",
                    asset.Id,
                    LifecyclePersistenceSupport.StateSummary(
                        "Trashed",
                        relationships.Digest),
                    LifecyclePersistenceSupport.StateSummary(
                        "Ready",
                        relationships.Digest),
                    "Succeeded",
                    restoredAtUtc);
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            return Result.Failure(LifecycleApplicationErrors.VersionConflict);
        }
    }

    public async ValueTask<LifecyclePurgeBatchWork> StartPurgeBatchAsync(
        Guid tenantId,
        Guid batchId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        PurgeBatchRow? batch = await _context.PurgeBatches
            .SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken);
        if (batch is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(LifecyclePurgeBatchWorkStatus.NotFound, []);
        }

        if (batch.State == "Completed")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(LifecyclePurgeBatchWorkStatus.Completed, []);
        }

        if (batch.State is not ("Approved" or "Executing") ||
            !await HasValidApprovalAsync(batch, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(LifecyclePurgeBatchWorkStatus.InvalidState, []);
        }

        if (batch.State == "Approved")
        {
            batch.State = "Executing";
            batch.StartedAtUtc = startedAtUtc;
            batch.Version = checked(batch.Version + 1);
            await _context.SaveChangesAsync(cancellationToken);
        }

        List<Guid> pendingCandidates = await _context.PurgeBatchItems
            .AsNoTracking()
            .Where(row =>
                row.PurgeBatchId == batchId &&
                row.Result == "Failed")
            .OrderBy(row => row.AssetId)
            .Select(row => row.AssetId)
            .ToListAsync(cancellationToken);
        string batchItemPrefix = $"{batchId:D}:";
        HashSet<string> terminalFailures = (await _context.AuditEvents
                .AsNoTracking()
                .Where(row =>
                    row.Action == "asset.purge_failed" &&
                    row.ResourceType == "purgeBatchItem" &&
                    row.ResourceIdentifier.StartsWith(batchItemPrefix))
                .Select(row => row.ResourceIdentifier)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        Guid[] assets = pendingCandidates
            .Where(assetId => !terminalFailures.Contains(
                LifecyclePersistenceSupport.PurgeBatchItemResourceIdentifier(
                    batchId,
                    assetId)))
            .ToArray();
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return new(LifecyclePurgeBatchWorkStatus.Ready, assets);
    }

    public async ValueTask<LifecyclePurgeAssetPreparation> PreparePurgeAssetAsync(
        Guid tenantId,
        Guid batchId,
        Guid assetId,
        string storageProvider,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageProvider);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        if (await _context.DeletionTombstones
                .AsNoTracking()
                .AnyAsync(row => row.FormerAssetId == assetId, cancellationToken))
        {
            PurgeBatchItemRow? completedItem = await _context.PurgeBatchItems
                .SingleOrDefaultAsync(
                    row =>
                        row.PurgeBatchId == batchId &&
                        row.AssetId == assetId,
                    cancellationToken);
            if (completedItem is not null)
            {
                completedItem.Result = "Purged";
                await _context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new(
                LifecyclePurgeAssetPreparationStatus.AlreadyPurged,
                null,
                null);
        }

        PurgeBatchRow? batch = await _context.PurgeBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken);
        PurgeBatchItemRow? item = await _context.PurgeBatchItems
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.PurgeBatchId == batchId &&
                    row.AssetId == assetId,
                cancellationToken);
        if (batch is null ||
            item is null ||
            batch.State != "Executing" ||
            !await HasValidApprovalAsync(batch, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                LifecyclePurgeAssetPreparationStatus.Failed,
                null,
                "purge.invalid_batch");
        }

        LifecyclePurgeCandidateState? candidate =
            await LifecyclePersistenceSupport.LoadPurgeCandidateAsync(
                _context,
                assetId,
                evaluatedAtUtc,
                cancellationToken);
        if (candidate is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                LifecyclePurgeAssetPreparationStatus.Failed,
                null,
                "purge.asset_missing");
        }

        if (candidate.RevisionNumber != item.Revision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                LifecyclePurgeAssetPreparationStatus.Blocked,
                null,
                "purge.revision_changed");
        }

        if (!candidate.Eligible)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                LifecyclePurgeAssetPreparationStatus.Blocked,
                null,
                LifecyclePersistenceSupport.BarrierErrorCode(candidate));
        }

        AssetLifecycleRow lifecycle = await _context.AssetLifecycles
            .SingleAsync(row => row.AssetId == assetId, cancellationToken);
        if (lifecycle.State == "Purging" &&
            lifecycle.ActivePurgeBatchId != batchId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                LifecyclePurgeAssetPreparationStatus.Blocked,
                null,
                "purge.concurrent_batch");
        }

        if (lifecycle.State == "Trashed")
        {
            lifecycle.State = "Purging";
            lifecycle.ActivePurgeBatchId = batchId;
            lifecycle.PurgeRequestedByUserId = batch.RequestedByUserId;
            lifecycle.PurgeInitiatorKind = "Human";
            lifecycle.PurgeEvaluatedAtUtc = evaluatedAtUtc;
            lifecycle.PurgeObservedRevision = candidate.RevisionNumber;
            lifecycle.PurgeHasBlockingReferences = false;
            lifecycle.Version = checked(lifecycle.Version + 1);
            await _context.SaveChangesAsync(cancellationToken);
        }

        List<LifecyclePurgeProviderAction> actions =
            await LoadRemainingActionsAsync(
                assetId,
                storageProvider,
                cancellationToken);
        var fence = new LifecyclePurgeAssetFence(
            tenantId,
            batchId,
            assetId,
            item.Revision,
            lifecycle.Version,
            candidate.FrozenRelationships.Digest);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return new(
            LifecyclePurgeAssetPreparationStatus.Ready,
            new LifecyclePurgeAssetWork(fence, actions),
            null);
    }

    public async ValueTask<LifecyclePurgeActionCheck> RecheckPurgeActionAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(action);
        LifecyclePurgeActionCheck? fenceCheck = await ValidateFenceAsync(
            fence,
            evaluatedAtUtc,
            cancellationToken);
        if (fenceCheck is not null)
        {
            return fenceCheck;
        }

        return action.Kind switch
        {
            LifecyclePurgeProviderActionKind.Derivative =>
                await CheckDerivativeActionAsync(
                    fence,
                    action,
                    cancellationToken),
            LifecyclePurgeProviderActionKind.Original =>
                await CheckOriginalActionAsync(
                    fence,
                    action,
                    cancellationToken),
            _ => new(
                LifecyclePurgeActionCheckStatus.Stale,
                "purge.action_invalid"),
        };
    }

    public async ValueTask<Result> RecordPurgeActionDeletedAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                fence.TenantId,
                cancellationToken);
        AssetLifecycleRow? lifecycle = await _context.AssetLifecycles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.AssetId == fence.AssetId,
                cancellationToken);
        if (lifecycle is null ||
            lifecycle.State != "Purging" ||
            lifecycle.ActivePurgeBatchId != fence.BatchId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure(LifecycleApplicationErrors.InvalidState);
        }

        if (action.Kind == LifecyclePurgeProviderActionKind.Derivative)
        {
            DerivativeRequestRow? derivative = await _context
                .Set<DerivativeRequestRow>()
                .SingleOrDefaultAsync(
                    row =>
                        row.AssetId == fence.AssetId &&
                        row.RepresentationStorageKey == action.Key.Value,
                    cancellationToken);
            if (derivative is not null)
            {
                if (!string.Equals(
                        derivative.CacheKey,
                        action.ExpectedVersion.Value,
                        StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(LifecycleApplicationErrors.DryRunStale);
                }

                derivative.RepresentationStorageKey = null;
                derivative.State = "Failed";
                derivative.FailureCode = "Purged";
                derivative.UpdatedAtUtc = deletedAtUtc;
                derivative.Version = checked(derivative.Version + 1);
            }
        }
        else
        {
            BlobRow? blob = await _context.Blobs
                .SingleOrDefaultAsync(
                    row =>
                        row.ObjectKey == action.Key.Value &&
                        row.ProviderVersion == action.ExpectedVersion.Value,
                    cancellationToken);
            if (blob is not null && blob.State != "Deleted")
            {
                blob.State = "Deleted";
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success();
    }

    public async ValueTask<Result<long>> CompletePurgeAssetAsync(
        LifecyclePurgeAssetFence fence,
        DateTimeOffset purgedAtUtc,
        DateTimeOffset backupExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                fence.TenantId,
                cancellationToken);
        LifecyclePurgeActionCheck? fenceCheck = await ValidateFenceAsync(
            fence,
            purgedAtUtc,
            cancellationToken);
        if (fenceCheck is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<long>(LifecycleApplicationErrors.PurgeBlocked);
        }

        List<DerivativeRequestRow> derivatives = await _context
            .Set<DerivativeRequestRow>()
            .Where(row => row.AssetId == fence.AssetId)
            .ToListAsync(cancellationToken);
        if (derivatives.Any(row => row.RepresentationStorageKey is not null))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<long>(LifecycleApplicationErrors.InvalidState);
        }

        List<Guid> blobIds = await _context.AssetRevisions
            .AsNoTracking()
            .Where(row => row.AssetId == fence.AssetId)
            .Select(row => row.BlobId)
            .Distinct()
            .ToListAsync(cancellationToken);
        List<BlobRow> blobs = await _context.Blobs
            .Where(row => blobIds.Contains(row.Id))
            .ToListAsync(cancellationToken);
        foreach (BlobRow blob in blobs)
        {
            bool externallyReferenced = await _context.AssetRevisions
                .AsNoTracking()
                .AnyAsync(
                    row =>
                        row.BlobId == blob.Id &&
                        row.AssetId != fence.AssetId,
                    cancellationToken);
            if (!externallyReferenced &&
                blob.State is not ("Deleted" or "Missing"))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<long>(LifecycleApplicationErrors.InvalidState);
            }
        }

        RelationshipSnapshot relationships =
            await LifecyclePersistenceSupport.LoadFrozenRelationshipsAsync(
                _context,
                fence.AssetId,
                cancellationToken);
        long reclaimedBytes = checked(
            derivatives
                .Where(row => row.FailureCode == "Purged")
                .Sum(row => row.RepresentationContentLength ?? 0) +
            blobs.Where(row => row.State is "Deleted" or "Missing")
                .Sum(row => row.SizeBytes));
        if (!await _context.DeletionTombstones.AnyAsync(
                row => row.FormerAssetId == fence.AssetId,
                cancellationToken))
        {
            _context.DeletionTombstones.Add(new DeletionTombstoneRow
            {
                FormerAssetId = fence.AssetId,
                TenantId = fence.TenantId,
                PurgedAtUtc = purgedAtUtc,
                BackupExpiresAtUtc = backupExpiresAtUtc,
                RelationshipCount = relationships.Count,
                RelationshipDigest = relationships.Digest,
            });
        }

        PurgeBatchRow batch = await _context.PurgeBatches
            .SingleAsync(row => row.Id == fence.BatchId, cancellationToken);
        PurgeBatchItemRow item = await _context.PurgeBatchItems
            .SingleAsync(
                row =>
                    row.PurgeBatchId == fence.BatchId &&
                    row.AssetId == fence.AssetId,
                cancellationToken);
        item.Result = "Purged";
        item.ReclaimedBytes = reclaimedBytes;
        LifecyclePersistenceSupport.AddAudit(
            _context,
            _ids,
            fence.TenantId,
            batch.ApprovedByUserId ?? batch.RequestedByUserId,
            "asset.purged",
            fence.AssetId,
            LifecyclePersistenceSupport.StateSummary(
                "Purging",
                relationships.Digest),
            LifecyclePersistenceSupport.StateSummary(
                "Purged",
                relationships.Digest,
                reclaimedBytes),
            "Succeeded",
            purgedAtUtc);

        Guid[] derivativeIds = derivatives.Select(row => row.Id).ToArray();
        await _context.Set<PublicDerivativeRouteRow>()
            .Where(row => derivativeIds.Contains(row.RequestId))
            .ExecuteDeleteAsync(cancellationToken);
        _context.Set<DerivativeRequestRow>().RemoveRange(derivatives);
        await _context.ShareAssets
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.AlbumItems
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.AssetTags
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.AssetFavorites
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.ResourceGrants
            .Where(row =>
                row.ResourceKind == "Asset" &&
                row.ResourceId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.AssetMetadataHistory
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.RetentionHolds
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.RelationshipSnapshots
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteDeleteAsync(cancellationToken);
        await _context.Albums
            .Where(row => row.CoverAssetId == fence.AssetId)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(row => row.CoverAssetId, (Guid?)null),
                cancellationToken);
        await _context.UploadSessions
            .Where(row => row.ActivatedAssetId == fence.AssetId)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(row => row.ActivatedAssetId, (Guid?)null)
                    .SetProperty(row => row.ActivatedRevisionId, (Guid?)null)
                    .SetProperty(row => row.ActivatedBlobId, (Guid?)null),
                cancellationToken);
        await _context.IngestOperations
            .Where(row => row.AssetId == fence.AssetId)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(row => row.AssetId, (Guid?)null)
                    .SetProperty(row => row.RevisionId, (Guid?)null)
                    .SetProperty(row => row.BlobId, (Guid?)null),
                cancellationToken);

        AssetRow asset = await _context.Assets
            .SingleAsync(row => row.Id == fence.AssetId, cancellationToken);
        asset.CurrentRevisionId = null;
        asset.Status = "Purged";
        asset.UpdatedAtUtc = purgedAtUtc;
        asset.Version = checked(asset.Version + 1);
        await _context.SaveChangesAsync(cancellationToken);
        _context.Assets.Remove(asset);
        await _context.SaveChangesAsync(cancellationToken);
        foreach (BlobRow blob in blobs.Where(row => row.State is "Deleted" or "Missing"))
        {
            bool referenced = await _context.AssetRevisions
                .AsNoTracking()
                .AnyAsync(row => row.BlobId == blob.Id, cancellationToken);
            if (!referenced)
            {
                _context.Blobs.Remove(blob);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success(reclaimedBytes);
    }

    public async ValueTask<Result> RecordPurgeItemResultAsync(
        Guid tenantId,
        Guid batchId,
        Guid assetId,
        LifecyclePurgeItemOutcome outcome,
        string errorCode,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        PurgeBatchItemRow? item = await _context.PurgeBatchItems
            .SingleOrDefaultAsync(
                row =>
                    row.PurgeBatchId == batchId &&
                    row.AssetId == assetId,
                cancellationToken);
        PurgeBatchRow? batch = await _context.PurgeBatches
            .SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken);
        if (item is null || batch is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure(LifecycleApplicationErrors.NotFound);
        }

        item.Result = outcome switch
        {
            LifecyclePurgeItemOutcome.Purged => "Purged",
            LifecyclePurgeItemOutcome.Blocked => "Blocked",
            LifecyclePurgeItemOutcome.Failed => "Failed",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        item.ReclaimedBytes = 0;
        AssetLifecycleRow? lifecycle = await _context.AssetLifecycles
            .SingleOrDefaultAsync(row => row.AssetId == assetId, cancellationToken);
        if (lifecycle is not null &&
            lifecycle.State == "Purging" &&
            lifecycle.ActivePurgeBatchId == batchId)
        {
            lifecycle.State = "Trashed";
            lifecycle.ActivePurgeBatchId = null;
            lifecycle.PurgeRequestedByUserId = null;
            lifecycle.PurgeInitiatorKind = null;
            lifecycle.PurgeEvaluatedAtUtc = null;
            lifecycle.PurgeObservedRevision = null;
            lifecycle.PurgeHasBlockingReferences = null;
            lifecycle.Version = checked(lifecycle.Version + 1);
        }

        LifecyclePersistenceSupport.AddAudit(
            _context,
            _ids,
            tenantId,
            batch.ApprovedByUserId ?? batch.RequestedByUserId,
            outcome == LifecyclePurgeItemOutcome.Blocked
                ? "asset.purge_blocked"
                : "asset.purge_failed",
            LifecyclePersistenceSupport.PurgeBatchItemResourceIdentifier(
                batchId,
                assetId),
            LifecyclePersistenceSupport.StateSummary("Purging"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["state"] = "Trashed",
                ["errorCode"] = errorCode,
            },
            outcome == LifecyclePurgeItemOutcome.Blocked ? "Rejected" : "Failed",
            recordedAtUtc,
            resourceType: "purgeBatchItem");
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success();
    }

    public async ValueTask<Result> CompletePurgeBatchAsync(
        Guid tenantId,
        Guid batchId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        PurgeBatchRow? batch = await _context.PurgeBatches
            .SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken);
        if (batch is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure(LifecycleApplicationErrors.NotFound);
        }

        if (batch.State == "Completed")
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Success();
        }

        if (batch.State != "Executing")
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure(LifecycleApplicationErrors.InvalidState);
        }

        List<Guid> failedRows = await _context.PurgeBatchItems
            .AsNoTracking()
            .Where(row =>
                row.PurgeBatchId == batchId &&
                row.Result == "Failed")
            .Select(row => row.AssetId)
            .ToListAsync(cancellationToken);
        string batchItemPrefix = $"{batchId:D}:";
        HashSet<string> terminalFailures = (await _context.AuditEvents
                .AsNoTracking()
                .Where(row =>
                    row.Action == "asset.purge_failed" &&
                    row.ResourceType == "purgeBatchItem" &&
                    row.ResourceIdentifier.StartsWith(batchItemPrefix))
                .Select(row => row.ResourceIdentifier)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (failedRows.Any(assetId => !terminalFailures.Contains(
                LifecyclePersistenceSupport.PurgeBatchItemResourceIdentifier(
                    batchId,
                    assetId))))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure(LifecycleApplicationErrors.InvalidState);
        }

        batch.State = "Completed";
        batch.CompletedAtUtc = completedAtUtc;
        batch.Version = checked(batch.Version + 1);
        LifecyclePersistenceSupport.AddAudit(
            _context,
            _ids,
            tenantId,
            batch.ApprovedByUserId ?? batch.RequestedByUserId,
            "purge.completed",
            batchId,
            LifecyclePersistenceSupport.StateSummary("Executing"),
            LifecyclePersistenceSupport.StateSummary("Completed"),
            "Succeeded",
            completedAtUtc,
            resourceType: "purgeBatch");
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success();
    }

    private async ValueTask<bool> HasValidApprovalAsync(
        PurgeBatchRow batch,
        CancellationToken cancellationToken)
    {
        if (batch.ApprovedByUserId is not { } approverId)
        {
            return false;
        }

        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            approverId,
            cancellationToken);
        return role == "TenantOwner";
    }

    private async ValueTask<List<LifecyclePurgeProviderAction>>
        LoadRemainingActionsAsync(
            Guid assetId,
            string storageProvider,
            CancellationToken cancellationToken)
    {
        var actions = new List<LifecyclePurgeProviderAction>();
        List<DerivativeRequestRow> derivatives = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(row =>
                row.AssetId == assetId &&
                row.RepresentationStorageKey != null)
            .OrderBy(row => row.RepresentationStorageKey)
            .ToListAsync(cancellationToken);
        actions.AddRange(derivatives.Select(row =>
            new LifecyclePurgeProviderAction(
                LifecyclePurgeProviderActionKind.Derivative,
                new BlobKey(row.RepresentationStorageKey!),
                new BlobVersion(row.CacheKey),
                row.RepresentationContentLength ?? 0)));

        List<Guid> blobIds = await _context.AssetRevisions
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .Select(row => row.BlobId)
            .Distinct()
            .ToListAsync(cancellationToken);
        List<BlobRow> blobs = await _context.Blobs
            .AsNoTracking()
            .Where(row => blobIds.Contains(row.Id))
            .OrderBy(row => row.ObjectKey)
            .ToListAsync(cancellationToken);
        foreach (BlobRow blob in blobs)
        {
            bool externallyReferenced = await _context.AssetRevisions
                .AsNoTracking()
                .AnyAsync(
                    row =>
                        row.BlobId == blob.Id &&
                        row.AssetId != assetId,
                    cancellationToken);
            if (externallyReferenced || blob.State is "Deleted" or "Missing")
            {
                continue;
            }

            if (!string.Equals(blob.Provider, storageProvider, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(blob.ProviderVersion))
            {
                throw new InvalidOperationException(
                    "Purge storage identity does not match the configured provider.");
            }

            actions.Add(new LifecyclePurgeProviderAction(
                LifecyclePurgeProviderActionKind.Original,
                new BlobKey(blob.ObjectKey),
                new BlobVersion(blob.ProviderVersion),
                blob.SizeBytes));
        }

        return actions;
    }

    private async ValueTask<LifecyclePurgeActionCheck?> ValidateFenceAsync(
        LifecyclePurgeAssetFence fence,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        PurgeBatchRow? batch = await _context.PurgeBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == fence.BatchId, cancellationToken);
        AssetLifecycleRow? lifecycle = await _context.AssetLifecycles
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.AssetId == fence.AssetId, cancellationToken);
        if (batch is null ||
            batch.State != "Executing" ||
            !await HasValidApprovalAsync(batch, cancellationToken) ||
            lifecycle is null ||
            lifecycle.State != "Purging" ||
            lifecycle.ActivePurgeBatchId != fence.BatchId)
        {
            return new(
                LifecyclePurgeActionCheckStatus.Blocked,
                "purge.invalid_state");
        }

        if (lifecycle.Version != fence.LifecycleVersion)
        {
            return new(
                LifecyclePurgeActionCheckStatus.Blocked,
                "purge.lifecycle_changed");
        }

        LifecyclePurgeCandidateState? candidate =
            await LifecyclePersistenceSupport.LoadPurgeCandidateAsync(
                _context,
                fence.AssetId,
                evaluatedAtUtc,
                cancellationToken);
        if (candidate is null)
        {
            return new(
                LifecyclePurgeActionCheckStatus.Stale,
                "purge.asset_missing");
        }

        if (candidate.RevisionNumber != fence.RevisionNumber ||
            candidate.FrozenRelationships.Digest != fence.RelationshipDigest)
        {
            return new(
                LifecyclePurgeActionCheckStatus.Stale,
                "purge.revision_changed");
        }

        return candidate.Eligible
            ? null
            : new(
                LifecyclePurgeActionCheckStatus.Blocked,
                LifecyclePersistenceSupport.BarrierErrorCode(candidate));
    }

    private async ValueTask<LifecyclePurgeActionCheck> CheckDerivativeActionAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        CancellationToken cancellationToken)
    {
        DerivativeRequestRow? row = await _context.Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.AssetId == fence.AssetId &&
                    candidate.RepresentationStorageKey == action.Key.Value,
                cancellationToken);
        if (row is null)
        {
            return new(LifecyclePurgeActionCheckStatus.AlreadyDeleted, null);
        }

        return string.Equals(
            row.CacheKey,
            action.ExpectedVersion.Value,
            StringComparison.Ordinal)
            ? new(LifecyclePurgeActionCheckStatus.Allowed, null)
            : new(
                LifecyclePurgeActionCheckStatus.Stale,
                "purge.derivative_changed");
    }

    private async ValueTask<LifecyclePurgeActionCheck> CheckOriginalActionAsync(
        LifecyclePurgeAssetFence fence,
        LifecyclePurgeProviderAction action,
        CancellationToken cancellationToken)
    {
        BlobRow? blob = await _context.Blobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.ObjectKey == action.Key.Value,
                cancellationToken);
        if (blob is null || blob.State is "Deleted" or "Missing")
        {
            return new(LifecyclePurgeActionCheckStatus.AlreadyDeleted, null);
        }

        bool externallyReferenced = await _context.AssetRevisions
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.BlobId == blob.Id &&
                    row.AssetId != fence.AssetId,
                cancellationToken);
        if (externallyReferenced)
        {
            return new(
                LifecyclePurgeActionCheckStatus.Blocked,
                "purge.blob_referenced");
        }

        return string.Equals(
            blob.ProviderVersion,
            action.ExpectedVersion.Value,
            StringComparison.Ordinal)
            ? new(LifecyclePurgeActionCheckStatus.Allowed, null)
            : new(
                LifecyclePurgeActionCheckStatus.Stale,
                "purge.original_changed");
    }
}
