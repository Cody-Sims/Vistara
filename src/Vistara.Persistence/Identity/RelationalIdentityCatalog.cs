using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Domain.Identity;
using Vistara.Persistence.Model;
using Vistara.Persistence.Repositories;
using Vistara.Persistence.Tenancy;

namespace Vistara.Persistence.Identity;

public sealed record PersistedLocalCredential(
    Guid UserId,
    Guid LocalIdentityId,
    string PasswordHash,
    string UserStatus);

public sealed record PersistedIdentitySummary(
    Guid UserId,
    string Email,
    string DisplayName,
    string Status);

/// <summary>
/// Tenant-independent identity reads and credential writes used by browser
/// sign-in and first-owner provisioning.
/// </summary>
public sealed class RelationalIdentityCatalog(IdentityCatalogDbContext context)
{
    private const string PostgreSqlProviderName =
        "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly IdentityCatalogDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<bool> HasAnyUserAsync(CancellationToken cancellationToken) =>
        await _context.Users.AsNoTracking().AnyAsync(cancellationToken);

    /// <summary>
    /// Resolves every tenant membership of one principal with a single indexed
    /// query. There is no per-tenant probing and no global tenant enumeration,
    /// so the cost is proportional to the principal's own memberships.
    /// PostgreSQL grants the read through the transaction-local
    /// identity_directory policy; every other access stays tenant isolated.
    /// </summary>
    public async ValueTask<IReadOnlyList<PersistedTenantMembership>> ListMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDbContextTransaction transaction =
            await _context.Database.BeginTransactionAsync(cancellationToken);
        await using (transaction.ConfigureAwait(false))
        {
            if (_context.Database.ProviderName == PostgreSqlProviderName)
            {
                _ = await _context.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('vistara.identity_directory', 'on', true);",
                    cancellationToken);
            }

            PersistedTenantMembership[] memberships = await (
                from membership in _context.TenantMemberships.AsNoTracking()
                join tenant in _context.Tenants.AsNoTracking()
                    on membership.TenantId equals tenant.Id
                where membership.UserId == userId
                orderby tenant.Slug
                select new PersistedTenantMembership(
                    tenant.Id.Value,
                    tenant.Slug,
                    tenant.Name,
                    tenant.Status,
                    membership.Role,
                    membership.Status,
                    membership.JoinedAtUtc,
                    membership.Version))
                .ToArrayAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return memberships;
        }
    }

    public async ValueTask<PersistedLocalCredential?> FindCredentialAsync(
        string normalizedLogin,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedLogin);
        return await (
            from identity in _context.LocalIdentities.AsNoTracking()
            join credential in _context.LocalCredentials.AsNoTracking()
                on identity.Id equals credential.LocalIdentityId
            join user in _context.Users.AsNoTracking()
                on identity.UserId equals user.Id
            where identity.NormalizedLogin == normalizedLogin
            select new PersistedLocalCredential(
                user.Id,
                identity.Id,
                credential.PasswordHash,
                user.Status))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async ValueTask<User?> FindUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        UserRow? row = await _context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        LocalIdentityRow[] localIdentities = await _context.LocalIdentities
            .AsNoTracking()
            .Where(identity => identity.UserId == userId)
            .ToArrayAsync(cancellationToken);
        return DomainMapper.ToDomain(row, localIdentities, []);
    }

    public async ValueTask<PersistedIdentitySummary?> FindSummaryAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new PersistedIdentitySummary(
                user.Id,
                user.NormalizedEmail,
                user.DisplayName,
                user.Status))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Stores the password verifier for a local identity. The plaintext
    /// password never reaches persistence.
    /// </summary>
    public async ValueTask SetPasswordAsync(
        Guid localIdentityId,
        Guid userId,
        string passwordHash,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        LocalCredentialRow? existing = await _context.LocalCredentials
            .SingleOrDefaultAsync(
                row => row.LocalIdentityId == localIdentityId,
                cancellationToken);
        if (existing is null)
        {
            _context.LocalCredentials.Add(new LocalCredentialRow
            {
                LocalIdentityId = localIdentityId,
                UserId = userId,
                PasswordHash = passwordHash,
                UpdatedAtUtc = updatedAtUtc,
                Version = 1,
            });
        }
        else
        {
            existing.PasswordHash = passwordHash;
            existing.UpdatedAtUtc = updatedAtUtc;
            existing.Version++;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
