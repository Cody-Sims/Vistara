using Vistara.Domain.Common;

namespace Vistara.Domain.Gallery;

public sealed class Album
{
    private readonly List<AlbumItem> _items = [];

    private Album(
        AlbumId id,
        GalleryTenantId tenantId,
        GalleryUserId ownerId,
        string name,
        string? description)
    {
        Id = id;
        TenantId = tenantId;
        OwnerId = ownerId;
        Name = name;
        Description = description;
        Version = 1;
    }

    public AlbumId Id { get; }

    public GalleryTenantId TenantId { get; }

    public GalleryUserId OwnerId { get; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public GalleryAssetId? CoverAssetId { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<AlbumItem> Items => _items.AsReadOnly();

    public static Result<Album> Create(
        AlbumId id,
        GalleryTenantId tenantId,
        GalleryUserId ownerId,
        string name,
        string? description = null)
    {
        if (id.Value == Guid.Empty || tenantId.Value == Guid.Empty || ownerId.Value == Guid.Empty)
        {
            return Result.Failure<Album>(GalleryErrors.InvalidIdentifier());
        }

        string normalizedName = GalleryText.CollapseWhitespace(name);
        if (normalizedName.Length == 0)
        {
            return Result.Failure<Album>(GalleryErrors.AlbumNameRequired());
        }

        string? normalizedDescription = description is null
            ? null
            : GalleryText.CollapseWhitespace(description);

        return Result.Success(new Album(id, tenantId, ownerId, normalizedName, normalizedDescription));
    }

    public Result AddAsset(
        GalleryAssetRef asset,
        GalleryUserId addedBy,
        DateTimeOffset addedAtUtc,
        int? index = null)
    {
        if (asset.TenantId != TenantId)
        {
            return Result.Failure(GalleryErrors.CrossTenantReference());
        }

        if (asset.AssetId.Value == Guid.Empty || addedBy.Value == Guid.Empty)
        {
            return Result.Failure(GalleryErrors.InvalidIdentifier());
        }

        if (!GalleryTime.IsUtc(addedAtUtc))
        {
            return Result.Failure(GalleryErrors.TimestampMustBeUtc());
        }

        if (_items.Any(item => item.AssetId == asset.AssetId))
        {
            return Result.Failure(GalleryErrors.DuplicateAlbumItem());
        }

        int insertionIndex = index ?? _items.Count;
        if (insertionIndex < 0 || insertionIndex > _items.Count)
        {
            return Result.Failure(GalleryErrors.InvalidAlbumPosition());
        }

        _items.Insert(
            insertionIndex,
            new AlbumItem(asset.AssetId, insertionIndex, addedBy, addedAtUtc));
        Reindex();
        Version++;
        return Result.Success();
    }

    public Result MoveAsset(GalleryAssetId assetId, int newIndex, long expectedVersion)
    {
        Result versionCheck = CheckVersion(expectedVersion);
        if (versionCheck.IsFailure)
        {
            return versionCheck;
        }

        int currentIndex = _items.FindIndex(item => item.AssetId == assetId);
        if (currentIndex < 0)
        {
            return Result.Failure(GalleryErrors.AlbumItemNotFound());
        }

        if (newIndex < 0 || newIndex >= _items.Count)
        {
            return Result.Failure(GalleryErrors.InvalidAlbumPosition());
        }

        if (currentIndex == newIndex)
        {
            return Result.Success();
        }

        AlbumItem item = _items[currentIndex];
        _items.RemoveAt(currentIndex);
        _items.Insert(newIndex, item);
        Reindex();
        Version++;
        return Result.Success();
    }

    public Result RemoveAsset(GalleryAssetId assetId, long expectedVersion)
    {
        Result versionCheck = CheckVersion(expectedVersion);
        if (versionCheck.IsFailure)
        {
            return versionCheck;
        }

        int index = _items.FindIndex(item => item.AssetId == assetId);
        if (index < 0)
        {
            return Result.Success();
        }

        _items.RemoveAt(index);
        if (CoverAssetId == assetId)
        {
            CoverAssetId = null;
        }

        Reindex();
        Version++;
        return Result.Success();
    }

    public Result SetCover(GalleryAssetRef? asset, long expectedVersion)
    {
        Result versionCheck = CheckVersion(expectedVersion);
        if (versionCheck.IsFailure)
        {
            return versionCheck;
        }

        if (asset is not null)
        {
            if (asset.Value.TenantId != TenantId)
            {
                return Result.Failure(GalleryErrors.CrossTenantReference());
            }

            if (_items.All(item => item.AssetId != asset.Value.AssetId))
            {
                return Result.Failure(GalleryErrors.AlbumItemNotFound());
            }
        }

        GalleryAssetId? coverAssetId = asset?.AssetId;
        if (CoverAssetId == coverAssetId)
        {
            return Result.Success();
        }

        CoverAssetId = coverAssetId;
        Version++;
        return Result.Success();
    }

    private Result CheckVersion(long expectedVersion) =>
        expectedVersion == Version
            ? Result.Success()
            : Result.Failure(GalleryErrors.VersionConflict());

    private void Reindex()
    {
        for (int index = 0; index < _items.Count; index++)
        {
            _items[index] = _items[index] with { Position = index };
        }
    }
}

public sealed record AlbumItem(
    GalleryAssetId AssetId,
    long Position,
    GalleryUserId AddedBy,
    DateTimeOffset AddedAtUtc);
