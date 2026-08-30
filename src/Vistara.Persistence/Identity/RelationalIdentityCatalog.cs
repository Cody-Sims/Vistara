using Microsoft.EntityFrameworkCore;
using Vistara.Domain.Identity;
using Vistara.Persistence.Model;
using Vistara.Persistence.Repositories;

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
    private readonly IdentityCatalogDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<bool> HasAnyUserAsync(CancellationToken cancellationToken) =>
        await _context.Users.AsNoTracking().AnyAsync(cancellationToken);

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
