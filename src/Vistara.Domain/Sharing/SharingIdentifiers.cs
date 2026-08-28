namespace Vistara.Domain.Sharing;

public readonly record struct ShareId
{
    public ShareId(Guid value)
    {
        SharingIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct ResourceGrantId
{
    public ResourceGrantId(Guid value)
    {
        SharingIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct SharingTenantId
{
    public SharingTenantId(Guid value)
    {
        SharingIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct SharingUserId
{
    public SharingUserId(Guid value)
    {
        SharingIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct SharingAlbumId
{
    public SharingAlbumId(Guid value)
    {
        SharingIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct SharedAssetId
{
    public SharedAssetId(Guid value)
    {
        SharingIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

internal static class SharingIdGuard
{
    public static void EnsureUuid7(Guid value, string parameterName)
    {
        if (!IsUuid7(value))
        {
            throw new ArgumentException(
                "Sharing IDs must be non-empty UUIDv7 values.",
                parameterName);
        }
    }

    public static bool IsUuid7(Guid value) =>
        value != Guid.Empty && value.Version == 7;
}
