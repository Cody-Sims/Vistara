using System.Globalization;
using System.Security.Claims;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Albums;
using Vistara.Api.Features.Assets;
using Vistara.Api.Features.Lifecycle;
using Vistara.Api.Features.Shares;
using Vistara.Application.Common;
using Vistara.Application.Gallery;
using Vistara.Application.Lifecycle;

namespace Vistara.Api.Composition.Gallery;

internal sealed class GalleryAssetQueryAuthorizationPort(
    IPlatformTenantContext tenantContext) : IAssetQueryAuthorizationPort
{
    public ValueTask<AssetQueryAccess> AuthorizeCollectionAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Authorize(context, null, cancellationToken));

    public ValueTask<AssetQueryAccess> AuthorizeAssetAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Authorize(context, assetId, cancellationToken));

    private AssetQueryAccess Authorize(
        HttpContext context,
        Guid? assetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return AssetQueryAccess.Denied(
                AssetQueryAccessStatus.Unauthenticated);
        }

        if (assetId is { } id && !GalleryPrincipalReader.IsUuid7(id))
        {
            return AssetQueryAccess.Denied(AssetQueryAccessStatus.Concealed);
        }

        if (!GalleryPrincipalReader.TryRead(
                context.User,
                tenantContext,
                out Guid tenantId,
                out Guid actorId))
        {
            return AssetQueryAccess.Denied(AssetQueryAccessStatus.Forbidden);
        }

        bool canRead = context.User.HasClaim("scope", "assets.read");
        bool canManageMetadata =
            context.User.HasClaim("scope", "metadata.manage");
        if (!canRead && !canManageMetadata)
        {
            return AssetQueryAccess.Denied(AssetQueryAccessStatus.Forbidden);
        }

        return AssetQueryAccess.Authorized(
            tenantId,
            actorId,
            canManageMetadata,
            canManageMetadata);
    }
}

internal sealed class GalleryCurationAuthorizationPort(
    IPlatformTenantContext tenantContext) : IGalleryCurationAuthorizationPort
{
    public ValueTask<GalleryCurationAccess> AuthorizeAsync(
        HttpContext context,
        GalleryCurationOperation operation,
        Guid? resourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(
                GalleryCurationAccess.Denied(
                    GalleryCurationAccessStatus.Unauthenticated));
        }

        if (resourceId is { } id && !GalleryPrincipalReader.IsUuid7(id))
        {
            return ValueTask.FromResult(
                GalleryCurationAccess.Denied(
                    GalleryCurationAccessStatus.Concealed));
        }

        if (!GalleryPrincipalReader.TryRead(
                context.User,
                tenantContext,
                out Guid tenantId,
                out Guid actorId))
        {
            return ValueTask.FromResult(
                GalleryCurationAccess.Denied(
                    GalleryCurationAccessStatus.Forbidden));
        }

        string requiredScope = operation is
            GalleryCurationOperation.ReadAlbums or
            GalleryCurationOperation.ReadTags
                ? "assets.read"
                : "metadata.manage";
        if (!context.User.HasClaim("scope", requiredScope))
        {
            return ValueTask.FromResult(
                GalleryCurationAccess.Denied(
                    GalleryCurationAccessStatus.Forbidden));
        }

        string? role = context.User.FindFirstValue(ClaimTypes.Role);
        bool canManageAll =
            role is "TenantOwner" or "TenantAdmin";
        return ValueTask.FromResult(
            GalleryCurationAccess.Authorized(
                new CurationActor(tenantId, actorId, canManageAll)));
    }
}

