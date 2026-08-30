using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Lifecycle;
using Vistara.Persistence.Model;
using Vistara.IntegrationTests.Persistence;
using Vistara.Worker.Features.Lifecycle;
using Xunit;

namespace Vistara.IntegrationTests.Lifecycle;

public sealed class LifecyclePurgeTests
{
    private static readonly DateTimeOffset Now =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public async Task Purge_approval_is_two_step_and_new_hold_blocks_worker_before_delete()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(100);
        Guid requesterId = LifecyclePersistenceTests.Id(101);
        Guid approverId = LifecyclePersistenceTests.Id(102);
        Guid assetId = LifecyclePersistenceTests.Id(103);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset seeded =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                requesterId,
                assetId,
                includeRelationships: false);
        await SeedActorAsync(database.Context, tenantId, approverId);
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(1));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleActorContext requester = Human(tenantId, requesterId, clock.UtcNow);

        LifecycleAssetMutationResult trashed = Assert.Single(
            LifecyclePersistenceTests.Required(
                await service.TrashAsync(
                    requester,
                    [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                    "retention cleanup",
                    CancellationToken.None)));
        clock.Advance(TimeSpan.FromDays(31));
        requester = Human(tenantId, requesterId, clock.UtcNow);
        LifecyclePurgeDryRunSnapshot dryRun =
            LifecyclePersistenceTests.Required(
                await service.CreatePurgeDryRunAsync(
                    requester,
                    [new LifecycleAssetTarget(assetId, trashed.Version)],
                    "purge-dry-run",
                    CancellationToken.None));

        Result<LifecyclePurgeBatchSnapshot> selfApproval =
            await service.ConfirmPurgeAsync(
                requester,
                dryRun.BatchId,
                dryRun.Version,
                dryRun.DryRunDigest,
                "purge-self-confirm",
                CancellationToken.None);
        Assert.Equal(
            "lifecycle.separate_approver_required",
            selfApproval.Error?.Code);

        LifecycleActorContext approver = Human(
            tenantId,
            approverId,
            clock.UtcNow);
        LifecyclePurgeBatchSnapshot approved =
            LifecyclePersistenceTests.Required(
                await service.ConfirmPurgeAsync(
                    approver,
                    dryRun.BatchId,
                    dryRun.Version,
                    dryRun.DryRunDigest,
                    "purge-confirm",
                    CancellationToken.None));
        Assert.Equal("approved", approved.State);
        LifecycleHoldSnapshot hold = LifecyclePersistenceTests.Required(
            await service.PlaceHoldAsync(
                approver,
                assetId,
                "legal matter with private details",
                CancellationToken.None));
        Assert.True(hold.Active);

        var storage = new ScriptedBlobStore();
        await AddOriginalToStorageAsync(database.Context, storage, seeded.BlobId);
        await using VistaraDbContext workerContext =
            database.CreateContext(tenantId);
        var worker = new LifecyclePurgeService(
            new RelationalLifecycleWorkerStore(workerContext, ids),
            storage,
            clock);

        Assert.True((await worker.ProcessAsync(
            tenantId,
            dryRun.BatchId,
            CancellationToken.None)).IsSuccess);

        await using VistaraDbContext verification =
            database.CreateContext(tenantId);
        var reader = new LifecycleService(
            new RelationalLifecycleStore(verification, ids),
            clock,
            ids);
        LifecyclePurgeBatchSnapshot batch = LifecyclePersistenceTests.Required(
            await reader.GetPurgeBatchAsync(
                approver,
                dryRun.BatchId,
                CancellationToken.None));
        LifecyclePurgeItemSnapshot item = Assert.Single(batch.Items);
        Assert.Equal("completed", batch.State);
        Assert.Equal("blocked", item.Result);
        Assert.Equal("purge.active_hold", item.ErrorCode);
        Assert.Equal(0, storage.TotalDeleteCalls);
        Assert.Empty(await verification.DeletionTombstones.ToListAsync());
        Assert.Equal("Trashed", (await verification.Assets.SingleAsync()).Status);
    }

    [Fact]
    public async Task Purge_confirmation_rejects_changed_revision_and_relationships()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(120);
        Guid requesterId = LifecyclePersistenceTests.Id(121);
        Guid approverId = LifecyclePersistenceTests.Id(122);
        Guid firstAssetId = LifecyclePersistenceTests.Id(123);
        Guid secondAssetId = LifecyclePersistenceTests.Id(130);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset first =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                requesterId,
                firstAssetId,
                includeRelationships: false);
        LifecyclePersistenceTests.SeededAsset second =
            await AddAssetOnlyAsync(
                database.Context,
                tenantId,
                requesterId,
                secondAssetId);
        await SeedActorAsync(database.Context, tenantId, approverId);
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(2));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleActorContext requester = Human(tenantId, requesterId, clock.UtcNow);
        IReadOnlyList<LifecycleAssetMutationResult> trashed =
            LifecyclePersistenceTests.Required(
                await service.TrashAsync(
                    requester,
                    [
                        new LifecycleAssetTarget(firstAssetId, first.AssetVersion),
                        new LifecycleAssetTarget(secondAssetId, second.AssetVersion),
                    ],
                    "cleanup",
                    CancellationToken.None));
        clock.Advance(TimeSpan.FromDays(31));
        requester = Human(tenantId, requesterId, clock.UtcNow);
        LifecyclePurgeDryRunSnapshot dryRun =
            LifecyclePersistenceTests.Required(
                await service.CreatePurgeDryRunAsync(
                    requester,
                    trashed.Select(item =>
                        new LifecycleAssetTarget(item.AssetId, item.Version)).ToArray(),
                    "purge-change-check",
                    CancellationToken.None));

        database.Context.ChangeTracker.Clear();
        AssetRow firstAsset = await database.Context.Assets
            .SingleAsync(row => row.Id == firstAssetId);
        Guid newBlobId = LifecyclePersistenceTests.Offset(firstAssetId, 10);
        Guid newRevisionId = LifecyclePersistenceTests.Offset(firstAssetId, 11);
        database.Context.Blobs.Add(new BlobRow
        {
            Id = newBlobId,
            TenantId = tenantId,
            Provider = "test",
            Container = "media",
            ObjectKey = $"originals/{tenantId:N}/{firstAssetId:N}/2/image.jpg",
            ProviderVersion = "original-v2",
            Sha256 = new string('b', 64),
            SizeBytes = 8_192,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = clock.UtcNow,
        });
        await database.Context.SaveChangesAsync();
        database.Context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = newRevisionId,
            TenantId = tenantId,
            AssetId = firstAssetId,
            RevisionNumber = 2,
            BlobId = newBlobId,
            DetectedFormat = "jpeg",
            DetectedContentType = "image/jpeg",
            Width = 800,
            Height = 600,
            FrameCount = 1,
            SafeMetadataJson = "{}",
            PrivateMetadataJson = "{}",
            CreatedAtUtc = clock.UtcNow,
        });
        await database.Context.SaveChangesAsync();
        firstAsset.CurrentRevisionId = newRevisionId;
        firstAsset.Version++;
        await database.Context.SaveChangesAsync();
        Guid albumId = LifecyclePersistenceTests.Offset(secondAssetId, 10);
        database.Context.Albums.Add(new AlbumRow
        {
            Id = albumId,
            TenantId = tenantId,
            OwnerId = requesterId,
            Name = "Late reference",
            SortMode = "Manual",
            Version = 1,
        });
        database.Context.AlbumItems.Add(new AlbumItemRow
        {
            TenantId = tenantId,
            AlbumId = albumId,
            AssetId = secondAssetId,
            Position = 1,
            AddedByUserId = requesterId,
            AddedAtUtc = clock.UtcNow,
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        Result<LifecyclePurgeBatchSnapshot> confirmation =
            await service.ConfirmPurgeAsync(
                Human(tenantId, approverId, clock.UtcNow),
                dryRun.BatchId,
                dryRun.Version,
                dryRun.DryRunDigest,
                "purge-stale-confirm",
                CancellationToken.None);

        Assert.Equal("lifecycle.dry_run_stale", confirmation.Error?.Code);
        Assert.Empty(await database.Context.Jobs
            .Where(row => row.Type == LifecycleJobContracts.PurgeType.Value)
            .ToListAsync());
    }

    [Fact]
    public async Task Purge_worker_retries_partial_and_ambiguous_deletes_then_reconciles()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(150);
        Guid requesterId = LifecyclePersistenceTests.Id(151);
        Guid approverId = LifecyclePersistenceTests.Id(152);
        Guid assetId = LifecyclePersistenceTests.Id(153);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset seeded =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                requesterId,
                assetId,
                includeRelationships: false);
        await SeedActorAsync(database.Context, tenantId, approverId);
        await SeedReadyDerivativeAsync(
            database.Context,
            tenantId,
            assetId,
            seeded.RevisionId,
            "derivatives/a.webp",
            "derivative-a-v1",
            100,
            1);
        await SeedReadyDerivativeAsync(
            database.Context,
            tenantId,
            assetId,
            seeded.RevisionId,
            "derivatives/b.webp",
            "derivative-b-v1",
            200,
            2);
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(3));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleActorContext requester = Human(tenantId, requesterId, clock.UtcNow);
        LifecycleAssetMutationResult trashed = Assert.Single(
            LifecyclePersistenceTests.Required(
                await service.TrashAsync(
                    requester,
                    [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                    "cleanup",
                    CancellationToken.None)));
        clock.Advance(TimeSpan.FromDays(31));
        LifecyclePurgeDryRunSnapshot dryRun =
            LifecyclePersistenceTests.Required(
                await service.CreatePurgeDryRunAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, trashed.Version)],
                    "purge-retry",
                    CancellationToken.None));
        _ = LifecyclePersistenceTests.Required(
            await service.ConfirmPurgeAsync(
                Human(tenantId, approverId, clock.UtcNow),
                dryRun.BatchId,
                dryRun.Version,
                dryRun.DryRunDigest,
                "purge-retry-confirm",
                CancellationToken.None));

        var storage = new ScriptedBlobStore();
        storage.Add("derivatives/a.webp", "derivative-a-v1", 100);
        storage.Add("derivatives/b.webp", "derivative-b-v1", 200);
        await AddOriginalToStorageAsync(database.Context, storage, seeded.BlobId);
        storage.Script(
            "derivatives/a.webp",
            DeleteBehavior.Delete);
        storage.Script(
            "derivatives/b.webp",
            DeleteBehavior.OutcomeUnknownPresent,
            DeleteBehavior.OutcomeUnknownMissing);
        await using VistaraDbContext workerContext =
            database.CreateContext(tenantId);
        var worker = new LifecyclePurgeService(
            new RelationalLifecycleWorkerStore(workerContext, ids),
            storage,
            clock);

        Vistara.Worker.Runtime.Jobs.JobHandlerResult firstAttempt =
            await worker.ProcessAsync(
                tenantId,
                dryRun.BatchId,
                CancellationToken.None);
        Assert.False(firstAttempt.IsSuccess);
        Assert.Equal(1, storage.DeleteCalls("derivatives/a.webp"));
        Assert.Equal(1, storage.DeleteCalls("derivatives/b.webp"));
        Assert.Equal(0, storage.DeleteCalls(
            $"originals/{tenantId:N}/{assetId:N}/1/image.jpg"));

        Assert.True((await worker.ProcessAsync(
            tenantId,
            dryRun.BatchId,
            CancellationToken.None)).IsSuccess);
        Assert.True((await worker.ProcessAsync(
            tenantId,
            dryRun.BatchId,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(1, storage.DeleteCalls("derivatives/a.webp"));
        Assert.Equal(2, storage.DeleteCalls("derivatives/b.webp"));
        Assert.Equal(1, storage.DeleteCalls(
            $"originals/{tenantId:N}/{assetId:N}/1/image.jpg"));
        await using VistaraDbContext verification =
            database.CreateContext(tenantId);
        Assert.Empty(await verification.Assets.ToListAsync());
        Assert.Empty(await verification.AssetRevisions.ToListAsync());
        Assert.Empty(await verification.Blobs.ToListAsync());
        DeletionTombstoneRow tombstone =
            await verification.DeletionTombstones.SingleAsync();
        Assert.Equal(assetId, tombstone.FormerAssetId);
        Assert.Equal(0, tombstone.RelationshipCount);
        Assert.Equal(64, tombstone.RelationshipDigest.Length);
        PurgeBatchItemRow item = await verification.PurgeBatchItems.SingleAsync();
        Assert.Equal("Purged", item.Result);
        Assert.Equal(4_396, item.ReclaimedBytes);
        string audit = string.Join(
            '|',
            await verification.AuditEvents
                .Select(row => row.BeforeJson + row.AfterJson)
                .ToListAsync());
        Assert.DoesNotContain("derivatives/", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("originals/", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purge_worker_rechecks_share_references_after_head_before_delete()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(180);
        Guid requesterId = LifecyclePersistenceTests.Id(181);
        Guid approverId = LifecyclePersistenceTests.Id(182);
        Guid assetId = LifecyclePersistenceTests.Id(183);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset seeded =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                requesterId,
                assetId,
                includeRelationships: false);
        await SeedActorAsync(database.Context, tenantId, approverId);
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(5));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleAssetMutationResult trashed = Assert.Single(
            LifecyclePersistenceTests.Required(
                await service.TrashAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                    "cleanup",
                    CancellationToken.None)));
        clock.Advance(TimeSpan.FromDays(31));
        LifecyclePurgeDryRunSnapshot dryRun =
            LifecyclePersistenceTests.Required(
                await service.CreatePurgeDryRunAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, trashed.Version)],
                    "purge-reference-recheck",
                    CancellationToken.None));
        _ = LifecyclePersistenceTests.Required(
            await service.ConfirmPurgeAsync(
                Human(tenantId, approverId, clock.UtcNow),
                dryRun.BatchId,
                dryRun.Version,
                dryRun.DryRunDigest,
                "purge-reference-confirm",
                CancellationToken.None));

        var storage = new ScriptedBlobStore();
        await AddOriginalToStorageAsync(database.Context, storage, seeded.BlobId);
        int injected = 0;
        storage.BeforeHeadAsync = async key =>
        {
            if (!key.StartsWith("originals/", StringComparison.Ordinal) ||
                Interlocked.Exchange(ref injected, 1) != 0)
            {
                return;
            }

            await using VistaraDbContext mutation =
                database.CreateContext(tenantId);
            Guid shareId = LifecyclePersistenceTests.Id(190);
            mutation.Shares.Add(new ShareRow
            {
                Id = shareId,
                TenantId = tenantId,
                CreatedByUserId = requesterId,
                TokenHash = new string('9', 64),
                TargetKind = "Snapshot",
                SnapshotJson = "{}",
                Permissions = 1,
                CreatedAtUtc = clock.UtcNow,
                Version = 1,
            });
            mutation.ShareAssets.Add(new ShareAssetRow
            {
                TenantId = tenantId,
                ShareId = shareId,
                AssetId = assetId,
                RevisionId = seeded.RevisionId,
                RevisionNumber = 1,
            });
            await mutation.SaveChangesAsync();
        };
        await using VistaraDbContext workerContext =
            database.CreateContext(tenantId);
        var worker = new LifecyclePurgeService(
            new RelationalLifecycleWorkerStore(workerContext, ids),
            storage,
            clock);

        Assert.True((await worker.ProcessAsync(
            tenantId,
            dryRun.BatchId,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(0, storage.TotalDeleteCalls);
        await using VistaraDbContext verification =
            database.CreateContext(tenantId);
        Assert.Equal("Trashed", (await verification.Assets.SingleAsync()).Status);
        Assert.Empty(await verification.DeletionTombstones.ToListAsync());
        Assert.Equal(
            "Blocked",
            (await verification.PurgeBatchItems.SingleAsync()).Result);
    }

    [Fact]
    public async Task Purge_worker_rechecks_approver_permission_after_head_before_delete()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(210);
        Guid requesterId = LifecyclePersistenceTests.Id(211);
        Guid approverId = LifecyclePersistenceTests.Id(212);
        Guid assetId = LifecyclePersistenceTests.Id(213);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset seeded =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                requesterId,
                assetId,
                includeRelationships: false);
        await SeedActorAsync(database.Context, tenantId, approverId);
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(6));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleAssetMutationResult trashed = Assert.Single(
            LifecyclePersistenceTests.Required(
                await service.TrashAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                    "cleanup",
                    CancellationToken.None)));
        clock.Advance(TimeSpan.FromDays(31));
        LifecyclePurgeDryRunSnapshot dryRun =
            LifecyclePersistenceTests.Required(
                await service.CreatePurgeDryRunAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, trashed.Version)],
                    "purge-permission-recheck",
                    CancellationToken.None));
        _ = LifecyclePersistenceTests.Required(
            await service.ConfirmPurgeAsync(
                Human(tenantId, approverId, clock.UtcNow),
                dryRun.BatchId,
                dryRun.Version,
                dryRun.DryRunDigest,
                "purge-permission-confirm",
                CancellationToken.None));

        var storage = new ScriptedBlobStore();
        await AddOriginalToStorageAsync(database.Context, storage, seeded.BlobId);
        int injected = 0;
        storage.BeforeHeadAsync = async key =>
        {
            if (!key.StartsWith("originals/", StringComparison.Ordinal) ||
                Interlocked.Exchange(ref injected, 1) != 0)
            {
                return;
            }

            await using VistaraDbContext mutation =
                database.CreateContext(tenantId);
            TenantMembershipRow membership = await mutation.TenantMemberships
                .SingleAsync(row => row.UserId == approverId);
            membership.Status = "Suspended";
            membership.Version++;
            membership.UpdatedAtUtc = clock.UtcNow;
            await mutation.SaveChangesAsync();
        };
        await using VistaraDbContext workerContext =
            database.CreateContext(tenantId);
        var worker = new LifecyclePurgeService(
            new RelationalLifecycleWorkerStore(workerContext, ids),
            storage,
            clock);

        Assert.True((await worker.ProcessAsync(
            tenantId,
            dryRun.BatchId,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(0, storage.TotalDeleteCalls);
        await using VistaraDbContext verification =
            database.CreateContext(tenantId);
        Assert.Equal("Trashed", (await verification.Assets.SingleAsync()).Status);
        Assert.Empty(await verification.DeletionTombstones.ToListAsync());
    }

    [Fact]
    public async Task Executing_batch_distinguishes_terminal_failure_from_pending_work()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(230);
        Guid requesterId = LifecyclePersistenceTests.Id(231);
        Guid approverId = LifecyclePersistenceTests.Id(232);
        Guid assetId = LifecyclePersistenceTests.Id(233);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset seeded =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                requesterId,
                assetId,
                includeRelationships: false);
        await SeedActorAsync(database.Context, tenantId, approverId);
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(7));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleAssetMutationResult trashed = Assert.Single(
            LifecyclePersistenceTests.Required(
                await service.TrashAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                    "cleanup",
                    CancellationToken.None)));
        clock.Advance(TimeSpan.FromDays(31));
        LifecyclePurgeDryRunSnapshot dryRun =
            LifecyclePersistenceTests.Required(
                await service.CreatePurgeDryRunAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, trashed.Version)],
                    "purge-terminal-failure",
                    CancellationToken.None));
        _ = LifecyclePersistenceTests.Required(
            await service.ConfirmPurgeAsync(
                Human(tenantId, approverId, clock.UtcNow),
                dryRun.BatchId,
                dryRun.Version,
                dryRun.DryRunDigest,
                "purge-terminal-failure-confirm",
                CancellationToken.None));

        await using VistaraDbContext workerContext =
            database.CreateContext(tenantId);
        var workerStore = new RelationalLifecycleWorkerStore(workerContext, ids);
        LifecyclePurgeBatchWork first = await workerStore.StartPurgeBatchAsync(
            tenantId,
            dryRun.BatchId,
            clock.UtcNow,
            CancellationToken.None);
        Assert.Equal([assetId], first.AssetIds);
        Assert.True((await workerStore.RecordPurgeItemResultAsync(
            tenantId,
            dryRun.BatchId,
            assetId,
            LifecyclePurgeItemOutcome.Failed,
            "purge.provider_contract_failure",
            clock.UtcNow,
            CancellationToken.None)).IsSuccess);

        LifecyclePurgeBatchWork retried = await workerStore.StartPurgeBatchAsync(
            tenantId,
            dryRun.BatchId,
            clock.UtcNow,
            CancellationToken.None);
        LifecyclePurgeBatchSnapshot snapshot =
            LifecyclePersistenceTests.Required(
                await service.GetPurgeBatchAsync(
                    Human(tenantId, approverId, clock.UtcNow),
                    dryRun.BatchId,
                    CancellationToken.None));

        Assert.Empty(retried.AssetIds);
        LifecyclePurgeItemSnapshot item = Assert.Single(snapshot.Items);
        Assert.Equal("failed", item.Result);
        Assert.Equal("purge.provider_contract_failure", item.ErrorCode);
        Assert.Equal(1, snapshot.ProcessedCount);
        Assert.Equal("executing", snapshot.State);
    }

    [Fact]
    public async Task Hold_racing_after_provider_delete_records_the_physical_outcome()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(250);
        Guid requesterId = LifecyclePersistenceTests.Id(251);
        Guid approverId = LifecyclePersistenceTests.Id(252);
        Guid assetId = LifecyclePersistenceTests.Id(253);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset seeded =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                requesterId,
                assetId,
                includeRelationships: false);
        await SeedActorAsync(database.Context, tenantId, approverId);
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(8));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleAssetMutationResult trashed = Assert.Single(
            LifecyclePersistenceTests.Required(
                await service.TrashAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                    "cleanup",
                    CancellationToken.None)));
        clock.Advance(TimeSpan.FromDays(31));
        LifecyclePurgeDryRunSnapshot dryRun =
            LifecyclePersistenceTests.Required(
                await service.CreatePurgeDryRunAsync(
                    Human(tenantId, requesterId, clock.UtcNow),
                    [new LifecycleAssetTarget(assetId, trashed.Version)],
                    "purge-hold-race",
                    CancellationToken.None));
        _ = LifecyclePersistenceTests.Required(
            await service.ConfirmPurgeAsync(
                Human(tenantId, approverId, clock.UtcNow),
                dryRun.BatchId,
                dryRun.Version,
                dryRun.DryRunDigest,
                "purge-hold-race-confirm",
                CancellationToken.None));

        var storage = new ScriptedBlobStore();
        await AddOriginalToStorageAsync(database.Context, storage, seeded.BlobId);
        storage.AfterDeleteAsync = async key =>
        {
            if (!key.StartsWith("originals/", StringComparison.Ordinal))
            {
                return;
            }

            await using VistaraDbContext mutation =
                database.CreateContext(tenantId);
            mutation.RetentionHolds.Add(new RetentionHoldRow
            {
                Id = LifecyclePersistenceTests.Id(260),
                TenantId = tenantId,
                AssetId = assetId,
                Reason = "late legal hold",
                CreatedByUserId = approverId,
                CreatedAtUtc = clock.UtcNow,
                Version = 1,
            });
            AssetLifecycleRow lifecycle =
                await mutation.AssetLifecycles.SingleAsync();
            lifecycle.Version++;
            await mutation.SaveChangesAsync();
        };
        await using VistaraDbContext workerContext =
            database.CreateContext(tenantId);
        var worker = new LifecyclePurgeService(
            new RelationalLifecycleWorkerStore(workerContext, ids),
            storage,
            clock);

        Assert.True((await worker.ProcessAsync(
            tenantId,
            dryRun.BatchId,
            CancellationToken.None)).IsSuccess);

        await using VistaraDbContext verification =
            database.CreateContext(tenantId);
        Assert.Equal("Deleted", (await verification.Blobs.SingleAsync()).State);
        Assert.Equal("Trashed", (await verification.Assets.SingleAsync()).Status);
        Assert.Empty(await verification.DeletionTombstones.ToListAsync());
        Assert.Equal(
            "Blocked",
            (await verification.PurgeBatchItems.SingleAsync()).Result);
    }

    private static LifecycleActorContext Human(
        Guid tenantId,
        Guid actorId,
        DateTimeOffset authenticatedAtUtc) =>
        LifecycleActorContext.Human(
            tenantId,
            actorId,
            LifecycleRights.All,
            authenticatedAtUtc);

    private static async ValueTask SeedActorAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid actorId)
    {
        context.Users.Add(new UserRow
        {
            Id = actorId,
            NormalizedEmail = $"{actorId:N}@example.test",
            DisplayName = "Purge approver",
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = tenantId,
            UserId = actorId,
            Role = "TenantOwner",
            Status = "Active",
            InvitedAtUtc = Now,
            JoinedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async ValueTask<LifecyclePersistenceTests.SeededAsset>
        AddAssetOnlyAsync(
            VistaraDbContext context,
            Guid tenantId,
            Guid actorId,
            Guid assetId)
    {
        Guid blobId = LifecyclePersistenceTests.Offset(assetId, 1);
        Guid revisionId = LifecyclePersistenceTests.Offset(assetId, 2);
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = tenantId,
            Provider = "test",
            Container = "media",
            ObjectKey = $"originals/{tenantId:N}/{assetId:N}/1/image.jpg",
            ProviderVersion = "original-v1",
            Sha256 = new string('c', 64),
            SizeBytes = 2_048,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = Now,
        });
        var asset = new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = actorId,
            Title = "Second asset",
            Status = "Ready",
            Visibility = "Private",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 4,
        };
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = revisionId,
            TenantId = tenantId,
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = blobId,
            DetectedFormat = "jpeg",
            DetectedContentType = "image/jpeg",
            Width = 320,
            Height = 240,
            FrameCount = 1,
            SafeMetadataJson = "{}",
            PrivateMetadataJson = "{}",
            CreatedAtUtc = Now,
        });
        await context.SaveChangesAsync();
        asset.CurrentRevisionId = revisionId;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return new(assetId, revisionId, blobId, 4);
    }

    private static async ValueTask SeedReadyDerivativeAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid assetId,
        Guid revisionId,
        string key,
        string version,
        long bytes,
        int suffix)
    {
        Guid jobId = LifecyclePersistenceTests.Id(160 + suffix);
        context.Jobs.Add(new JobRow
        {
            Id = jobId,
            TenantId = tenantId,
            Type = "derivative.generate",
            Payload = "{}",
            PayloadVersion = 1,
            DedupeKey = $"seed-derivative-{suffix}",
            Priority = 0,
            MaxAttempts = 5,
            State = "Pending",
            AvailableAtUtc = Now,
            CreatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
        Guid requestId = LifecyclePersistenceTests.Id(170 + suffix);
        string generation = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                requestId.ToByteArray())).ToLowerInvariant();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO derivative_requests (
                id, tenant_id, asset_id, revision_id, job_id,
                idempotency_key, request_hash, preset_name, preset_revision,
                width, height, fit, format, quality,
                focal_point_x, focal_point_y, crop_x, crop_y, crop_width, crop_height,
                pipeline_id, pipeline_fingerprint, source_sha256, recipe_sha256,
                generation_identity, cache_key, extension, is_public, state,
                failure_code, representation_storage_key,
                representation_content_length, representation_content_type,
                representation_sha256, created_at_utc, updated_at_utc, version
            ) VALUES (
                {requestId}, {tenantId}, {assetId}, {revisionId}, {jobId},
                {$"seed-{suffix}"}, {generation}, {"pipeline"}, {1},
                {100}, {100}, {"cover"}, {"webp"}, {80},
                {null}, {null}, {null}, {null}, {null}, {null},
                {"pipeline-v1"}, {"pipeline-fingerprint"}, {new string('a', 64)},
                {new string('d', 64)}, {generation}, {version}, {"webp"}, {false},
                {"Ready"}, {null}, {key}, {bytes}, {"image/webp"},
                {new string('e', 64)}, {Now}, {Now}, {1}
            );
            """);
        context.ChangeTracker.Clear();
    }

    private static async ValueTask AddOriginalToStorageAsync(
        VistaraDbContext context,
        ScriptedBlobStore storage,
        Guid blobId)
    {
        context.ChangeTracker.Clear();
        BlobRow blob = await context.Blobs
            .AsNoTracking()
            .SingleAsync(row => row.Id == blobId);
        storage.Add(blob.ObjectKey, blob.ProviderVersion!, blob.SizeBytes);
    }

    private enum DeleteBehavior
    {
        Delete,
        OutcomeUnknownPresent,
        OutcomeUnknownMissing,
    }

    private sealed class ScriptedBlobStore : IBlobStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, StoredBlob> _objects =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<DeleteBehavior>> _scripts =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _deleteCalls =
            new(StringComparer.Ordinal);

        public Func<string, ValueTask>? BeforeHeadAsync { get; set; }

        public Func<string, ValueTask>? AfterDeleteAsync { get; set; }

        public string Name => "test";

        public BlobStoreCapabilities Capabilities { get; } = new()
        {
            SupportsConditionalDelete = true,
            ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
        };

        public int TotalDeleteCalls
        {
            get
            {
                lock (_gate)
                {
                    return _deleteCalls.Values.Sum();
                }
            }
        }

        public void Add(string key, string version, long bytes)
        {
            lock (_gate)
            {
                _objects.Add(key, new StoredBlob(version, bytes));
            }
        }

        public void Script(string key, params DeleteBehavior[] behaviors)
        {
            lock (_gate)
            {
                _scripts[key] = new Queue<DeleteBehavior>(behaviors);
            }
        }

        public int DeleteCalls(string key)
        {
            lock (_gate)
            {
                return _deleteCalls.GetValueOrDefault(key);
            }
        }

        public async ValueTask<BlobHead?> HeadAsync(
            BlobKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeHeadAsync is not null)
            {
                await BeforeHeadAsync(key.Value);
            }

            lock (_gate)
            {
                return _objects.TryGetValue(key.Value, out StoredBlob? value)
                    ? Head(key, value)
                    : null;
            }
        }

        public async ValueTask<BlobDeleteResult> DeleteAsync(
            BlobKey key,
            BlobDeleteOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlobDeleteResult result;
            lock (_gate)
            {
                _deleteCalls[key.Value] = _deleteCalls.GetValueOrDefault(key.Value) + 1;
                if (!_objects.TryGetValue(key.Value, out StoredBlob? current))
                {
                    return new BlobDeleteResult(false, null);
                }

                BlobVersion? expected = options.EffectiveConditions.IfMatch;
                if (expected is not null &&
                    !string.Equals(
                        expected.Value,
                        current.Version,
                        StringComparison.Ordinal))
                {
                    throw new BlobStoreException(
                        BlobStoreErrorCode.PreconditionFailed,
                        "The test delete version changed.");
                }

                DeleteBehavior behavior =
                    _scripts.TryGetValue(key.Value, out Queue<DeleteBehavior>? script) &&
                    script.Count > 0
                        ? script.Dequeue()
                        : DeleteBehavior.Delete;
                switch (behavior)
                {
                    case DeleteBehavior.Delete:
                        _objects.Remove(key.Value);
                        result = new BlobDeleteResult(
                            true,
                            new BlobIdentity(key, new BlobVersion(current.Version)));
                        break;
                    case DeleteBehavior.OutcomeUnknownPresent:
                        throw new BlobStoreException(
                            BlobStoreErrorCode.OutcomeUnknown,
                            "The test provider outcome is ambiguous.");
                    case DeleteBehavior.OutcomeUnknownMissing:
                        _objects.Remove(key.Value);
                        throw new BlobStoreException(
                            BlobStoreErrorCode.OutcomeUnknown,
                            "The test provider deleted the object before losing the response.");
                    default:
                        throw new InvalidOperationException(
                            "The scripted delete behavior is invalid.");
                }
            }

            if (AfterDeleteAsync is not null)
            {
                await AfterDeleteAsync(key.Value);
            }

            return result;
        }

        public ValueTask<BlobReadHandle> OpenReadAsync(
            BlobKey key,
            BlobReadOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobWriteResult> PutAsync(
            BlobKey key,
            IReplayableBlobContent content,
            BlobWriteOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobCopyResult> CopyAsync(
            BlobKey source,
            BlobKey destination,
            BlobCopyOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<BlobHead> ListAsync(
            BlobListOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

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

        private static BlobHead Head(BlobKey key, StoredBlob value)
        {
            var version = new BlobVersion(value.Version);
            return new BlobHead(
                new BlobIdentity(key, version),
                new BlobProperties(
                    value.Bytes,
                    new BlobMediaType("application/octet-stream"),
                    Now,
                    version,
                    new BlobEntityTag($"\"{value.Version}\""),
                    [],
                    BlobMetadata.Empty));
        }

        private sealed record StoredBlob(string Version, long Bytes);
    }
}
