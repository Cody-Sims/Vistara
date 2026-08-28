using Vistara.Domain.Common;

namespace Vistara.Domain.Gallery;

public sealed class FavoriteSet
{
    private readonly List<FavoriteItem> _items = [];

    public FavoriteSet(GalleryTenantId tenantId, GalleryUserId userId)
    {
        if (tenantId.Value == Guid.Empty || userId.Value == Guid.Empty)
        {
            throw new ArgumentException("Favorite owner identifiers cannot be empty.");
        }

        TenantId = tenantId;
        UserId = userId;
    }

    public GalleryTenantId TenantId { get; }

    public GalleryUserId UserId { get; }

    public long Version { get; private set; }

    public IReadOnlyList<FavoriteItem> Items => _items.AsReadOnly();

    public Result Add(GalleryAssetRef asset, DateTimeOffset addedAtUtc)
    {
        if (asset.TenantId != TenantId)
        {
            return Result.Failure(GalleryErrors.CrossTenantReference());
        }

        if (!GalleryTime.IsUtc(addedAtUtc))
        {
            return Result.Failure(GalleryErrors.TimestampMustBeUtc());
        }

        if (_items.Any(item => item.AssetId == asset.AssetId))
        {
            return Result.Success();
        }

        _items.Add(new FavoriteItem(asset.AssetId, addedAtUtc));
        Version++;
        return Result.Success();
    }

    public Result Remove(GalleryAssetId assetId)
    {
        int index = _items.FindIndex(item => item.AssetId == assetId);
        if (index < 0)
        {
            return Result.Success();
        }

        _items.RemoveAt(index);
        Version++;
        return Result.Success();
    }
}

public sealed record FavoriteItem(
    GalleryAssetId AssetId,
    DateTimeOffset AddedAtUtc);
