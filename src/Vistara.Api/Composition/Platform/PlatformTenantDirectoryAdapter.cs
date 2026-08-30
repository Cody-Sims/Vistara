using Microsoft.EntityFrameworkCore;
using Vistara.Api.Features.Tenants;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Tenancy;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Bridges tenant and member administration onto the existing tenancy
/// repositories, factories, directory reads, and audit writer.
/// </summary>
internal sealed class PlatformTenantDirectoryAdapter(
    RelationalTenantDirectory directory,
    IUserRepository users,
    ITenantMembershipRepository memberships,
    IdentityFactory identities,
    TenantFactory tenants,
    IAuditWriter audit,
    IUuid7Generator ids,
    IClock clock) : ITenantDirectoryPort
{
    public async ValueTask<IReadOnlyList<TenantMembershipView>> ListTenantsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PersistedTenantMembership> persisted =
            await directory.ListForUserAsync(userId, cancellationToken);
        return persisted
            .Select(membership => new TenantMembershipView(
                membership.TenantId,
                membership.Slug,
                membership.Name,
                membership.TenantStatus,
                membership.Role,
                membership.MembershipStatus,
                membership.JoinedAtUtc,
                membership.Version))
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<TenantMemberView>> ListMembersAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PersistedTenantMember> persisted =
            await directory.ListMembersAsync(tenantId, cancellationToken);
        return persisted
            .Select(member => new TenantMemberView(
                member.UserId,
                member.Email,
                member.DisplayName,
                member.Role,
                member.MembershipStatus,
                member.InvitedAtUtc,
                member.JoinedAtUtc,
                member.Version))
            .ToArray();
    }

    public async ValueTask<Result<TenantMemberView>> InviteMemberAsync(
        TenantMemberInvitation invitation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        cancellationToken.ThrowIfCancellationRequested();

        Result<NormalizedEmail> email = NormalizedEmail.Create(invitation.Email);
        if (!email.TryGetValue(out NormalizedEmail normalized))
        {
            return Result.Failure<TenantMemberView>(ResultError.Validation(
                "tenants.invalid_email",
                "The member email address is invalid."));
        }

        if (!Enum.TryParse(invitation.Role, ignoreCase: false, out TenantRole role) ||
            !Enum.IsDefined(role))
        {
            return Result.Failure<TenantMemberView>(ResultError.Validation(
                "tenants.invalid_role",
                "The member role is not supported."));
        }

        var tenantId = new TenantId(invitation.TenantId);
        User? user = await users.FindByEmailAsync(normalized, cancellationToken);
        if (user is null)
        {
            Result<User> created = identities.CreateUser(
                normalized.Value,
                DeriveDisplayName(normalized.Value));
            if (!created.TryGetValue(out User? newUser))
            {
                return Result.Failure<TenantMemberView>(created.Error!);
            }

            user = newUser;
            await users.AddAsync(user, cancellationToken);
        }

        TenantMembership? existing =
            await memberships.FindAsync(tenantId, user.Id, cancellationToken);
        if (existing is not null)
        {
            return Result.Failure<TenantMemberView>(ResultError.Conflict(
                "tenants.member_exists",
                "The user already has a membership in this tenant."));
        }

        Result<TenantMembership> invited =
            tenants.InviteMember(tenantId, user.Id, role);
        if (!invited.TryGetValue(out TenantMembership? membership))
        {
            return Result.Failure<TenantMemberView>(invited.Error!);
        }

        try
        {
            await memberships.AddAsync(membership, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<TenantMemberView>(ResultError.Conflict(
                "tenants.member_exists",
                "The user already has a membership in this tenant."));
        }

        Result<AuditChangeSummary> after = AuditChangeSummary.Create(
        [
            AuditField.Plain("role", membership.Role.ToString()),
            AuditField.Plain("status", membership.Status.ToString()),
        ]);
        await audit.AppendAsync(
            new AuditRecord(
                new AuditEventId(ids.NewId()),
                new AuditTenantId(invitation.TenantId),
                new AuditActor(
                    AuditActorKind.User,
                    invitation.ActorUserId.ToString("D")),
                "tenant.member.invited",
                new AuditResource("tenant_membership", user.Id.Value.ToString("D")),
                AuditChangeSummary.Empty,
                after.TryGetValue(out AuditChangeSummary? summary)
                    ? summary
                    : AuditChangeSummary.Empty,
                AuditOutcome.Succeeded,
                clock.UtcNow),
            cancellationToken);

        return Result.Success(new TenantMemberView(
            user.Id.Value,
            user.Email.Value,
            user.DisplayName,
            membership.Role.ToString(),
            membership.Status.ToString(),
            membership.InvitedAt,
            membership.JoinedAt,
            membership.Version));
    }

    private static string DeriveDisplayName(string email)
    {
        int separator = email.IndexOf('@', StringComparison.Ordinal);
        string local = separator > 0 ? email[..separator] : email;
        return string.IsNullOrWhiteSpace(local) ? email : local;
    }
}
