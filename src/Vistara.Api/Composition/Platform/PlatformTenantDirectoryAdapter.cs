using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Api.Features.Tenants;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Tenancy;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Bridges tenant and member administration onto the existing tenancy
/// repositories, factories, directory reads, and audit writer.
/// </summary>
internal sealed class PlatformTenantDirectoryAdapter(
    VistaraDbContext context,
    RelationalTenantDirectory directory,
    RelationalIdentityCatalog catalog,
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
        Guid? restrictToTenantId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PersistedTenantMembership> persisted =
            await catalog.ListMembershipsAsync(userId, cancellationToken);
        return persisted
            .Where(membership =>
                restrictToTenantId is not { } tenantId ||
                membership.TenantId == tenantId)
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

    public async ValueTask<Result<TenantMemberView>> UpdateMemberAsync(
        TenantMemberUpdate update,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        if (update.Role is null && update.Status is null)
        {
            return Result.Failure<TenantMemberView>(ResultError.Validation(
                "tenants.empty_member_patch",
                "A membership change must set a role, a status, or both."));
        }

        if (!TryReadRole(update.Role, out TenantRole? role))
        {
            return Result.Failure<TenantMemberView>(ResultError.Validation(
                "tenants.invalid_role",
                "The member role is not supported."));
        }

        if (!TryReadStatus(update.Status, out MembershipStatus? status))
        {
            return Result.Failure<TenantMemberView>(ResultError.Validation(
                "tenants.invalid_status",
                "The member status must be Active, Suspended, or Removed."));
        }

        bool actorIsOwner = string.Equals(
            update.ActorRole,
            OwnerRoleName,
            StringComparison.Ordinal);
        var tenantId = new TenantId(update.TenantId);
        TenantKey tenantKey = update.TenantId;

        IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                await EstablishRowSecurityAsync(update.TenantId, cancellationToken);
                TenantMembership? membership = await memberships.FindAsync(
                    tenantId,
                    new UserId(update.MemberUserId),
                    cancellationToken);
                if (membership is null)
                {
                    return Result.Failure<TenantMemberView>(ResultError.NotFound(
                        "tenants.member_not_found",
                        "The requested member was not found."));
                }

                // The hierarchy check reads the target's current role: an admin
                // must never demote, suspend, or remove an owner, and must
                // never create one.
                if (!actorIsOwner &&
                    (membership.Role == TenantRole.TenantOwner ||
                        role == TenantRole.TenantOwner))
                {
                    return Result.Failure<TenantMemberView>(ResultError.Forbidden(
                        "tenants.owner_requires_owner",
                        "Only a tenant owner may change an owner or grant that role."));
                }

                if (membership.Version != expectedVersion)
                {
                    return Result.Failure<TenantMemberView>(StaleMembership);
                }

                DateTimeOffset now = clock.UtcNow;
                if (role is { } newRole && newRole != membership.Role)
                {
                    Result changed = membership.ChangeRole(newRole, now);
                    if (changed.IsFailure)
                    {
                        return Result.Failure<TenantMemberView>(changed.Error!);
                    }
                }

                if (status is { } newStatus && newStatus != membership.Status)
                {
                    Result transitioned = newStatus switch
                    {
                        MembershipStatus.Active => membership.Activate(now),
                        MembershipStatus.Suspended => membership.Suspend(now),
                        MembershipStatus.Removed => membership.Remove(now),
                        _ => Result.Failure(ResultError.Validation(
                            "tenants.invalid_status",
                            "The member status transition is not supported.")),
                    };
                    if (transitioned.IsFailure)
                    {
                        return Result.Failure<TenantMemberView>(transitioned.Error!);
                    }
                }

                await memberships.UpdateAsync(
                    membership,
                    expectedVersion,
                    cancellationToken);

                // The invariant is evaluated after the write and inside the
                // transaction, so two concurrent demotions of different owner
                // rows cannot both observe a surviving owner.
                int activeOwners = await context.TenantMemberships
                    .AsNoTracking()
                    .CountAsync(
                        row =>
                            row.TenantId == tenantKey &&
                            row.Role == OwnerRoleName &&
                            row.Status == ActiveStatusName,
                        cancellationToken);
                if (activeOwners == 0)
                {
                    await SafeRollbackAsync(transaction);
                    context.ChangeTracker.Clear();
                    return Result.Failure<TenantMemberView>(ResultError.Conflict(
                        "tenants.last_owner",
                        "A tenant must keep at least one active owner."));
                }

                await WriteMemberAuditAsync(
                    update.TenantId,
                    update.ActorUserId,
                    update.MemberUserId,
                    "tenant.member.updated",
                    membership,
                    now,
                    cancellationToken);
                TenantMemberView view = await DescribeAsync(
                    update.TenantId,
                    update.MemberUserId,
                    membership,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Result.Success(view);
            }
            catch (Exception failure) when (
                failure is DbUpdateException or InvalidOperationException or DbException)
            {
                await SafeRollbackAsync(transaction);
                context.ChangeTracker.Clear();
                return Result.Failure<TenantMemberView>(
                    RelationalFirstOwnerProvisioningStore.IsContentionOrConstraint(failure)
                        ? ResultError.Conflict(
                            "tenants.member_contended",
                            "A concurrent membership change interrupted this one; retry the request.")
                        : StaleMembership);
            }
        }
    }

    private const string OwnerRoleName = nameof(TenantRole.TenantOwner);

    private const string ActiveStatusName = nameof(MembershipStatus.Active);

    private static bool TryReadRole(string? value, out TenantRole? role)
    {
        role = null;
        if (value is null)
        {
            return true;
        }

        if (!Enum.TryParse(value, ignoreCase: false, out TenantRole parsed) ||
            !Enum.IsDefined(parsed))
        {
            return false;
        }

        role = parsed;
        return true;
    }

    private static bool TryReadStatus(string? value, out MembershipStatus? status)
    {
        status = null;
        if (value is null)
        {
            return true;
        }

        if (!Enum.TryParse(value, ignoreCase: false, out MembershipStatus parsed) ||
            !Enum.IsDefined(parsed) ||
            parsed == MembershipStatus.Invited)
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private async ValueTask EstablishRowSecurityAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('vistara.tenant_id', {tenantId.ToString("D")}, true);",
            cancellationToken);
    }

    private static async Task SafeRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The transaction was already completed by the provider.
        }
        catch (DbException)
        {
            // The connection is unusable; disposal releases it.
        }
    }

    private async ValueTask<TenantMemberView> DescribeAsync(
        Guid tenantId,
        Guid memberUserId,
        TenantMembership membership,
        CancellationToken cancellationToken)
    {
        PersistedTenantMember? refreshed = (await directory.ListMembersAsync(
                tenantId,
                cancellationToken))
            .SingleOrDefault(member => member.UserId == memberUserId);
        return refreshed is null
            ? new TenantMemberView(
                memberUserId,
                string.Empty,
                string.Empty,
                membership.Role.ToString(),
                membership.Status.ToString(),
                membership.InvitedAt,
                membership.JoinedAt,
                membership.Version)
            : new TenantMemberView(
                refreshed.UserId,
                refreshed.Email,
                refreshed.DisplayName,
                refreshed.Role,
                refreshed.MembershipStatus,
                refreshed.InvitedAtUtc,
                refreshed.JoinedAtUtc,
                refreshed.Version);
    }

    private async ValueTask WriteMemberAuditAsync(
        Guid tenantId,
        Guid actorUserId,
        Guid memberUserId,
        string action,
        TenantMembership membership,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        Result<AuditChangeSummary> after = AuditChangeSummary.Create(
        [
            AuditField.Plain("role", membership.Role.ToString()),
            AuditField.Plain("status", membership.Status.ToString()),
        ]);
        await audit.AppendAsync(
            new AuditRecord(
                new AuditEventId(ids.NewId()),
                new AuditTenantId(tenantId),
                new AuditActor(AuditActorKind.User, actorUserId.ToString("D")),
                action,
                new AuditResource("tenant_membership", memberUserId.ToString("D")),
                AuditChangeSummary.Empty,
                after.TryGetValue(out AuditChangeSummary? summary)
                    ? summary
                    : AuditChangeSummary.Empty,
                AuditOutcome.Succeeded,
                occurredAt),
            cancellationToken);
    }

    private static ResultError StaleMembership => ResultError.Conflict(
        "tenants.member_version_conflict",
        "The membership changed since it was read.");

    private static string DeriveDisplayName(string email)
    {
        int separator = email.IndexOf('@', StringComparison.Ordinal);
        string local = separator > 0 ? email[..separator] : email;
        return string.IsNullOrWhiteSpace(local) ? email : local;
    }
}
