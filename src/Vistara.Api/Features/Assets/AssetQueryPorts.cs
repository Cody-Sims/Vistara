using Microsoft.AspNetCore.Http;

namespace Vistara.Api.Features.Assets;

public enum AssetQueryAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
    Concealed,
}

public sealed record AssetQueryAccess
{
    private AssetQueryAccess(
        AssetQueryAccessStatus status,
        Guid? tenantId,
        Guid? actorId,
        bool canReadRestrictedMetadata,
        bool canUpdateMetadata)
    {
        Status = status;
        TenantId = tenantId;
        ActorId = actorId;
        CanReadRestrictedMetadata = canReadRestrictedMetadata;
        CanUpdateMetadata = canUpdateMetadata;
    }

    public AssetQueryAccessStatus Status { get; }
    public Guid? TenantId { get; }
    public Guid? ActorId { get; }
    public bool CanReadRestrictedMetadata { get; }
    public bool CanUpdateMetadata { get; }

    public static AssetQueryAccess Authorized(
        Guid tenantId,
        Guid actorId,
        bool canReadRestrictedMetadata,
        bool canUpdateMetadata)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        return new AssetQueryAccess(
            AssetQueryAccessStatus.Authorized,
            tenantId,
            actorId,
            canReadRestrictedMetadata,
            canUpdateMetadata);
    }

    public static AssetQueryAccess Denied(AssetQueryAccessStatus status)
    {
        if (status == AssetQueryAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new AssetQueryAccess(status, null, null, false, false);
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The identifier must be UUIDv7.", parameterName);
        }
    }
}

public interface IAssetQueryAuthorizationPort
{
    ValueTask<AssetQueryAccess> AuthorizeCollectionAsync(
        HttpContext context,
        CancellationToken cancellationToken);

    ValueTask<AssetQueryAccess> AuthorizeAssetAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken);
}
