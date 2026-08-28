using Vistara.Domain.Common;
using Vistara.Domain.Gallery;

namespace Vistara.UnitTests.Gallery;

public sealed class GalleryDomainTests
{
    private static readonly GalleryTenantId Tenant = new(Guid.Parse("0195a111-1111-7111-8111-111111111111"));
    private static readonly GalleryTenantId OtherTenant = new(Guid.Parse("0195a222-2222-7222-8222-222222222222"));
    private static readonly GalleryUserId Owner = new(Guid.Parse("0195a333-3333-7333-8333-333333333333"));
    private static readonly DateTimeOffset AddedAt = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Album_membership_is_unique_tenant_scoped_and_reorderable()
    {
        Album album = Value(Album.Create(
            new AlbumId(Guid.Parse("0195a444-4444-7444-8444-444444444444")),
            Tenant,
            Owner,
            "Portfolio",
            "Selected work"));
        GalleryAssetRef first = Asset("0195a555-5555-7555-8555-555555555555", Tenant);
        GalleryAssetRef second = Asset("0195a666-6666-7666-8666-666666666666", Tenant);
        GalleryAssetRef third = Asset("0195a777-7777-7777-8777-777777777777", Tenant);

        Assert.True(album.AddAsset(first, Owner, AddedAt).IsSuccess);
        Assert.True(album.AddAsset(second, Owner, AddedAt.AddMinutes(1)).IsSuccess);
        Assert.True(album.AddAsset(third, Owner, AddedAt.AddMinutes(2)).IsSuccess);

        Result duplicate = album.AddAsset(first, Owner, AddedAt.AddMinutes(3));
        Result crossTenant = album.AddAsset(
            Asset("0195a888-8888-7888-8888-888888888888", OtherTenant),
            Owner,
            AddedAt.AddMinutes(4));
        Result moved = album.MoveAsset(third.AssetId, 0, album.Version);

        Assert.Equal("gallery.album_item_duplicate", duplicate.Error?.Code);
        Assert.Equal(ErrorCategory.Forbidden, crossTenant.Error?.Category);
        Assert.True(moved.IsSuccess);
        Assert.Equal([third.AssetId, first.AssetId, second.AssetId], album.Items.Select(item => item.AssetId));
        Assert.Equal([0L, 1L, 2L], album.Items.Select(item => item.Position));
    }

    [Fact]
    public void Tag_catalog_normalizes_names_and_rejects_equivalent_duplicates()
    {
        var catalog = new TagCatalog(Tenant);

        Result<Tag> created = catalog.CreateTag(
            new TagId(Guid.Parse("0195a999-9999-7999-8999-999999999999")),
            "  Café   Noir  ",
            "#112233");
        Result<Tag> duplicate = catalog.CreateTag(
            new TagId(Guid.Parse("0195aaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa")),
            "CAFE\u0301 NOIR",
            null);

        Tag tag = Value(created);
        Assert.Equal("Café Noir", tag.DisplayName);
        Assert.Equal("café noir", tag.NormalizedName);
        Assert.Equal("gallery.tag_name_duplicate", duplicate.Error?.Code);
        Assert.Single(catalog.Tags);
    }

    [Fact]
    public void Favorites_are_unique_and_reject_cross_tenant_assets()
    {
        var favorites = new FavoriteSet(Tenant, Owner);
        GalleryAssetRef asset = Asset("0195abbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb", Tenant);

        Assert.True(favorites.Add(asset, AddedAt).IsSuccess);
        Assert.True(favorites.Add(asset, AddedAt.AddMinutes(1)).IsSuccess);
        Result crossTenant = favorites.Add(
            Asset("0195accc-cccc-7ccc-8ccc-cccccccccccc", OtherTenant),
            AddedAt);

        Assert.Single(favorites.Items);
        Assert.Equal(1, favorites.Version);
        Assert.Equal(ErrorCategory.Forbidden, crossTenant.Error?.Category);
    }

    [Fact]
    public void Gallery_identifiers_require_non_empty_uuid7_values()
    {
        Action<Guid>[] constructors =
        [
            value => _ = new AlbumId(value),
            value => _ = new TagId(value),
            value => _ = new GalleryTenantId(value),
            value => _ = new GalleryUserId(value),
            value => _ = new GalleryAssetId(value),
        ];
        Guid versionFour = Guid.Parse("11111111-1111-4111-8111-111111111111");

        foreach (Action<Guid> construct in constructors)
        {
            Assert.Throws<ArgumentException>(() => construct(Guid.Empty));
            Assert.Throws<ArgumentException>(() => construct(versionFour));
        }
    }

    private static GalleryAssetRef Asset(string id, GalleryTenantId tenantId) =>
        new(tenantId, new GalleryAssetId(Guid.Parse(id)));

    private static T Value<T>(Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Message);
        return value;
    }
}