internal sealed class GalleryShareAuthorizationPort(
    IPlatformTenantContext tenantContext) : IShareAuthorizationPort
{
    public ValueTask<ShareAccessDecision> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(
                ShareAccessDecision.Denied(
                    ShareAccessDecisionStatus.Unauthenticated));
        }

        if (!GalleryPrincipalReader.TryRead(
                context.User,
                tenantContext,
                out Guid tenantId,
                out Guid actorId) ||
            (!context.User.HasClaim("scope", "shares.manage") &&
             !context.User.HasClaim("scope", "metadata.manage")))
        {
            return ValueTask.FromResult(
                ShareAccessDecision.Denied(
                    ShareAccessDecisionStatus.Forbidden));
        }

        return ValueTask.FromResult(
            ShareAccessDecision.Authorized(tenantId, actorId));
    }
}

internal sealed class GalleryLifecycleAuthorizationPort(
    IPlatformTenantContext tenantContext,
    IClock clock) : ILifecycleAuthorizationPort
{
    public ValueTask<LifecycleAccess> AuthorizeAsync(
        HttpContext context,
        LifecycleApiOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(
                LifecycleAccess.Denied(LifecycleAccessStatus.Unauthenticated));
        }

        if (!GalleryPrincipalReader.TryRead(
                context.User,
                tenantContext,
                out Guid tenantId,
                out Guid actorId))
        {
            return ValueTask.FromResult(
                LifecycleAccess.Denied(LifecycleAccessStatus.Forbidden));
        }

        bool canRead = context.User.HasClaim("scope", "assets.read");
        bool canMutate = context.User.HasClaim("scope", "metadata.manage");
        if (operation == LifecycleApiOperation.ListTrash ? !canRead : !canMutate)
        {
            return ValueTask.FromResult(
                LifecycleAccess.Denied(LifecycleAccessStatus.Forbidden));
        }

        string? role = context.User.FindFirstValue(ClaimTypes.Role);
        LifecycleRights rights =
            LifecycleRights.ListTrash |
            LifecycleRights.Trash |
            LifecycleRights.Restore;
        if (role is "TenantOwner" or "TenantAdmin")
        {
            rights |= LifecycleRights.ManageHolds;
        }

        DateTimeOffset? authenticatedAt = ReadAuthenticationTime(context.User);
        if (role == "TenantOwner" && authenticatedAt.HasValue)
        {
            rights |= LifecycleRights.Purge;
        }

        bool apiKey = string.Equals(
            context.User.FindFirstValue("vistara_auth_kind"),
            PlatformAuthenticationKind.ApiKey.ToString(),
            StringComparison.Ordinal);
        LifecycleActorContext actor = apiKey
            ? LifecycleActorContext.ApiKey(tenantId, actorId, rights)
            : LifecycleActorContext.Human(
                tenantId,
                actorId,
                rights,
                authenticatedAt ?? DateTimeOffset.UnixEpoch);
        return ValueTask.FromResult(LifecycleAccess.Authorized(actor));
    }

    private DateTimeOffset? ReadAuthenticationTime(ClaimsPrincipal principal)
    {
        string? value = principal.FindFirstValue("auth_time");
        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long seconds))
        {
            return null;
        }

        DateTimeOffset authenticatedAt;
        try
        {
            authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return authenticatedAt <= clock.UtcNow ? authenticatedAt : null;
    }
}

internal static class GalleryPrincipalReader
{
    internal static bool TryRead(
        ClaimsPrincipal principal,
        IPlatformTenantContext tenantContext,
        out Guid tenantId,
        out Guid actorId)
    {
        tenantId = default;
        actorId = default;
        return principal.Identity?.IsAuthenticated == true &&
            TryReadUuid7(principal, "tenant_id", out tenantId) &&
            TryReadUuid7(
                principal,
                ClaimTypes.NameIdentifier,
                out actorId) &&
            tenantContext.TenantId == tenantId;
    }

    internal static bool IsUuid7(Guid value) =>
        value != Guid.Empty && value.Version == 7;

    private static bool TryReadUuid7(
        ClaimsPrincipal principal,
        string claimType,
        out Guid value)
    {
        value = default;
        string[] values = principal.FindAll(claimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return values.Length == 1 &&
            Guid.TryParse(values[0], out value) &&
            IsUuid7(value);
    }
}
