using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;

namespace Vistara.Application.Tenancy.Authorization;

public enum TenantAction
{
    ReadAssets,
    UploadAssets,
    ManageMetadata,
    ManageMembers,
    ManageApiKeys,
    ManageQuotas,
    ManageShares,
}

public enum ObjectAction
{
    Read,
    DownloadOriginal,
    RequestDerivative,
    UpdateMetadata,
    Trash,
    Restore,
    Share,
    Purge,
}

public sealed record TenantObjectReference(TenantId TenantId, Guid OwnerId);

public static class AuthorizationPolicy
{
    public static Result AuthorizeTenant(
        AuthenticatedActor actor,
        TenantId tenantId,
        TenantAction action)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.TenantId != tenantId)
        {
            return Result.Failure(AuthorizationErrors.ResourceNotFound);
        }

        (TenantRole minimumRole, ActorScope requiredScope) = action switch
        {
            TenantAction.ReadAssets => (TenantRole.Viewer, ActorScope.ReadAssets),
            TenantAction.UploadAssets => (TenantRole.Member, ActorScope.UploadAssets),
            TenantAction.ManageMetadata => (TenantRole.Member, ActorScope.ManageMetadata),
            TenantAction.ManageMembers => (TenantRole.TenantAdmin, ActorScope.ManageMembers),
            TenantAction.ManageApiKeys => (TenantRole.TenantAdmin, ActorScope.ManageApiKeys),
            TenantAction.ManageQuotas => (TenantRole.TenantOwner, ActorScope.ManageQuotas),
            TenantAction.ManageShares => (TenantRole.Member, ActorScope.ManageShares),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        return HasMinimumRole(actor.Role, minimumRole) && actor.HasScope(requiredScope)
            ? Result.Success()
            : Result.Failure(AuthorizationErrors.Forbidden);
    }

    public static Result AuthorizeObject(
        AuthenticatedActor actor,
        TenantId requestedTenantId,
        TenantObjectReference? resource,
        ObjectAction action)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.TenantId != requestedTenantId)
        {
            return Result.Failure(AuthorizationErrors.ResourceNotFound);
        }

        Result tenantPermission = AuthorizeTenant(
            actor,
            requestedTenantId,
            RequiredTenantAction(action));
        if (tenantPermission.IsFailure)
        {
            return tenantPermission;
        }

        if (resource is null || resource.TenantId != requestedTenantId)
        {
            return Result.Failure(AuthorizationErrors.ResourceNotFound);
        }

        if (!RequiresOwnership(action))
        {
            return Result.Success();
        }

        if (action == ObjectAction.Purge)
        {
            return actor.Role == TenantRole.TenantOwner
                ? Result.Success()
                : Result.Failure(AuthorizationErrors.Forbidden);
        }

        bool elevated = actor.Role is TenantRole.TenantOwner or TenantRole.TenantAdmin;
        return elevated || resource.OwnerId == actor.UserId.Value
            ? Result.Success()
            : Result.Failure(AuthorizationErrors.Forbidden);
    }

    private static TenantAction RequiredTenantAction(ObjectAction action) =>
        action switch
        {
            ObjectAction.Read or
            ObjectAction.DownloadOriginal or
            ObjectAction.RequestDerivative => TenantAction.ReadAssets,
            ObjectAction.UpdateMetadata or
            ObjectAction.Trash or
            ObjectAction.Restore or
            ObjectAction.Purge => TenantAction.ManageMetadata,
            ObjectAction.Share => TenantAction.ManageShares,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static bool RequiresOwnership(ObjectAction action) =>
        action is ObjectAction.UpdateMetadata or
            ObjectAction.Trash or
            ObjectAction.Restore or
            ObjectAction.Share or
            ObjectAction.Purge;

    private static bool HasMinimumRole(TenantRole actual, TenantRole minimum) =>
        Rank(actual) >= Rank(minimum);

    private static int Rank(TenantRole role) =>
        role switch
        {
            TenantRole.Viewer => 0,
            TenantRole.Member => 1,
            TenantRole.TenantAdmin => 2,
            TenantRole.TenantOwner => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
}
