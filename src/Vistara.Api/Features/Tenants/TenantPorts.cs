using Vistara.Domain.Common;

namespace Vistara.Api.Features.Tenants;

public sealed record TenantMembershipView(
    Guid TenantId,
    string Slug,
    string Name,
    string TenantStatus,
    string Role,
    string MembershipStatus,
    DateTimeOffset? JoinedAt,
    long Version);

public sealed record TenantMemberView(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    DateTimeOffset InvitedAt,
    DateTimeOffset? JoinedAt,
    long Version);

public sealed record TenantMemberUpdate(
    Guid TenantId,
    Guid ActorUserId,
    string ActorRole,
    Guid MemberUserId,
    string? Role,
    string? Status);

public sealed record TenantMemberInvitation(
    Guid TenantId,
    Guid ActorUserId,
    string Email,
    string Role);

/// <summary>
/// Tenant and member administration backed by the existing tenancy
/// repositories, factories, and audit writer.
/// </summary>
public interface ITenantDirectoryPort
{
    /// <summary>
    /// Lists the principal's tenants. When
    /// <paramref name="restrictToTenantId"/> is supplied the projection is
    /// limited to that tenant, so a tenant-bound credential cannot discover
    /// where its owner is a member elsewhere.
    /// </summary>
    ValueTask<IReadOnlyList<TenantMembershipView>> ListTenantsForUserAsync(
        Guid userId,
        Guid? restrictToTenantId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<TenantMemberView>> ListMembersAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    ValueTask<Result<TenantMemberView>> InviteMemberAsync(
        TenantMemberInvitation invitation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a role or status change guarded by the membership version. The
    /// tenant must keep at least one active owner.
    /// </summary>
    ValueTask<Result<TenantMemberView>> UpdateMemberAsync(
        TenantMemberUpdate update,
        long expectedVersion,
        CancellationToken cancellationToken);
}
