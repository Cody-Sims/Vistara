using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Tenancy;

public sealed record PersistedTenantMembership(
    Guid TenantId,
    string Slug,
    string Name,
    string TenantStatus,
    string Role,
    string MembershipStatus,
    DateTimeOffset? JoinedAtUtc,
    long Version);

public sealed record PersistedTenantMember(
    Guid UserId,
    string Email,
    string DisplayName,
    string UserStatus,
    string Role,
    string MembershipStatus,
    DateTimeOffset InvitedAtUtc,
    DateTimeOffset? JoinedAtUtc,
    long Version);

/// <summary>
/// Reads tenant membership directories. Cross-tenant reads open one explicitly
/// scoped context per tenant so PostgreSQL row-level security and the SQLite
/// tenant filters both remain in force.
/// </summary>
public sealed class RelationalTenantDirectory(
    VistaraDbContext context,
    TenantDbContextFactory tenantContexts)
{
    /// <summary>Caps directory enumeration for a single request.</summary>
    public const int MaximumTenants = 256;

    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly TenantDbContextFactory _tenantContexts =
        tenantContexts ?? throw new ArgumentNullException(nameof(tenantContexts));

    public async ValueTask<IReadOnlyList<PersistedTenantMembership>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid[] tenantIds = await _context.WorkerTenantCatalog
            .AsNoTracking()
            .OrderBy(row => row.RoutedTenantId)
            .Take(MaximumTenants)
            .Select(row => row.RoutedTenantId.Value)
            .ToArrayAsync(cancellationToken);

        var memberships = new List<PersistedTenantMembership>(tenantIds.Length);
        foreach (Guid tenantId in tenantIds)
        {
            await using VistaraDbContext scoped = _tenantContexts.Create(tenantId);
            TenantKey key = tenantId;
            PersistedTenantMembership? membership = await (
                from tenant in scoped.Tenants.AsNoTracking()
                join member in scoped.TenantMemberships.AsNoTracking()
                    on tenant.Id equals member.TenantId
                where tenant.Id == key && member.UserId == userId
                select new PersistedTenantMembership(
                    tenant.Id.Value,
                    tenant.Slug,
                    tenant.Name,
                    tenant.Status,
                    member.Role,
                    member.Status,
                    member.JoinedAtUtc,
                    member.Version))
                .SingleOrDefaultAsync(cancellationToken);
            if (membership is not null)
            {
                memberships.Add(membership);
            }
        }

        return memberships
            .OrderBy(membership => membership.Slug, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<PersistedTenantMember>> ListMembersAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                "The member directory read does not match the active tenant scope.");
        }

        TenantKey key = tenantId;
        return await (
            from member in _context.TenantMemberships.AsNoTracking()
            join user in _context.Users.AsNoTracking()
                on member.UserId equals user.Id
            where member.TenantId == key
            orderby user.NormalizedEmail
            select new PersistedTenantMember(
                user.Id,
                user.NormalizedEmail,
                user.DisplayName,
                user.Status,
                member.Role,
                member.Status,
                member.InvitedAtUtc,
                member.JoinedAtUtc,
                member.Version))
            .ToArrayAsync(cancellationToken);
    }
}
