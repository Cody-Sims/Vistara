namespace Vistara.Domain.Gallery;

public readonly record struct AlbumId
{
    public AlbumId(Guid value)
    {
        GalleryIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct TagId
{
    public TagId(Guid value)
    {
        GalleryIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct GalleryTenantId
{
    public GalleryTenantId(Guid value)
    {
        GalleryIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct GalleryUserId
{
    public GalleryUserId(Guid value)
    {
        GalleryIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct GalleryAssetId
{
    public GalleryAssetId(Guid value)
    {
        GalleryIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct GalleryAssetRef(
    GalleryTenantId TenantId,
    GalleryAssetId AssetId);

internal static class GalleryIdGuard
{
    public static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Gallery IDs must be non-empty UUIDv7 values.",
                parameterName);
        }
    }
}
