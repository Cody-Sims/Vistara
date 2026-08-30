using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.IntegrationTests.Persistence;
using Vistara.Persistence;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Lifecycle;
using Vistara.Persistence.Model;
using Vistara.Worker.Features.Lifecycle;
using Xunit;

namespace Vistara.IntegrationTests.Lifecycle;

public sealed class LifecyclePersistenceTests
{
    private static readonly DateTimeOffset Now =
        new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task Trash_and_restore_worker_preserve_stable_asset_data_and_relationships()
    {
        Guid tenantId = Id(1);
        Guid actorId = Id(2);
        Guid assetId = Id(3);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        SeededAsset seeded = await SeedAssetAsync(
            database.Context,
            tenantId,
            actorId,
            assetId,
            includeRelationships: true);
        var clock = new MutableClock(Now);
        var ids = new SequenceUuid7Generator(Now.AddSeconds(1));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleActorContext actor = LifecycleActorContext.Human(
            tenantId,
            actorId,
            LifecycleRights.All,
            Now);

        Result<IReadOnlyList<LifecycleAssetMutationResult>> trashed =
            await service.TrashAsync(
                actor,
                [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                "cleanup contains private context",
                CancellationToken.None);
        LifecycleAssetMutationResult trashedItem =
            Assert.Single(Required(trashed));
        Result<IReadOnlyList<LifecycleAssetMutationResult>> replayedTrash =
            await service.TrashAsync(
                actor,
                [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                "cleanup contains private context",
                CancellationToken.None);

        Assert.Equal("trashed", trashedItem.Status);
        Assert.Equal("alreadyTrashed", Assert.Single(Required(replayedTrash)).Status);
        database.Context.ChangeTracker.Clear();
        AssetRow hidden = await database.Context.Assets.SingleAsync();
        Assert.Equal("Trashed", hidden.Status);
        var media = new RelationalDerivativeRequestStore(
            database.Context,
            new FixedTenantScope(tenantId));
        Assert.Null(await media.GetSourceAsync(
            tenantId,
            assetId,
            CancellationToken.None));

        Result<LifecycleJobSubmission> submitted =
            await service.SubmitRestoreAsync(
                actor,
                [new LifecycleAssetTarget(assetId, trashedItem.Version)],
                "restore-one",
                CancellationToken.None);
        Result<LifecycleJobSubmission> replayedSubmission =
            await service.SubmitRestoreAsync(
                actor,
                [new LifecycleAssetTarget(assetId, trashedItem.Version)],
                "restore-one",
                CancellationToken.None);
        LifecycleJobSubmission submission = Required(submitted);
        Assert.Equal(submission.JobId, Required(replayedSubmission).JobId);
        Assert.True(Required(replayedSubmission).Replayed);
        database.Context.ChangeTracker.Clear();
        JobRow job = await database.Context.Jobs.SingleAsync();
        Assert.True(LifecycleJobContracts.TryParseRestore(
            new Vistara.Domain.Jobs.JobType(job.Type),
            job.PayloadVersion,
            job.Payload,
            out LifecycleRestoreJobPayload? payload));

        await using VistaraDbContext workerContext =
            database.CreateContext(tenantId);
        var restore = new LifecycleRestoreService(
            new RelationalLifecycleWorkerStore(workerContext, ids),
            clock);
        Assert.True((await restore.ProcessAsync(
            payload!,
            CancellationToken.None)).IsSuccess);
        Assert.True((await restore.ProcessAsync(
            payload!,
            CancellationToken.None)).IsSuccess);

        await using VistaraDbContext verification =
            database.CreateContext(tenantId);
        AssetRow restored = await verification.Assets.SingleAsync();
        AssetRevisionRow revision = await verification.AssetRevisions.SingleAsync();
        Assert.Equal(assetId, restored.Id);
        Assert.Equal(seeded.RevisionId, restored.CurrentRevisionId);
        Assert.Equal(seeded.RevisionId, revision.Id);
        Assert.Equal("Ready", restored.Status);
        Assert.Equal("{\"camera\":\"safe\"}", revision.SafeMetadataJson);
        Assert.Equal("{\"gps\":\"private\"}", revision.PrivateMetadataJson);
        Assert.Single(await verification.AlbumItems.ToListAsync());
        Assert.Single(await verification.AssetTags.ToListAsync());
        Assert.Single(await verification.AssetFavorites.ToListAsync());
        Assert.Single(await verification.ShareAssets.ToListAsync());
        Assert.Single(await verification.ResourceGrants.ToListAsync());
        Assert.Empty(await verification.TrashEntries.ToListAsync());
        Assert.Equal("Ready", (await verification.AssetLifecycles.SingleAsync()).State);
        Assert.Equal(2, await verification.AuditEvents.CountAsync());
        string audit = string.Join(
            '|',
            await verification.AuditEvents
                .OrderBy(row => row.OccurredAtUtc)
                .Select(row => row.BeforeJson + row.AfterJson)
                .ToListAsync());
        Assert.Contains("[REDACTED]", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-audit", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gps", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("originals/", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("cleanup contains", audit, StringComparison.Ordinal);

        var restoredMedia = new RelationalDerivativeRequestStore(
            verification,
            new FixedTenantScope(tenantId));
        Assert.NotNull(await restoredMedia.GetSourceAsync(
            tenantId,
            assetId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Trash_cannot_cross_the_active_tenant_scope()
    {
        Guid firstTenant = Id(20);
        Guid firstActor = Id(21);
        Guid assetId = Id(22);
        Guid secondTenant = Id(23);
        Guid secondActor = Id(24);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(firstTenant);
        SeededAsset seeded = await SeedAssetAsync(
            database.Context,
            firstTenant,
            firstActor,
            assetId,
            includeRelationships: false);
        await using VistaraDbContext secondContext =
            database.CreateContext(secondTenant);
        await SeedPrincipalAsync(secondContext, secondTenant, secondActor);
        var ids = new SequenceUuid7Generator(Now.AddSeconds(2));
        var service = new LifecycleService(
            new RelationalLifecycleStore(secondContext, ids),
            new MutableClock(Now),
            ids);
        LifecycleActorContext actor = LifecycleActorContext.Human(
            secondTenant,
            secondActor,
            LifecycleRights.All,
            Now);

        LifecycleAssetMutationResult result = Assert.Single(Required(
            await service.TrashAsync(
                actor,
                [new LifecycleAssetTarget(assetId, seeded.AssetVersion)],
                "cross tenant",
                CancellationToken.None)));

        Assert.Equal("notFound", result.Status);
        await using VistaraDbContext verification =
            database.CreateContext(firstTenant);
        Assert.Equal("Ready", (await verification.Assets.SingleAsync()).Status);
        Assert.Empty(await verification.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Trash_list_returns_only_trashed_assets_with_retention_details()
    {
        Guid tenantId = Id(40);
        Guid actorId = Id(41);
        Guid trashedAssetId = Id(42);
        Guid readyAssetId = Id(50);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        SeededAsset trashedSeed = await SeedAssetAsync(
            database.Context,
            tenantId,
            actorId,
            trashedAssetId,
            includeRelationships: false);
        _ = await AddAssetForExistingPrincipalAsync(
            database.Context,
            tenantId,
            actorId,
            readyAssetId);
        var ids = new SequenceUuid7Generator(Now.AddSeconds(4));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            new MutableClock(Now),
            ids);
        LifecycleActorContext actor = LifecycleActorContext.Human(
            tenantId,
            actorId,
            LifecycleRights.All,
            Now);
        _ = Required(await service.TrashAsync(
            actor,
            [new LifecycleAssetTarget(trashedAssetId, trashedSeed.AssetVersion)],
            "cleanup",
            CancellationToken.None));

        LifecycleTrashPage page = Required(await service.ListTrashAsync(
            actor,
            new LifecycleTrashListRequest(50, null, null, descending: true),
            CancellationToken.None));

        LifecycleTrashItemSnapshot item = Assert.Single(page.Items);
        Assert.Equal(trashedAssetId, item.AssetId);
        Assert.Equal(Now.AddDays(30), item.PurgeAtUtc);
        Assert.Equal(0, item.ActiveHoldCount);
        Assert.Equal(4_096, item.EstimatedReclaimBytes);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Trash_list_keyset_pages_preserve_deleted_at_order()
    {
        Guid tenantId = Id(60);
        Guid actorId = Id(61);
        Guid firstAssetId = Id(62);
        Guid secondAssetId = Id(70);
        Guid thirdAssetId = Id(80);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        SeededAsset first = await SeedAssetAsync(
            database.Context,
            tenantId,
            actorId,
            firstAssetId,
            includeRelationships: false);
        SeededAsset second = await AddAssetForExistingPrincipalAsync(
            database.Context,
            tenantId,
            actorId,
            secondAssetId);
        SeededAsset third = await AddAssetForExistingPrincipalAsync(
            database.Context,
            tenantId,
            actorId,
            thirdAssetId);
        var clock = new MutableClock(Now);
        var ids = new SequenceUuid7Generator(Now.AddSeconds(5));
        var service = new LifecycleService(
            new RelationalLifecycleStore(database.Context, ids),
            clock,
            ids);
        LifecycleActorContext actor = LifecycleActorContext.Human(
            tenantId,
            actorId,
            LifecycleRights.All,
            Now);
        _ = Required(await service.TrashAsync(
            actor,
            [new LifecycleAssetTarget(firstAssetId, first.AssetVersion)],
            "first",
            CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = Required(await service.TrashAsync(
            actor,
            [new LifecycleAssetTarget(secondAssetId, second.AssetVersion)],
            "second",
            CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = Required(await service.TrashAsync(
            actor,
            [new LifecycleAssetTarget(thirdAssetId, third.AssetVersion)],
            "third",
            CancellationToken.None));

        LifecycleTrashPage firstPage = Required(await service.ListTrashAsync(
            actor,
            new LifecycleTrashListRequest(2, null, null, descending: true),
            CancellationToken.None));
        LifecycleTrashItemSnapshot cursor = firstPage.Items[^1];
        LifecycleTrashPage secondPage = Required(await service.ListTrashAsync(
            actor,
            new LifecycleTrashListRequest(
                2,
                cursor.DeletedAtUtc,
                cursor.AssetId,
                descending: true),
            CancellationToken.None));

        Assert.Equal(
            [thirdAssetId, secondAssetId],
            firstPage.Items.Select(item => item.AssetId));
        Assert.True(firstPage.HasMore);
        Assert.Equal(firstAssetId, Assert.Single(secondPage.Items).AssetId);
        Assert.False(secondPage.HasMore);
    }

    internal static async ValueTask<SeededAsset> SeedAssetAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid actorId,
        Guid assetId,
        bool includeRelationships)
    {
        await SeedPrincipalAsync(context, tenantId, actorId);
        Guid blobId = Offset(assetId, 1);
        Guid revisionId = Offset(assetId, 2);
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = tenantId,
            Provider = "test",
            Container = "media",
            ObjectKey = $"originals/{tenantId:N}/{assetId:N}/1/image.jpg",
            ProviderVersion = "original-v1",
            Sha256 = new string('a', 64),
            SizeBytes = 4_096,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = Now,
        });
        var asset = new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = actorId,
            CurrentRevisionId = null,
            Title = "Stable title",
            Description = "Stable description",
            Status = "Ready",
            Visibility = "Private",
            CapturedAtUtc = Now.AddDays(-5),
            CreatedAtUtc = Now.AddDays(-4),
            UpdatedAtUtc = Now.AddDays(-3),
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
            Width = 640,
            Height = 480,
            FrameCount = 1,
            SafeMetadataJson = "{\"camera\":\"safe\"}",
            PrivateMetadataJson = "{\"gps\":\"private\"}",
            CreatedAtUtc = Now,
        });
        await context.SaveChangesAsync();
        asset.CurrentRevisionId = revisionId;
        await context.SaveChangesAsync();
        context.AssetMetadataHistory.Add(new AssetMetadataHistoryRow
        {
            Id = Offset(assetId, 3),
            TenantId = tenantId,
            AssetId = assetId,
            ActorUserId = actorId,
            Source = "user",
            ChangesJson = "{\"privateMetadata\":\"do-not-audit\"}",
            ChangedAtUtc = Now,
        });
        if (includeRelationships)
        {
            Guid albumId = Offset(assetId, 4);
            Guid tagId = Offset(assetId, 5);
            Guid shareId = Offset(assetId, 6);
            context.Albums.Add(new AlbumRow
            {
                Id = albumId,
                TenantId = tenantId,
                OwnerId = actorId,
                Name = "Album",
                SortMode = "Manual",
                Version = 1,
            });
            context.AlbumItems.Add(new AlbumItemRow
            {
                TenantId = tenantId,
                AlbumId = albumId,
                AssetId = assetId,
                Position = 1,
                AddedByUserId = actorId,
                AddedAtUtc = Now,
            });
            context.Tags.Add(new TagRow
            {
                Id = tagId,
                TenantId = tenantId,
                NormalizedName = "tag",
                DisplayName = "Tag",
                Version = 1,
            });
            context.AssetTags.Add(new AssetTagRow
            {
                TenantId = tenantId,
                AssetId = assetId,
                TagId = tagId,
                Source = "user",
            });
            context.AssetFavorites.Add(new AssetFavoriteRow
            {
                TenantId = tenantId,
                UserId = actorId,
                AssetId = assetId,
                AddedAtUtc = Now,
            });
            context.Shares.Add(new ShareRow
            {
                Id = shareId,
                TenantId = tenantId,
                CreatedByUserId = actorId,
                TokenHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        shareId.ToByteArray())).ToLowerInvariant(),
                TargetKind = "Snapshot",
                SnapshotJson = "{}",
                Permissions = 1,
                CreatedAtUtc = Now,
                RevokedAtUtc = Now,
                RevokedByUserId = actorId,
                Version = 2,
            });
            context.ShareAssets.Add(new ShareAssetRow
            {
                TenantId = tenantId,
                ShareId = shareId,
                AssetId = assetId,
                RevisionId = revisionId,
                RevisionNumber = 1,
            });
            context.ResourceGrants.Add(new ResourceGrantRow
            {
                Id = Offset(assetId, 7),
                TenantId = tenantId,
                ResourceKind = "Asset",
                ResourceId = assetId,
                GranteeKind = "User",
                GranteeId = actorId,
                Role = "Viewer",
                CreatedByUserId = actorId,
                CreatedAtUtc = Now,
                RevokedAtUtc = Now,
                RevokedByUserId = actorId,
                Version = 2,
            });
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return new SeededAsset(assetId, revisionId, blobId, 4);
    }

    internal static async ValueTask SeedPrincipalAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid actorId,
        string role = "TenantOwner")
    {
        context.Users.Add(new UserRow
        {
            Id = actorId,
            NormalizedEmail = $"{actorId:N}@example.test",
            DisplayName = "Lifecycle actor",
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = $"tenant-{tenantId:N}",
            Name = "Lifecycle tenant",
            Status = "Active",
            SettingsJson = "{}",
            QuotasJson = "{}",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = tenantId,
            UserId = actorId,
            Role = role,
            Status = "Active",
            InvitedAtUtc = Now,
            JoinedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async ValueTask<SeededAsset> AddAssetForExistingPrincipalAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid actorId,
        Guid assetId)
    {
        Guid blobId = Offset(assetId, 1);
        Guid revisionId = Offset(assetId, 2);
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = tenantId,
            Provider = "test",
            Container = "media",
            ObjectKey = $"originals/{tenantId:N}/{assetId:N}/1/image.jpg",
            ProviderVersion = "original-v1",
            Sha256 = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    assetId.ToByteArray())),
            SizeBytes = 1_024,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = Now,
        });
        var asset = new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = actorId,
            Title = "Ready asset",
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
            Width = 100,
            Height = 100,
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

    internal static T Required<T>(Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Message);
        return value;
    }

    internal static Guid Id(int suffix) =>
        Guid.Parse($"019ac000-0000-7000-8000-{suffix:D12}");

    internal static Guid Offset(Guid value, int offset)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        bytes[^1] = checked((byte)(bytes[^1] + offset));
        return new Guid(bytes);
    }

    internal sealed record SeededAsset(
        Guid AssetId,
        Guid RevisionId,
        Guid BlobId,
        long AssetVersion);

    internal sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    internal sealed class SequenceUuid7Generator(DateTimeOffset start) : IUuid7Generator
    {
        private long _offset;

        public Guid NewId() => Guid.CreateVersion7(start.AddMilliseconds(_offset++));
    }
}
