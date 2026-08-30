using System.Security.Claims;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Albums;
using Vistara.Api.Features.Assets;
using Vistara.Api.Features.Lifecycle;
using Vistara.Api.Features.Shares;
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
    IPlatformTenantContext tenantContext) : ILifecycleAuthorizationPort
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

        PlatformAuthenticationKind? authenticationKind =
            ReadAuthenticationKind(context);
        if (authenticationKind is null)
        {
            return ValueTask.FromResult(
                LifecycleAccess.Denied(LifecycleAccessStatus.Forbidden));
        }

        bool purgeOperation = operation is
            LifecycleApiOperation.PurgeDryRun or
            LifecycleApiOperation.PurgeConfirm or
            LifecycleApiOperation.PurgeStatus;
        if (purgeOperation &&
            authenticationKind != PlatformAuthenticationKind.Cookie)
        {
            return ValueTask.FromResult(
                LifecycleAccess.Denied(LifecycleAccessStatus.Forbidden));
        }

        if (role == "TenantOwner" &&
            authenticationKind == PlatformAuthenticationKind.Cookie)
        {
            rights |= LifecycleRights.Purge;
        }

        LifecycleActorContext actor =
            authenticationKind == PlatformAuthenticationKind.ApiKey
            ? LifecycleActorContext.ApiKey(tenantId, actorId, rights)
            : LifecycleActorContext.Human(
                tenantId,
                actorId,
                rights,
                ReadReauthentication(context, actorId));
        return ValueTask.FromResult(LifecycleAccess.Authorized(actor));
    }

    private static PlatformAuthenticationKind? ReadAuthenticationKind(
        HttpContext context) =>
        context.Items.TryGetValue(
            PlatformAuthenticationState.KindKey,
            out object? value) &&
        value is PlatformAuthenticationKind kind
            ? kind
            : null;

    private static LifecycleReauthenticationContext? ReadReauthentication(
        HttpContext context,
        Guid actorId)
    {
        if (!context.Items.TryGetValue(
                PlatformAuthenticationState.ReauthenticationKey,
                out object? value) ||
            value is not PlatformReauthenticationContext reauthentication ||
            reauthentication.ActorId != actorId ||
            reauthentication.Strength !=
                PlatformAuthenticationStrength.PrimaryCredential)
        {
            return null;
        }

        return new LifecycleReauthenticationContext(
            actorId,
            reauthentication.VerifiedAtUtc,
            LifecycleAuthenticationStrength.PrimaryCredential);
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
