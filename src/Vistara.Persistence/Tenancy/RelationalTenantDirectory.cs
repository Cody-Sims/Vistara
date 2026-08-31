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
/// Reads the member roster of the tenant that owns the current scope.
/// Cross-tenant membership lookups belong to
/// <c>RelationalIdentityCatalog.ListMembershipsAsync</c>, which resolves them
/// with a single indexed query instead of probing tenants.
/// </summary>
public sealed class RelationalTenantDirectory(VistaraDbContext context)
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

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
