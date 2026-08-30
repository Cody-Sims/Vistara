using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Application.Tenancy.Authorization;

[Flags]
public enum ActorScope
{
    None = 0,
    ReadAssets = 1 << 0,
    UploadAssets = 1 << 1,
    ManageMetadata = 1 << 2,
    ManageMembers = 1 << 3,
    ManageApiKeys = 1 << 4,
    ManageQuotas = 1 << 5,
    ManageShares = 1 << 6,
    All = ReadAssets |
        UploadAssets |
        ManageMetadata |
        ManageMembers |
        ManageApiKeys |
        ManageQuotas |
        ManageShares,
}

public sealed record AuthenticatedActor
{
    private AuthenticatedActor(
        UserId userId,
        TenantId tenantId,
        TenantRole role,
        ActorScope scopes)
    {
        UserId = userId;
        TenantId = tenantId;
        Role = role;
        Scopes = scopes;
    }

    public UserId UserId { get; }

    public TenantId TenantId { get; }

    public TenantRole Role { get; }

    public ActorScope Scopes { get; }

    public bool HasScope(ActorScope scope) =>
        scope != ActorScope.None &&
        (scope & ~ActorScope.All) == ActorScope.None &&
        (Scopes & scope) == scope;

    public static Result<AuthenticatedActor> Create(
        UserId authenticatedUserId,
        TenantMembership membership,
        ActorScope scopes)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (membership.UserId != authenticatedUserId)
        {
            return Result.Failure<AuthenticatedActor>(
                AuthorizationErrors.MembershipPrincipalMismatch);
        }

        if (membership.Status != MembershipStatus.Active)
        {
            return Result.Failure<AuthenticatedActor>(
                AuthorizationErrors.InactiveMembership);
        }

        if ((scopes & ~ActorScope.All) != ActorScope.None)
        {
            throw new ArgumentOutOfRangeException(nameof(scopes));
        }

        return Result.Success(new AuthenticatedActor(
            authenticatedUserId,
            membership.TenantId,
            membership.Role,
            scopes));
    }
}
