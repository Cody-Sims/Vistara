using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Domain.Lifecycle;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Lifecycle;

public sealed class RelationalLifecycleStore(
    VistaraDbContext context,
    IUuid7Generator ids) : ILifecycleStore
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IUuid7Generator _ids =
        ids ?? throw new ArgumentNullException(nameof(ids));

    public async ValueTask<Result<LifecycleTrashPage>> ListTrashAsync(
        LifecycleTrashQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            query.ActorId,
            cancellationToken);
        if (role is null)
        {
            return Result.Failure<LifecycleTrashPage>(
                LifecycleApplicationErrors.Forbidden);
        }

        IQueryable<TrashEntryRow> trashQuery =
            _context.TrashEntries.AsNoTracking();
        if (query.Request.AfterDeletedAtUtc is { } afterDeletedAt &&
            query.Request.AfterAssetId is { } afterAssetId)
        {
            trashQuery = query.Request.Descending
                ? trashQuery.Where(row =>
                    row.DeletedAtUtc < afterDeletedAt ||
                    (row.DeletedAtUtc == afterDeletedAt &&
                     row.AssetId.CompareTo(afterAssetId) < 0))
                : trashQuery.Where(row =>
                    row.DeletedAtUtc > afterDeletedAt ||
                    (row.DeletedAtUtc == afterDeletedAt &&
                     row.AssetId.CompareTo(afterAssetId) > 0));
        }

        IQueryable<TrashEntryRow> pageQuery = query.Request.Descending
            ? trashQuery
                .OrderByDescending(row => row.DeletedAtUtc)
                .ThenByDescending(row => row.AssetId)
                .Take(query.Request.Limit + 1)
            : trashQuery
                .OrderBy(row => row.DeletedAtUtc)
                .ThenBy(row => row.AssetId)
                .Take(query.Request.Limit + 1);
        var joinedPage =
            from trash in pageQuery
            join asset in _context.Assets.AsNoTracking()
                on new { trash.TenantId, Id = trash.AssetId }
                equals new { asset.TenantId, asset.Id }
            join revision in _context.AssetRevisions.AsNoTracking()
                on new { asset.TenantId, Id = asset.CurrentRevisionId }
                equals new
                {
                    revision.TenantId,
                    Id = (Guid?)revision.Id,
                }
            join blob in _context.Blobs.AsNoTracking()
                on new { revision.TenantId, Id = revision.BlobId }
                equals new { blob.TenantId, blob.Id }
            select new
            {
                Trash = trash,
                Asset = asset,
                Revision = revision,
                Blob = blob,
            };
        var orderedJoined =
            query.Request.Descending
                ? joinedPage
                    .OrderByDescending(row => row.Trash.DeletedAtUtc)
                    .ThenByDescending(row => row.Trash.AssetId)
                : joinedPage
                    .OrderBy(row => row.Trash.DeletedAtUtc)
                    .ThenBy(row => row.Trash.AssetId);
        List<TrashListRow> selected = await orderedJoined
            .Select(row => new TrashListRow(
                row.Asset.Id,
                row.Asset.Title,
                row.Asset.Description,
                row.Asset.Visibility,
                row.Asset.CapturedAtUtc,
                row.Asset.CreatedAtUtc,
                row.Asset.UpdatedAtUtc,
                row.Asset.Version,
                row.Revision.RevisionNumber,
                row.Revision.DetectedContentType,
                row.Revision.DetectedFormat,
                row.Revision.Width,
                row.Revision.Height,
                row.Revision.BlobId,
                row.Blob.SizeBytes,
                row.Trash.DeletedAtUtc,
                row.Trash.PurgeAtUtc,
                row.Trash.Reason))
            .ToListAsync(cancellationToken);
        bool hasMore = selected.Count > query.Request.Limit;
        TrashListRow[] pageRows = selected
            .Take(query.Request.Limit)
            .ToArray();
        Guid[] assetIds = pageRows.Select(row => row.AssetId).ToArray();
        if (assetIds.Length == 0)
        {
            return Result.Success(new LifecycleTrashPage([], hasMore));
        }

        List<TrashTagRow> tagRows = await (
            from link in _context.AssetTags.AsNoTracking()
            join tag in _context.Tags.AsNoTracking()
                on new { link.TenantId, Id = link.TagId }
                equals new { tag.TenantId, tag.Id }
            where assetIds.Contains(link.AssetId)
            orderby tag.NormalizedName, tag.Id
            select new TrashTagRow(
                link.AssetId,
                tag.Id,
                tag.DisplayName,
                tag.Color))
            .ToListAsync(cancellationToken);
        Dictionary<Guid, IReadOnlyList<LifecycleTrashTagSnapshot>> tagsByAsset =
            tagRows
                .GroupBy(row => row.AssetId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<LifecycleTrashTagSnapshot>)group
                        .Select(row => new LifecycleTrashTagSnapshot(
                            row.TagId,
                            row.Name,
                            row.Color))
                        .ToArray());
        HashSet<Guid> favorites = (await _context.AssetFavorites
                .AsNoTracking()
                .Where(row =>
                    assetIds.Contains(row.AssetId) &&
                    row.UserId == query.ActorId)
                .Select(row => row.AssetId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        Dictionary<Guid, int> holdCounts = await _context.RetentionHolds
            .AsNoTracking()
            .Where(row =>
                assetIds.Contains(row.AssetId) &&
                row.ReleasedAtUtc == null)
            .GroupBy(row => row.AssetId)
            .ToDictionaryAsync(
                group => group.Key,
                group => group.Count(),
                cancellationToken);
        Dictionary<Guid, int> shareCounts = await (
            from shareAsset in _context.ShareAssets.AsNoTracking()
            join share in _context.Shares.AsNoTracking()
                on new { shareAsset.TenantId, Id = shareAsset.ShareId }
                equals new { share.TenantId, share.Id }
            where assetIds.Contains(shareAsset.AssetId) &&
                share.RevokedAtUtc == null &&
                (share.ExpiresAtUtc == null ||
                 share.ExpiresAtUtc > query.EvaluatedAtUtc)
            group shareAsset by shareAsset.AssetId
            into grouped
            select new
            {
                AssetId = grouped.Key,
                Count = grouped.Select(row => row.ShareId).Distinct().Count(),
            })
            .ToDictionaryAsync(
                row => row.AssetId,
                row => row.Count,
                cancellationToken);
        Dictionary<Guid, int> grantCounts = await _context.ResourceGrants
            .AsNoTracking()
            .Where(row =>
                row.ResourceKind == "Asset" &&
                assetIds.Contains(row.ResourceId) &&
                row.RevokedAtUtc == null)
            .GroupBy(row => row.ResourceId)
            .ToDictionaryAsync(
                group => group.Key,
                group => group.Count(),
                cancellationToken);
        Dictionary<Guid, long> derivativeBytes = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(row =>
                assetIds.Contains(row.AssetId) &&
                row.RepresentationStorageKey != null &&
                row.RepresentationContentLength != null)
            .GroupBy(row => row.AssetId)
            .ToDictionaryAsync(
                group => group.Key,
                group => group.Sum(row => row.RepresentationContentLength ?? 0),
                cancellationToken);
        List<AssetBlobReferenceRow> pageBlobReferences =
            await _context.AssetRevisions
                .AsNoTracking()
                .Where(row => assetIds.Contains(row.AssetId))
                .Select(row => new AssetBlobReferenceRow(row.AssetId, row.BlobId))
                .Distinct()
                .ToListAsync(cancellationToken);
        Guid[] blobIds = pageBlobReferences
            .Select(row => row.BlobId)
            .Distinct()
            .ToArray();
        Dictionary<Guid, long> blobSizes = await _context.Blobs
            .AsNoTracking()
            .Where(row => blobIds.Contains(row.Id))
            .ToDictionaryAsync(
                row => row.Id,
                row => row.SizeBytes,
                cancellationToken);
        List<AssetBlobReferenceRow> allBlobReferences =
            await _context.AssetRevisions
                .AsNoTracking()
                .Where(row => blobIds.Contains(row.BlobId))
                .Select(row => new AssetBlobReferenceRow(row.AssetId, row.BlobId))
                .Distinct()
                .ToListAsync(cancellationToken);
        Dictionary<Guid, int> blobAssetCounts = allBlobReferences
            .GroupBy(row => row.BlobId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.AssetId).Distinct().Count());
        Dictionary<Guid, long> originalBytes = pageBlobReferences
            .Where(row => blobAssetCounts.GetValueOrDefault(row.BlobId) == 1)
            .GroupBy(row => row.AssetId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => blobSizes.GetValueOrDefault(row.BlobId)));

        var items = new List<LifecycleTrashItemSnapshot>(pageRows.Length);
        foreach (TrashListRow row in pageRows)
        {
            items.Add(new LifecycleTrashItemSnapshot(
                row.AssetId,
                row.Title,
                row.Description,
                "trashed",
                row.Visibility.ToLowerInvariant(),
                row.RevisionNumber,
                row.ContentType,
                row.Format,
                row.Width,
                row.Height,
                row.SizeBytes,
                row.CapturedAtUtc,
                row.ImportedAtUtc,
                row.UpdatedAtUtc,
                favorites.Contains(row.AssetId),
                tagsByAsset.GetValueOrDefault(row.AssetId) ?? [],
                row.Version,
                row.DeletedAtUtc,
                row.PurgeAtUtc,
                row.Reason,
                holdCounts.GetValueOrDefault(row.AssetId),
                checked(
                    shareCounts.GetValueOrDefault(row.AssetId) +
                    grantCounts.GetValueOrDefault(row.AssetId)),
                checked(
                    derivativeBytes.GetValueOrDefault(row.AssetId) +
                    originalBytes.GetValueOrDefault(row.AssetId))));
        }

        return Result.Success(new LifecycleTrashPage(items, hasMore));
    }

    public async ValueTask<Result<IReadOnlyList<LifecycleAssetMutationResult>>> TrashAsync(
        LifecycleTrashCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        try
        {
            string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
                _context,
                command.ActorId,
                cancellationToken);
            var results = new List<LifecycleAssetMutationResult>(command.Targets.Count);
            foreach (LifecycleAssetTarget target in command.Targets)
            {
                AssetRow? asset = await _context.Assets
                    .SingleOrDefaultAsync(
                        row => row.Id == target.AssetId,
                        cancellationToken);
                if (asset is null)
                {
                    results.Add(new(target.AssetId, "notFound", target.Version, "lifecycle.not_found"));
                    continue;
                }

                if (!LifecyclePersistenceSupport.CanMutateOwnedAsset(
                        role,
                        command.ActorId,
                        asset.OwnerId))
                {
                    results.Add(new(target.AssetId, "forbidden", asset.Version, "lifecycle.forbidden"));
                    continue;
                }

                if (asset.Status == "Trashed")
                {
                    results.Add(new(target.AssetId, "alreadyTrashed", asset.Version, null));
                    continue;
                }

                if (asset.Version != target.Version)
                {
                    results.Add(new(
                        target.AssetId,
                        "versionConflict",
                        asset.Version,
                        "lifecycle.version_conflict"));
                    continue;
                }

                if (asset.Status != "Ready" || asset.CurrentRevisionId is null)
                {
                    results.Add(new(
                        target.AssetId,
                        "invalidState",
                        asset.Version,
                        "lifecycle.invalid_state"));
                    continue;
                }

                AssetRevisionRow revision = await _context.AssetRevisions
                    .SingleAsync(
                        row => row.Id == asset.CurrentRevisionId,
                        cancellationToken);
                RelationshipSnapshot relationships =
                    await LifecyclePersistenceSupport.LoadLiveRelationshipsAsync(
                        _context,
                        asset.Id,
                        cancellationToken);
                AssetLifecycleRow? lifecycle = await _context.AssetLifecycles
                    .SingleOrDefaultAsync(
                        row => row.AssetId == asset.Id,
                        cancellationToken);
                if (lifecycle is null)
                {
                    lifecycle = new AssetLifecycleRow
                    {
                        AssetId = asset.Id,
                        TenantId = command.TenantId,
                        CurrentRevision = revision.RevisionNumber,
                        State = "Ready",
                        Version = 1,
                    };
                    _context.AssetLifecycles.Add(lifecycle);
                }

                List<RelationshipSnapshotRow> previous =
                    await _context.RelationshipSnapshots
                        .Where(row => row.AssetId == asset.Id)
                        .ToListAsync(cancellationToken);
                _context.RelationshipSnapshots.RemoveRange(previous);
                foreach (RelationshipReference relationship in relationships.Relationships)
                {
                    _context.RelationshipSnapshots.Add(new RelationshipSnapshotRow
                    {
                        TenantId = command.TenantId,
                        AssetId = asset.Id,
                        Kind = relationship.Kind.ToString(),
                        ResourceId = relationship.ResourceId,
                    });
                }

                TrashEntryRow? existingTrash = await _context.TrashEntries
                    .SingleOrDefaultAsync(
                        row => row.AssetId == asset.Id,
                        cancellationToken);
                if (existingTrash is not null)
                {
                    _context.TrashEntries.Remove(existingTrash);
                }

                _context.TrashEntries.Add(new TrashEntryRow
                {
                    TenantId = command.TenantId,
                    AssetId = asset.Id,
                    DeletedByUserId = command.ActorId,
                    DeletedAtUtc = command.DeletedAtUtc,
                    PurgeAtUtc = command.PurgeAtUtc,
                    Reason = command.Reason,
                });
                string beforeState = asset.Status;
                asset.Status = "Trashed";
                asset.UpdatedAtUtc = command.DeletedAtUtc;
                asset.Version = checked(asset.Version + 1);
                lifecycle.CurrentRevision = revision.RevisionNumber;
                lifecycle.State = "Trashed";
                lifecycle.HasBeenTrashed = true;
                lifecycle.ActivePurgeBatchId = null;
                lifecycle.PurgeRequestedByUserId = null;
                lifecycle.PurgeInitiatorKind = null;
                lifecycle.PurgeEvaluatedAtUtc = null;
                lifecycle.PurgeObservedRevision = null;
                lifecycle.PurgeHasBlockingReferences = null;
                lifecycle.Version = checked(lifecycle.Version + 1);
                LifecyclePersistenceSupport.AddAudit(
                    _context,
                    _ids,
                    command.TenantId,
                    command.ActorId,
                    "asset.trashed",
                    asset.Id,
                    LifecyclePersistenceSupport.StateSummary(
                        beforeState,
                        relationships.Digest),
                    LifecyclePersistenceSupport.StateSummary(
                        "Trashed",
                        relationships.Digest),
                    "Succeeded",
                    command.DeletedAtUtc);
                results.Add(new(asset.Id, "trashed", asset.Version, null));
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(results);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            return Result.Failure<IReadOnlyList<LifecycleAssetMutationResult>>(
                LifecycleApplicationErrors.VersionConflict);
        }
    }

    public async ValueTask<Result<LifecycleJobSubmission>> SubmitRestoreAsync(
        LifecycleRestoreCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        string requestHash = LifecyclePersistenceSupport.HashRequest(
            "restore",
            command.Targets);
        string storageKey = LifecyclePersistenceSupport.IdempotencyStorageKey(
            "lifecycle.restore",
            command.IdempotencyKey);
        IdempotencyRequestRow? existing = await _context.IdempotencyRequests
            .SingleOrDefaultAsync(
                row =>
                    row.PrincipalId == command.ActorId &&
                    row.Key == storageKey,
                cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal) ||
                !Guid.TryParse(existing.ResponseReference, out Guid existingJobId))
            {
                return Result.Failure<LifecycleJobSubmission>(
                    LifecycleApplicationErrors.IdempotencyConflict);
            }

            return Result.Success(new LifecycleJobSubmission(
                existingJobId,
                "queued",
                command.Targets.Count,
                command.SubmittedAtUtc,
                Replayed: true));
        }

        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            command.ActorId,
            cancellationToken);
        foreach (LifecycleAssetTarget target in command.Targets)
        {
            AssetRow? asset = await _context.Assets
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == target.AssetId,
                    cancellationToken);
            if (asset is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecycleJobSubmission>(
                    LifecycleApplicationErrors.NotFound);
            }

            if (!LifecyclePersistenceSupport.CanMutateOwnedAsset(
                    role,
                    command.ActorId,
                    asset.OwnerId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecycleJobSubmission>(
                    LifecycleApplicationErrors.Forbidden);
            }

            if (asset.Status != "Trashed")
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecycleJobSubmission>(
                    LifecycleApplicationErrors.InvalidState);
            }

            if (asset.Version != target.Version)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecycleJobSubmission>(
                    LifecycleApplicationErrors.VersionConflict);
            }
        }

        var payload = new LifecycleRestoreJobPayload(
            command.TenantId,
            command.ActorId,
            command.Targets);
        DurableJob job = DurableJob.Create(
            new JobId(command.JobId),
            new JobTenantId(command.TenantId),
            LifecycleJobContracts.RestoreType,
            LifecycleJobContracts.SerializeRestore(payload),
            LifecycleJobContracts.PayloadVersion,
            new JobDedupeKey($"lifecycle:restore:{requestHash}"),
            priority: 0,
            maxAttempts: 5,
            availableAtUtc: command.SubmittedAtUtc,
            createdAtUtc: command.SubmittedAtUtc);
        _context.Jobs.Add(JobMapper.ToRow(job));
        _context.IdempotencyRequests.Add(new IdempotencyRequestRow
        {
            TenantId = command.TenantId,
            PrincipalId = command.ActorId,
            Key = storageKey,
            RequestHash = requestHash,
            ResponseReference = command.JobId.ToString("D"),
            ExpiresAtUtc = command.SubmittedAtUtc.AddDays(1),
        });
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success(new LifecycleJobSubmission(
            command.JobId,
            "queued",
            command.Targets.Count,
            command.SubmittedAtUtc,
            Replayed: false));
    }

    public async ValueTask<Result<LifecyclePurgeDryRunSnapshot>>
        CreatePurgeDryRunAsync(
            LifecycleCreatePurgeDryRunCommand command,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        string requestHash = LifecyclePersistenceSupport.HashRequest(
            "purge-dry-run",
            command.Targets);
        string storageKey = LifecyclePersistenceSupport.IdempotencyStorageKey(
            "lifecycle.purge.dry-run",
            command.IdempotencyKey);
        IdempotencyRequestRow? existing = await _context.IdempotencyRequests
            .SingleOrDefaultAsync(
                row =>
                    row.PrincipalId == command.ActorId &&
                    row.Key == storageKey,
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal) ||
                !Guid.TryParse(existing.ResponseReference, out Guid existingBatchId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecyclePurgeDryRunSnapshot>(
                    LifecycleApplicationErrors.IdempotencyConflict);
            }

            PurgeBatchRow? replayedBatch = await _context.PurgeBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == existingBatchId,
                    cancellationToken);
            if (replayedBatch is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecyclePurgeDryRunSnapshot>(
                    LifecycleApplicationErrors.NotFound);
            }

            LifecyclePurgeDryRunSnapshot replayed =
                await BuildDryRunSnapshotAsync(
                    replayedBatch,
                    replayed: true,
                    cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return Result.Success(replayed);
        }

        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            command.ActorId,
            cancellationToken);
        if (role != "TenantOwner")
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeDryRunSnapshot>(
                LifecycleApplicationErrors.Forbidden);
        }

        var candidates = new List<LifecyclePurgeCandidateState>(
            command.Targets.Count);
        foreach (LifecycleAssetTarget target in command.Targets)
        {
            LifecyclePurgeCandidateState? candidate =
                await LifecyclePersistenceSupport.LoadPurgeCandidateAsync(
                    _context,
                    target.AssetId,
                    command.RequestedAtUtc,
                    cancellationToken);
            if (candidate is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecyclePurgeDryRunSnapshot>(
                    LifecycleApplicationErrors.NotFound);
            }

            if (candidate.Asset.Version != target.Version)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecyclePurgeDryRunSnapshot>(
                    LifecycleApplicationErrors.VersionConflict);
            }

            candidates.Add(candidate);
        }

        string digest = LifecyclePersistenceSupport.ComputePurgeDigest(
            command.TenantId,
            command.BatchId,
            command.ExpiresAtUtc,
            candidates);
        var batch = new PurgeBatchRow
        {
            Id = command.BatchId,
            TenantId = command.TenantId,
            RequestedByUserId = command.ActorId,
            RequestedAtUtc = command.RequestedAtUtc,
            DryRunHash = digest,
            DryRunCompletedAtUtc = command.RequestedAtUtc,
            CandidateCount = candidates.Count,
            EligibleCount = candidates.Count(candidate => candidate.Eligible),
            State = "DryRunCompleted",
            Version = 2,
        };
        _context.PurgeBatches.Add(batch);
        foreach (LifecyclePurgeCandidateState candidate in candidates)
        {
            _context.PurgeBatchItems.Add(new PurgeBatchItemRow
            {
                TenantId = command.TenantId,
                PurgeBatchId = command.BatchId,
                AssetId = candidate.Asset.Id,
                Revision = candidate.RevisionNumber,
                Result = candidate.Eligible ? "Failed" : "Blocked",
                ReclaimedBytes = 0,
            });
        }

        _context.IdempotencyRequests.Add(new IdempotencyRequestRow
        {
            TenantId = command.TenantId,
            PrincipalId = command.ActorId,
            Key = storageKey,
            RequestHash = requestHash,
            ResponseReference = command.BatchId.ToString("D"),
            ExpiresAtUtc = command.RequestedAtUtc.AddDays(1),
        });
        LifecyclePersistenceSupport.AddAudit(
            _context,
            _ids,
            command.TenantId,
            command.ActorId,
            "purge.dry_run_created",
            command.BatchId,
            LifecyclePersistenceSupport.StateSummary("Draft"),
            LifecyclePersistenceSupport.StateSummary("DryRunCompleted", digest),
            "Succeeded",
            command.RequestedAtUtc,
            resourceType: "purgeBatch");
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success(ToDryRunSnapshot(
            batch,
            command.ExpiresAtUtc,
            candidates,
            replayed: false));
    }

    public async ValueTask<Result<LifecyclePurgeBatchSnapshot>> ConfirmPurgeAsync(
        LifecycleConfirmPurgeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        string requestHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{command.BatchId:N}:{command.ExpectedVersion}:{command.DryRunDigest}")));
        string storageKey = LifecyclePersistenceSupport.IdempotencyStorageKey(
            "lifecycle.purge.confirm",
            command.IdempotencyKey);
        IdempotencyRequestRow? existing = await _context.IdempotencyRequests
            .SingleOrDefaultAsync(
                row =>
                    row.PrincipalId == command.ActorId &&
                    row.Key == storageKey,
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal) ||
                !Guid.TryParse(existing.ResponseReference, out Guid existingBatchId) ||
                existingBatchId != command.BatchId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecyclePurgeBatchSnapshot>(
                    LifecycleApplicationErrors.IdempotencyConflict);
            }

            PurgeBatchRow? replayedBatch = await _context.PurgeBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == command.BatchId,
                    cancellationToken);
            if (replayedBatch is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecyclePurgeBatchSnapshot>(
                    LifecycleApplicationErrors.NotFound);
            }

            LifecyclePurgeBatchSnapshot replayed =
                await BuildBatchSnapshotAsync(
                    replayedBatch,
                    replayed: true,
                    cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return Result.Success(replayed);
        }

        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            command.ActorId,
            cancellationToken);
        if (role != "TenantOwner")
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.Forbidden);
        }

        PurgeBatchRow? batch = await _context.PurgeBatches
            .SingleOrDefaultAsync(
                row => row.Id == command.BatchId,
                cancellationToken);
        if (batch is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.NotFound);
        }

        if (batch.RequestedByUserId == command.ActorId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.SeparateApproverRequired);
        }

        if (batch.State != "DryRunCompleted")
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.InvalidState);
        }

        if (batch.Version != command.ExpectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.VersionConflict);
        }

        DateTimeOffset expiresAt = (batch.DryRunCompletedAtUtc ??
            batch.RequestedAtUtc).Add(LifecycleService.PurgeDryRunLifetime);
        if (command.ConfirmedAtUtc > expiresAt)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.DryRunExpired);
        }

        if (batch.DryRunHash is null ||
            !LifecyclePersistenceSupport.FixedTimeEqualsHex(
                batch.DryRunHash,
                command.DryRunDigest))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.DryRunStale);
        }

        List<PurgeBatchItemRow> itemRows = await _context.PurgeBatchItems
            .AsNoTracking()
            .Where(row => row.PurgeBatchId == batch.Id)
            .OrderBy(row => row.AssetId)
            .ToListAsync(cancellationToken);
        var candidates = new List<LifecyclePurgeCandidateState>(itemRows.Count);
        foreach (PurgeBatchItemRow item in itemRows)
        {
            LifecyclePurgeCandidateState? candidate =
                await LifecyclePersistenceSupport.LoadPurgeCandidateAsync(
                    _context,
                    item.AssetId,
                    command.ConfirmedAtUtc,
                    cancellationToken);
            if (candidate is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecyclePurgeBatchSnapshot>(
                    LifecycleApplicationErrors.DryRunStale);
            }

            candidates.Add(candidate);
        }

        string currentDigest = LifecyclePersistenceSupport.ComputePurgeDigest(
            command.TenantId,
            command.BatchId,
            expiresAt,
            candidates);
        if (!LifecyclePersistenceSupport.FixedTimeEqualsHex(
                currentDigest,
                batch.DryRunHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.DryRunStale);
        }

        if (batch.EligibleCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.PurgeBlocked);
        }

        batch.ApprovedByUserId = command.ActorId;
        batch.ApprovedAtUtc = command.ConfirmedAtUtc;
        batch.State = "Approved";
        batch.Version = checked(batch.Version + 1);
        var payload = new LifecyclePurgeJobPayload(
            command.TenantId,
            command.BatchId);
        DurableJob job = DurableJob.Create(
            new JobId(command.JobId),
            new JobTenantId(command.TenantId),
            LifecycleJobContracts.PurgeType,
            LifecycleJobContracts.SerializePurge(payload),
            LifecycleJobContracts.PayloadVersion,
            new JobDedupeKey($"lifecycle:purge:{command.BatchId:N}"),
            priority: 10,
            maxAttempts: 10,
            availableAtUtc: command.ConfirmedAtUtc,
            createdAtUtc: command.ConfirmedAtUtc);
        _context.Jobs.Add(JobMapper.ToRow(job));
        _context.IdempotencyRequests.Add(new IdempotencyRequestRow
        {
            TenantId = command.TenantId,
            PrincipalId = command.ActorId,
            Key = storageKey,
            RequestHash = requestHash,
            ResponseReference = command.BatchId.ToString("D"),
            ExpiresAtUtc = command.ConfirmedAtUtc.AddDays(1),
        });
        LifecyclePersistenceSupport.AddAudit(
            _context,
            _ids,
            command.TenantId,
            command.ActorId,
            "purge.approved",
            command.BatchId,
            LifecyclePersistenceSupport.StateSummary(
                "DryRunCompleted",
                batch.DryRunHash),
            LifecyclePersistenceSupport.StateSummary("Approved", batch.DryRunHash),
            "Succeeded",
            command.ConfirmedAtUtc,
            resourceType: "purgeBatch");
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success(
            await BuildBatchSnapshotAsync(
                batch,
                replayed: false,
                cancellationToken));
    }

    public async ValueTask<Result<LifecyclePurgeBatchSnapshot>> GetPurgeBatchAsync(
        Guid tenantId,
        Guid actorId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            actorId,
            cancellationToken);
        if (role != "TenantOwner")
        {
            return Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.Forbidden);
        }

        PurgeBatchRow? batch = await _context.PurgeBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken);
        return batch is null
            ? Result.Failure<LifecyclePurgeBatchSnapshot>(
                LifecycleApplicationErrors.NotFound)
            : Result.Success(
                await BuildBatchSnapshotAsync(
                    batch,
                    replayed: false,
                    cancellationToken));
    }

    public async ValueTask<Result<LifecycleHoldSnapshot>> PlaceHoldAsync(
        LifecyclePlaceHoldCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Reason.Length > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            command.ActorId,
            cancellationToken);
        if (role is not ("TenantOwner" or "TenantAdmin"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecycleHoldSnapshot>(
                LifecycleApplicationErrors.Forbidden);
        }

        AssetRow? asset = await _context.Assets
            .SingleOrDefaultAsync(
                row => row.Id == command.AssetId,
                cancellationToken);
        if (asset is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecycleHoldSnapshot>(
                LifecycleApplicationErrors.NotFound);
        }

        AssetLifecycleRow? lifecycle = await _context.AssetLifecycles
            .SingleOrDefaultAsync(
                row => row.AssetId == command.AssetId,
                cancellationToken);
        if (lifecycle is null)
        {
            AssetRevisionRow? revision = asset.CurrentRevisionId is null
                ? null
                : await _context.AssetRevisions.SingleOrDefaultAsync(
                    row => row.Id == asset.CurrentRevisionId,
                    cancellationToken);
            if (revision is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure<LifecycleHoldSnapshot>(
                    LifecycleApplicationErrors.InvalidState);
            }

            lifecycle = new AssetLifecycleRow
            {
                AssetId = asset.Id,
                TenantId = command.TenantId,
                CurrentRevision = revision.RevisionNumber,
                State = asset.Status == "Trashed" ? "Trashed" : "Ready",
                HasBeenTrashed = asset.Status == "Trashed",
                Version = 1,
            };
            _context.AssetLifecycles.Add(lifecycle);
        }

        var hold = new RetentionHoldRow
        {
            Id = command.HoldId,
            TenantId = command.TenantId,
            AssetId = command.AssetId,
            Reason = command.Reason,
            CreatedByUserId = command.ActorId,
            CreatedAtUtc = command.CreatedAtUtc,
            Version = 1,
        };
        _context.RetentionHolds.Add(hold);
        lifecycle.Version = checked(lifecycle.Version + 1);
        LifecyclePersistenceSupport.AddAudit(
            _context,
            _ids,
            command.TenantId,
            command.ActorId,
            "retention_hold.placed",
            command.AssetId,
            LifecyclePersistenceSupport.StateSummary(lifecycle.State),
            LifecyclePersistenceSupport.StateSummary(lifecycle.State),
            "Succeeded",
            command.CreatedAtUtc);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        return Result.Success(new LifecycleHoldSnapshot(
            hold.Id,
            hold.AssetId,
            hold.Reason,
            hold.CreatedAtUtc,
            Active: true,
            hold.Version));
    }

    public async ValueTask<Result<LifecycleHoldSnapshot>> ReleaseHoldAsync(
        LifecycleReleaseHoldCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        string? role = await LifecyclePersistenceSupport.GetActiveRoleAsync(
            _context,
            command.ActorId,
            cancellationToken);
        if (role is not ("TenantOwner" or "TenantAdmin"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecycleHoldSnapshot>(
                LifecycleApplicationErrors.Forbidden);
        }

        RetentionHoldRow? hold = await _context.RetentionHolds
            .SingleOrDefaultAsync(
                row => row.Id == command.HoldId,
                cancellationToken);
        if (hold is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<LifecycleHoldSnapshot>(
                LifecycleApplicationErrors.NotFound);
        }

        if (hold.ReleasedAtUtc is null)
        {
            hold.ReleasedAtUtc = command.ReleasedAtUtc;
            hold.ReleasedByUserId = command.ActorId;
            hold.Version = checked(hold.Version + 1);
            AssetLifecycleRow? lifecycle = await _context.AssetLifecycles
                .SingleOrDefaultAsync(
                    row => row.AssetId == hold.AssetId,
                    cancellationToken);
            if (lifecycle is not null)
            {
                lifecycle.Version = checked(lifecycle.Version + 1);
            }

            LifecyclePersistenceSupport.AddAudit(
                _context,
                _ids,
                command.TenantId,
                command.ActorId,
                "retention_hold.released",
                hold.AssetId,
                LifecyclePersistenceSupport.StateSummary(lifecycle?.State ?? "Unknown"),
                LifecyclePersistenceSupport.StateSummary(lifecycle?.State ?? "Unknown"),
                "Succeeded",
                command.ReleasedAtUtc);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        _context.ChangeTracker.Clear();
        return Result.Success(new LifecycleHoldSnapshot(
            hold.Id,
            hold.AssetId,
            hold.Reason,
            hold.CreatedAtUtc,
            Active: false,
            hold.Version));
    }

    private async ValueTask<LifecyclePurgeDryRunSnapshot> BuildDryRunSnapshotAsync(
        PurgeBatchRow batch,
        bool replayed,
        CancellationToken cancellationToken)
    {
        List<PurgeBatchItemRow> items = await _context.PurgeBatchItems
            .AsNoTracking()
            .Where(row => row.PurgeBatchId == batch.Id)
            .OrderBy(row => row.AssetId)
            .ToListAsync(cancellationToken);
        DateTimeOffset evaluatedAt = batch.DryRunCompletedAtUtc ?? batch.RequestedAtUtc;
        var candidates = new List<LifecyclePurgeCandidateState>(items.Count);
        foreach (PurgeBatchItemRow item in items)
        {
            LifecyclePurgeCandidateState? candidate =
                await LifecyclePersistenceSupport.LoadPurgeCandidateAsync(
                    _context,
                    item.AssetId,
                    evaluatedAt,
                    cancellationToken);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return ToDryRunSnapshot(
            batch,
            evaluatedAt.Add(LifecycleService.PurgeDryRunLifetime),
            candidates,
            replayed);
    }

    private static LifecyclePurgeDryRunSnapshot ToDryRunSnapshot(
        PurgeBatchRow batch,
        DateTimeOffset expiresAtUtc,
        IReadOnlyList<LifecyclePurgeCandidateState> candidates,
        bool replayed) =>
        new(
            batch.Id,
            "dryRunCompleted",
            batch.DryRunHash ?? string.Empty,
            expiresAtUtc,
            batch.CandidateCount,
            batch.EligibleCount,
            candidates.Where(candidate => candidate.Eligible)
                .Sum(candidate => candidate.EstimatedReclaimBytes),
            candidates.Select(candidate => new LifecyclePurgeCandidateSnapshot(
                candidate.Asset.Id,
                candidate.RevisionNumber,
                candidate.Asset.Title,
                candidate.Eligible,
                candidate.Barriers.ToArray(),
                candidate.SharedLinkImpact,
                candidate.EstimatedReclaimBytes)).ToArray(),
            batch.Version,
            replayed);

    private async ValueTask<LifecyclePurgeBatchSnapshot> BuildBatchSnapshotAsync(
        PurgeBatchRow batch,
        bool replayed,
        CancellationToken cancellationToken)
    {
        List<PurgeBatchItemRow> rows = await _context.PurgeBatchItems
            .AsNoTracking()
            .Where(row => row.PurgeBatchId == batch.Id)
            .OrderBy(row => row.AssetId)
            .ToListAsync(cancellationToken);
        DateTimeOffset evaluatedAt = batch.CompletedAtUtc ??
            batch.StartedAtUtc ??
            batch.ApprovedAtUtc ??
            batch.RequestedAtUtc;
        var items = new List<LifecyclePurgeItemSnapshot>(rows.Count);
        foreach (PurgeBatchItemRow row in rows)
        {
            string itemIdentifier =
                LifecyclePersistenceSupport.PurgeBatchItemResourceIdentifier(
                    batch.Id,
                    row.AssetId);
            AuditEventRow? terminalAudit = await _context.AuditEvents
                .AsNoTracking()
                .Where(candidate =>
                    candidate.ResourceType == "purgeBatchItem" &&
                    candidate.ResourceIdentifier == itemIdentifier &&
                    (candidate.Action == "asset.purge_failed" ||
                     candidate.Action == "asset.purge_blocked"))
                .OrderByDescending(candidate => candidate.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            string result = row.Result == "Failed" && terminalAudit is null
                ? "pending"
                : row.Result.ToLowerInvariant();
            string? errorCode = null;
            if (terminalAudit is not null)
            {
                errorCode = LifecyclePersistenceSupport.ReadAuditErrorCode(
                    terminalAudit.AfterJson);
            }
            else if (result == "blocked")
            {
                LifecyclePurgeCandidateState? candidate =
                    await LifecyclePersistenceSupport.LoadPurgeCandidateAsync(
                        _context,
                        row.AssetId,
                        evaluatedAt,
                        cancellationToken);
                errorCode = candidate is null
                    ? "purge.blocked"
                    : LifecyclePersistenceSupport.BarrierErrorCode(candidate);
            }
            items.Add(new LifecyclePurgeItemSnapshot(
                row.AssetId,
                row.Revision,
                result,
                row.ReclaimedBytes,
                errorCode));
        }

        return new LifecyclePurgeBatchSnapshot(
            batch.Id,
            batch.State switch
            {
                "DryRunCompleted" => "dryRunCompleted",
                "Approved" => "approved",
                "Executing" => "executing",
                "Completed" => "completed",
                "Cancelled" => "cancelled",
                _ => "draft",
            },
            batch.RequestedAtUtc,
            batch.ApprovedAtUtc,
            batch.StartedAtUtc,
            batch.CompletedAtUtc,
            batch.CandidateCount,
            batch.EligibleCount,
            items.Count(item => item.Result != "pending"),
            rows.Sum(row => row.ReclaimedBytes),
            items,
            batch.Version,
            replayed);
    }

    private sealed record TrashListRow(
        Guid AssetId,
        string Title,
        string? Description,
        string Visibility,
        DateTimeOffset? CapturedAtUtc,
        DateTimeOffset ImportedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        long Version,
        long RevisionNumber,
        string ContentType,
        string Format,
        int Width,
        int Height,
        Guid BlobId,
        long SizeBytes,
        DateTimeOffset DeletedAtUtc,
        DateTimeOffset PurgeAtUtc,
        string Reason);

    private sealed record TrashTagRow(
        Guid AssetId,
        Guid TagId,
        string Name,
        string? Color);

    private sealed record AssetBlobReferenceRow(
        Guid AssetId,
        Guid BlobId);
}
