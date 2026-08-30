using Microsoft.EntityFrameworkCore;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Albums;
using Vistara.Application.Gallery.Favorites;
using Vistara.Application.Gallery.Tags;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Curation;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Persistence;

public sealed class GalleryCurationPersistenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Album_create_is_idempotent_and_rejects_key_reuse_with_other_content()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId);
        var application = new AlbumApplication(
            new RelationalGalleryCurationStore(database.Context));
        var actor = new CurationActor(tenantId, ownerId, canManageAll: false);
        Guid firstId = Guid.CreateVersion7();

        CurationResult<AlbumSnapshot> created = await application.CreateAsync(
            actor,
            firstId,
            " Road   Trip ",
            null,
            "album-create",
            Now,
            CancellationToken.None);
        CurationResult<AlbumSnapshot> replay = await application.CreateAsync(
            actor,
            Guid.CreateVersion7(),
            "Road Trip",
            null,
            "album-create",
            Now.AddMinutes(1),
            CancellationToken.None);
        CurationResult<AlbumSnapshot> conflict = await application.CreateAsync(
            actor,
            Guid.CreateVersion7(),
            "Different",
            null,
            "album-create",
            Now.AddMinutes(2),
            CancellationToken.None);

        Assert.True(created.IsSuccess);
        Assert.Equal(firstId, replay.Value!.Id);
        Assert.Equal("Road Trip", replay.Value.Name);
        Assert.Equal(CurationFailureKind.IdempotencyConflict, conflict.Error!.Kind);
        Assert.Single(await database.Context.Albums.ToListAsync());
    }

    [Fact]
    public async Task Album_rename_preserves_omitted_fields_and_rejects_stale_versions()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId);
        var application = new AlbumApplication(
            new RelationalGalleryCurationStore(database.Context));
        var actor = new CurationActor(tenantId, ownerId, canManageAll: false);
        Assert.True((await application.CreateAsync(
            actor,
            albumId,
            "Original",
            "Keep this",
            "create",
            Now,
            CancellationToken.None)).IsSuccess);

        CurationResult<AlbumSnapshot> renamed = await application.UpdateAsync(
            actor,
            albumId,
            1,
            new AlbumUpdate(
                OptionalField.Specified(" Renamed "),
                OptionalField.Unspecified<string>(),
                OptionalField.Unspecified<Guid?>()),
            "rename",
            Now.AddMinutes(1),
            CancellationToken.None);
        CurationResult<AlbumSnapshot> stale = await application.UpdateAsync(
            actor,
            albumId,
            1,
            new AlbumUpdate(
                OptionalField.Specified("Stale"),
                OptionalField.Unspecified<string>(),
                OptionalField.Unspecified<Guid?>()),
            "stale",
            Now.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal("Renamed", renamed.Value!.Name);
        Assert.Equal("Keep this", renamed.Value.Description);
        Assert.Equal(2, renamed.Value.Version);
        Assert.Equal("album_version_conflict", stale.Error!.Code);
    }

    [Fact]
    public async Task Album_membership_is_atomic_ordered_and_version_checked()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        Guid firstAsset = Guid.CreateVersion7();
        Guid secondAsset = Guid.CreateVersion7();
        Guid thirdAsset = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(
            database.Context,
            tenantId,
            ownerId,
            [firstAsset, secondAsset, thirdAsset]);
        var application = new AlbumApplication(
            new RelationalGalleryCurationStore(database.Context));
        var actor = new CurationActor(tenantId, ownerId, canManageAll: false);
        Assert.True((await application.CreateAsync(
            actor,
            albumId,
            "Album",
            null,
            "create",
            Now,
            CancellationToken.None)).IsSuccess);

        CurationResult<AlbumSnapshot> added = await application.AddItemsAsync(
            actor,
            albumId,
            1,
            [
                new VersionedAssetTarget(secondAsset, 1),
                new VersionedAssetTarget(firstAsset, 1),
                new VersionedAssetTarget(thirdAsset, 99),
            ],
            "add-invalid",
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(CurationFailureKind.Conflict, added.Error!.Kind);
        Assert.Empty(await database.Context.AlbumItems.ToListAsync());
        Assert.All(await database.Context.Assets.ToListAsync(), asset => Assert.Equal(1, asset.Version));

        added = await application.AddItemsAsync(
            actor,
            albumId,
            1,
            [
                new VersionedAssetTarget(secondAsset, 1),
                new VersionedAssetTarget(firstAsset, 1),
                new VersionedAssetTarget(thirdAsset, 1),
            ],
            "add",
            Now.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(
            [secondAsset, firstAsset, thirdAsset],
            added.Value!.Items.Select(item => item.Asset.Id).ToArray());
        Assert.Equal([0L, 1L, 2L], added.Value.Items.Select(item => item.Position).ToArray());
        Assert.Equal(2, added.Value.Version);

        CurationResult<AlbumSnapshot> reordered = await application.ReorderItemsAsync(
            actor,
            albumId,
            2,
            [
                new AlbumItemPosition(thirdAsset, 0),
                new AlbumItemPosition(secondAsset, 1),
                new AlbumItemPosition(firstAsset, 2),
            ],
            "reorder",
            Now.AddMinutes(3),
            CancellationToken.None);

        Assert.Equal(
            [thirdAsset, secondAsset, firstAsset],
            reordered.Value!.Items.Select(item => item.Asset.Id).ToArray());
        Assert.Equal(3, reordered.Value.Version);

        CurationResult<AlbumSnapshot> removed = await application.RemoveItemsAsync(
            actor,
            albumId,
            3,
            [new VersionedAssetTarget(secondAsset, 2)],
            "remove",
            Now.AddMinutes(4),
            CancellationToken.None);

        Assert.Equal(
            [thirdAsset, firstAsset],
            removed.Value!.Items.Select(item => item.Asset.Id).ToArray());
        Assert.Equal([0L, 1L], removed.Value.Items.Select(item => item.Position).ToArray());
        Assert.Equal(4, removed.Value.Version);
    }

    [Fact]
    public async Task Album_ownership_and_cross_tenant_assets_are_concealed_without_partial_writes()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid otherUserId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        Guid foreignAssetId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId);
        database.Context.Users.Add(User(otherUserId));
        database.Context.Albums.Add(Album(tenantId, albumId, ownerId));
        await database.Context.SaveChangesAsync();
        await using (VistaraDbContext other =
            database.CreateContext(otherTenantId))
        {
            other.Tenants.Add(Tenant(otherTenantId));
            other.Assets.Add(Asset(otherTenantId, foreignAssetId, otherUserId));
            await other.SaveChangesAsync();
        }

        var application = new AlbumApplication(
            new RelationalGalleryCurationStore(database.Context));
        CurationResult<AlbumSnapshot> forbidden = await application.UpdateAsync(
            new CurationActor(tenantId, otherUserId, canManageAll: false),
            albumId,
            1,
            new AlbumUpdate(
                OptionalField.Specified("Renamed"),
                OptionalField.Unspecified<string>(),
                OptionalField.Unspecified<Guid?>()),
            "rename",
            Now,
            CancellationToken.None);
        CurationResult<AlbumSnapshot> concealed = await application.AddItemsAsync(
            new CurationActor(tenantId, ownerId, canManageAll: false),
            albumId,
            1,
            [new VersionedAssetTarget(foreignAssetId, 1)],
            "foreign-add",
            Now,
            CancellationToken.None);

        Assert.Equal(CurationFailureKind.Forbidden, forbidden.Error!.Kind);
        Assert.Equal(CurationFailureKind.NotFound, concealed.Error!.Kind);
        Assert.Empty(await database.Context.AlbumItems.ToListAsync());
    }

    [Fact]
    public async Task Tags_are_flat_normalized_unique_and_concurrency_checked()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid travelId = Guid.CreateVersion7();
        Guid familyId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId);
        var application = new TagApplication(
            new RelationalGalleryCurationStore(database.Context));
        var actor = new CurationActor(tenantId, ownerId, canManageAll: false);

        CurationResult<TagSnapshot> travel = await application.CreateAsync(
            actor,
            travelId,
            "  TrAvEl  ",
            "#123456",
            "travel",
            Now,
            CancellationToken.None);
        CurationResult<TagSnapshot> duplicate = await application.CreateAsync(
            actor,
            Guid.CreateVersion7(),
            "travel",
            null,
            "travel-duplicate",
            Now,
            CancellationToken.None);
        CurationResult<TagSnapshot> family = await application.CreateAsync(
            actor,
            familyId,
            "Family",
            null,
            "family",
            Now,
            CancellationToken.None);
        CurationResult<TagSnapshot> renameConflict = await application.UpdateAsync(
            actor,
            familyId,
            family.Value!.Version,
            new TagUpdate(
                OptionalField.Specified("TRAVEL"),
                OptionalField.Unspecified<string>()),
            "family-rename",
            Now,
            CancellationToken.None);
        CurationResult<TagSnapshot> stale = await application.UpdateAsync(
            actor,
            travelId,
            99,
            new TagUpdate(
                OptionalField.Specified("Trips"),
                OptionalField.Unspecified<string>()),
            "travel-stale",
            Now,
            CancellationToken.None);

        Assert.Equal("TrAvEl", travel.Value!.Name);
        Assert.Equal(CurationFailureKind.Conflict, duplicate.Error!.Kind);
        Assert.Equal("tag_name_conflict", duplicate.Error.Code);
        Assert.Equal(CurationFailureKind.Conflict, renameConflict.Error!.Kind);
        Assert.Equal("tag_version_conflict", stale.Error!.Code);
        Assert.Equal(2, await database.Context.Tags.CountAsync());
    }

    [Fact]
    public async Task Favorites_are_user_scoped_idempotent_and_increment_asset_etags_once()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId, [assetId]);
        var application = new FavoriteApplication(
            new RelationalGalleryCurationStore(database.Context));
        var actor = new CurationActor(tenantId, ownerId, canManageAll: false);

        CurationResult<CuratedAssetSnapshot> added = await application.SetAsync(
            actor,
            assetId,
            1,
            true,
            "favorite",
            Now,
            CancellationToken.None);
        CurationResult<CuratedAssetSnapshot> replay = await application.SetAsync(
            actor,
            assetId,
            1,
            true,
            "favorite",
            Now.AddMinutes(1),
            CancellationToken.None);
        CurationResult<CuratedAssetSnapshot> noOp = await application.SetAsync(
            actor,
            assetId,
            2,
            true,
            "favorite-again",
            Now.AddMinutes(2),
            CancellationToken.None);
        CurationResult<CuratedAssetSnapshot> removed = await application.SetAsync(
            actor,
            assetId,
            2,
            false,
            "unfavorite",
            Now.AddMinutes(3),
            CancellationToken.None);

        Assert.Equal(2, added.Value!.Version);
        Assert.Equal(2, replay.Value!.Version);
        Assert.Equal(2, noOp.Value!.Version);
        Assert.Equal(3, removed.Value!.Version);
        Assert.False(removed.Value.Favorite);
        Assert.Empty(await database.Context.AssetFavorites.ToListAsync());
    }

    [Fact]
    public async Task Bulk_execution_returns_one_result_per_item_and_commits_items_independently()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid tagId = Guid.CreateVersion7();
        Guid firstAsset = Guid.CreateVersion7();
        Guid secondAsset = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId, [firstAsset, secondAsset]);
        database.Context.Tags.Add(new TagRow
        {
            Id = tagId,
            TenantId = tenantId,
            DisplayName = "Travel",
            NormalizedName = "travel",
            Version = 1,
        });
        await database.Context.SaveChangesAsync();
        var store = new RelationalGalleryCurationStore(database.Context);
        var actor = new CurationActor(tenantId, ownerId, canManageAll: false);
        var request = new BulkCurationRequest(
            [
                new BulkCurationTarget(firstAsset, 1),
                new BulkCurationTarget(secondAsset, 99),
            ],
            new BulkCurationAction("addTag", tagId, null, null));

        IReadOnlyList<BulkCurationItemResult> results = await store.ExecuteBulkAsync(
            actor,
            request,
            Now,
            CancellationToken.None);

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal("succeeded", result.Status);
                Assert.Equal(2, result.Version);
                Assert.Null(result.ErrorCode);
            },
            result =>
            {
                Assert.Equal("conflict", result.Status);
                Assert.Null(result.Version);
                Assert.Equal("asset_version_conflict", result.ErrorCode);
            });
        Assert.Single(await database.Context.AssetTags.ToListAsync());
    }

    [Fact]
    public async Task Bulk_submission_is_idempotent_and_preserves_per_item_versions()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId, [assetId]);
        var application = new FavoriteApplication(
            new RelationalGalleryCurationStore(database.Context));
        var actor = new CurationActor(tenantId, ownerId, canManageAll: false);
        var request = new BulkCurationRequest(
            [new BulkCurationTarget(assetId, 1)],
            new BulkCurationAction("setFavorite", null, null, true));

        CurationResult<BulkCurationSubmission> queued = await application.QueueBulkAsync(
            actor,
            jobId,
            request,
            "bulk",
            Now,
            CancellationToken.None);
        CurationResult<BulkCurationSubmission> replay = await application.QueueBulkAsync(
            actor,
            Guid.CreateVersion7(),
            request,
            "bulk",
            Now.AddMinutes(1),
            CancellationToken.None);
        CurationResult<BulkCurationSubmission> conflict = await application.QueueBulkAsync(
            actor,
            Guid.CreateVersion7(),
            request with
            {
                Action = new BulkCurationAction("setFavorite", null, null, false),
            },
            "bulk",
            Now.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(jobId, queued.Value!.JobId);
        Assert.Equal(jobId, replay.Value!.JobId);
        Assert.Equal(1, replay.Value.SubmittedCount);
        Assert.Equal(CurationFailureKind.IdempotencyConflict, conflict.Error!.Kind);
    }

    private static async Task SeedAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid ownerId,
        IReadOnlyList<Guid>? assetIds = null)
    {
        context.Users.Add(User(ownerId));
        context.Tenants.Add(Tenant(tenantId));
        foreach (Guid assetId in assetIds ?? [])
        {
            context.Assets.Add(Asset(tenantId, assetId, ownerId));
        }

        await context.SaveChangesAsync();
    }

    private static TenantRow Tenant(Guid id) => new()
    {
        Id = id,
        TenantId = id,
        Slug = $"tenant-{id:N}",
        Name = "Tenant",
        Status = "Active",
        SettingsJson = "{}",
        QuotasJson = "{}",
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
        Version = 1,
    };

    private static UserRow User(Guid id) => new()
    {
        Id = id,
        NormalizedEmail = $"{id:N}@example.test",
        DisplayName = "User",
        Status = "Active",
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
        Version = 1,
    };

    private static AssetRow Asset(Guid tenantId, Guid id, Guid ownerId) => new()
    {
        Id = id,
        TenantId = tenantId,
        OwnerId = ownerId,
        Title = id.ToString("N"),
        Status = "Ready",
        Visibility = "Private",
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
        Version = 1,
    };

    private static AlbumRow Album(Guid tenantId, Guid id, Guid ownerId) => new()
    {
        Id = id,
        TenantId = tenantId,
        OwnerId = ownerId,
        Name = "Album",
        SortMode = "Manual",
        Version = 1,
    };
}
