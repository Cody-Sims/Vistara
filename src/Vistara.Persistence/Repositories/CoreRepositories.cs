using Microsoft.EntityFrameworkCore;
using Vistara.Application.Assets;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Application.Uploads;
using Vistara.Domain.Assets;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Domain.Uploads;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Repositories;

public sealed class TenantRepository(VistaraDbContext context) : ITenantRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<Tenant?> FindByIdAsync(
        TenantId id,
        CancellationToken cancellationToken)
    {
        TenantKey key = id.Value;
        TenantRow? row = await _context.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == key, cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }

    public async ValueTask<Tenant?> FindBySlugAsync(
        TenantSlug slug,
        CancellationToken cancellationToken)
    {
        TenantRow? row = await _context.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Slug == slug.Value, cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }

    public async ValueTask AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _context.Tenants.Add(DomainMapper.ToRow(tenant));
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask UpdateAsync(
        Tenant tenant,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenant.Id.Value;
        TenantRow current = await _context.Tenants
            .SingleOrDefaultAsync(row => row.Id == key, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The tenant no longer exists.");
        EnsureVersion(current.Version, expectedVersion, "tenant");
        Copy(DomainMapper.ToRow(tenant), current);
        SetOriginalVersion(current, expectedVersion);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private void SetOriginalVersion(TenantRow row, long version) =>
        _context.Entry(row).Property(item => item.Version).OriginalValue = version;

    private static void Copy(TenantRow source, TenantRow target)
    {
        target.Name = source.Name;
        target.Status = source.Status;
        target.UpdatedAtUtc = source.UpdatedAtUtc;
        target.Version = source.Version;
    }

    internal static void EnsureVersion(long actual, long expected, string aggregate)
    {
        if (actual != expected)
        {
            throw new DbUpdateConcurrencyException(
                $"The persisted {aggregate} version {actual} does not match expected version {expected}.");
        }
    }
}

public sealed class UserRepository(VistaraDbContext context) : IUserRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public ValueTask<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken) =>
        FindAsync(row => row.Id == id.Value, cancellationToken);

    public ValueTask<User?> FindByEmailAsync(
        NormalizedEmail email,
        CancellationToken cancellationToken) =>
        FindAsync(row => row.NormalizedEmail == email.Value, cancellationToken);

    public async ValueTask<User?> FindByLocalIdentityAsync(
        NormalizedLogin login,
        CancellationToken cancellationToken)
    {
        Guid? userId = await _context.LocalIdentities
            .AsNoTracking()
            .Where(row => row.NormalizedLogin == login.Value)
            .Select(row => (Guid?)row.UserId)
            .SingleOrDefaultAsync(cancellationToken);
        return userId.HasValue
            ? await FindByIdAsync(new UserId(userId.Value), cancellationToken)
            : null;
    }

    public async ValueTask<User?> FindByExternalIdentityAsync(
        ExternalIssuer issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        Guid? userId = await _context.ExternalIdentities
            .AsNoTracking()
            .Where(row => row.Issuer == issuer.Value && row.Subject == subject)
            .Select(row => (Guid?)row.UserId)
            .SingleOrDefaultAsync(cancellationToken);
        return userId.HasValue
            ? await FindByIdAsync(new UserId(userId.Value), cancellationToken)
            : null;
    }

    public async ValueTask AddAsync(User user, CancellationToken cancellationToken)
    {
        _context.Users.Add(DomainMapper.ToRow(user));
        _context.LocalIdentities.AddRange(DomainMapper.ToLocalIdentityRows(user));
        _context.ExternalIdentities.AddRange(DomainMapper.ToExternalIdentityRows(user));
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask UpdateAsync(
        User user,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        UserRow current = await _context.Users
            .SingleOrDefaultAsync(row => row.Id == user.Id.Value, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The user no longer exists.");
        TenantRepository.EnsureVersion(current.Version, expectedVersion, "user");
        UserRow source = DomainMapper.ToRow(user);
        current.NormalizedEmail = source.NormalizedEmail;
        current.DisplayName = source.DisplayName;
        current.Status = source.Status;
        current.UpdatedAtUtc = source.UpdatedAtUtc;
        current.Version = source.Version;
        _context.Entry(current).Property(row => row.Version).OriginalValue = expectedVersion;

        LocalIdentityRow[] existingLocal = await _context.LocalIdentities
            .Where(row => row.UserId == user.Id.Value)
            .ToArrayAsync(cancellationToken);
        ExternalIdentityRow[] existingExternal = await _context.ExternalIdentities
            .Where(row => row.UserId == user.Id.Value)
            .ToArrayAsync(cancellationToken);
        _context.LocalIdentities.RemoveRange(existingLocal);
        _context.ExternalIdentities.RemoveRange(existingExternal);
        _context.LocalIdentities.AddRange(DomainMapper.ToLocalIdentityRows(user));
        _context.ExternalIdentities.AddRange(DomainMapper.ToExternalIdentityRows(user));
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async ValueTask<User?> FindAsync(
        System.Linq.Expressions.Expression<Func<UserRow, bool>> predicate,
        CancellationToken cancellationToken)
    {
        UserRow? row = await _context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(predicate, cancellationToken);
        if (row is null)
        {
            return null;
        }

        LocalIdentityRow[] local = await _context.LocalIdentities
            .AsNoTracking()
            .Where(identity => identity.UserId == row.Id)
            .ToArrayAsync(cancellationToken);
        ExternalIdentityRow[] external = await _context.ExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.UserId == row.Id)
            .ToArrayAsync(cancellationToken);
        return DomainMapper.ToDomain(row, local, external);
    }
}

public sealed class TenantMembershipRepository(VistaraDbContext context)
    : ITenantMembershipRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<TenantMembership?> FindAsync(
        TenantId tenantId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId.Value;
        TenantMembershipRow? row = await _context.TenantMemberships
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TenantId == key &&
                    candidate.UserId == userId.Value,
                cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }

    public async ValueTask<IReadOnlyList<TenantMembership>> ListForUserAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        TenantMembershipRow[] rows = await _context.TenantMemberships
            .AsNoTracking()
            .Where(row => row.UserId == userId.Value)
            .OrderBy(row => row.TenantId)
            .ToArrayAsync(cancellationToken);
        return rows.Select(DomainMapper.ToDomain).ToArray();
    }

    public async ValueTask AddAsync(
        TenantMembership membership,
        CancellationToken cancellationToken)
    {
        _context.TenantMemberships.Add(DomainMapper.ToRow(membership));
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask UpdateAsync(
        TenantMembership membership,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        TenantKey key = membership.TenantId.Value;
        TenantMembershipRow current = await _context.TenantMemberships
            .SingleOrDefaultAsync(
                row =>
                    row.TenantId == key &&
                    row.UserId == membership.UserId.Value,
                cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The tenant membership no longer exists.");
        TenantRepository.EnsureVersion(current.Version, expectedVersion, "tenant membership");
        TenantMembershipRow source = DomainMapper.ToRow(membership);
        current.Role = source.Role;
        current.Status = source.Status;
        current.JoinedAtUtc = source.JoinedAtUtc;
        current.UpdatedAtUtc = source.UpdatedAtUtc;
        current.Version = source.Version;
        _context.Entry(current).Property(row => row.Version).OriginalValue = expectedVersion;
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public sealed class AuthSessionRepository(VistaraDbContext context) : IAuthSessionRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<AuthSession?> FindByDigestAsync(
        SessionDigest digest,
        CancellationToken cancellationToken)
    {
        AuthSessionRow? row = await _context.AuthSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Digest == digest.Value, cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }

    public async ValueTask AddAsync(AuthSession session, CancellationToken cancellationToken)
    {
        _context.AuthSessions.Add(DomainMapper.ToRow(session));
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask UpdateAsync(
        AuthSession session,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        AuthSessionRow current = await _context.AuthSessions
            .SingleOrDefaultAsync(row => row.Id == session.Id.Value, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The auth session no longer exists.");
        TenantRepository.EnsureVersion(current.Version, expectedVersion, "auth session");
        AuthSessionRow source = DomainMapper.ToRow(session);
        current.RevokedAtUtc = source.RevokedAtUtc;
        current.UpdatedAtUtc = source.UpdatedAtUtc;
        current.Version = source.Version;
        _context.Entry(current).Property(row => row.Version).OriginalValue = expectedVersion;
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ApiKeyRepository(VistaraDbContext context) : IApiKeyRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public ValueTask<ApiKeyMetadata?> FindByIdAsync(
        TenantId tenantId,
        ApiKeyId id,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId.Value;
        return FindAsync(
            row => row.TenantId == key && row.Id == id.Value,
            cancellationToken);
    }

    public ValueTask<ApiKeyMetadata?> FindByPrefixAsync(
        ApiKeyPrefix prefix,
        CancellationToken cancellationToken) =>
        FindAsync(row => row.Prefix == prefix.Value, cancellationToken);

    public async ValueTask<IReadOnlyList<ApiKeyMetadata>> ListForTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId.Value;
        ApiKeyRow[] rows = await _context.ApiKeys
            .AsNoTracking()
            .Where(row => row.TenantId == key)
            .OrderBy(row => row.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);
        return rows.Select(DomainMapper.ToDomain).ToArray();
    }

    public async ValueTask AddAsync(ApiKeyMetadata apiKey, CancellationToken cancellationToken)
    {
        _context.ApiKeys.Add(DomainMapper.ToRow(apiKey));
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask UpdateAsync(
        ApiKeyMetadata apiKey,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ApiKeyRow current = await _context.ApiKeys
            .SingleOrDefaultAsync(row => row.Id == apiKey.Id.Value, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The API key no longer exists.");
        TenantRepository.EnsureVersion(current.Version, expectedVersion, "API key");
        ApiKeyRow source = DomainMapper.ToRow(apiKey);
        current.RevokedAtUtc = source.RevokedAtUtc;
        current.LastUsedAtUtc = source.LastUsedAtUtc;
        current.UpdatedAtUtc = source.UpdatedAtUtc;
        current.Version = source.Version;
        _context.Entry(current).Property(row => row.Version).OriginalValue = expectedVersion;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async ValueTask<ApiKeyMetadata?> FindAsync(
        System.Linq.Expressions.Expression<Func<ApiKeyRow, bool>> predicate,
        CancellationToken cancellationToken)
    {
        ApiKeyRow? row = await _context.ApiKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(predicate, cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }
}

public sealed class BlobMetadataRepository(VistaraDbContext context) : IBlobMetadataRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<BlobObjectMetadata?> GetAsync(
        Guid tenantId,
        Guid blobId,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId;
        BlobRow? row = await _context.Blobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.TenantId == key && candidate.Id == blobId,
                cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }

    public async ValueTask<BlobObjectMetadata?> FindExactAsync(
        TenantBlobDedupeIdentity identity,
        CancellationToken cancellationToken)
    {
        TenantKey key = identity.TenantId;
        BlobRow? row = await _context.Blobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TenantId == key &&
                    candidate.Sha256 == identity.Sha256.Value &&
                    candidate.SizeBytes == identity.SizeBytes,
                cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }

    public async ValueTask AddAsync(
        BlobObjectMetadata blob,
        CancellationToken cancellationToken)
    {
        _context.Blobs.Add(DomainMapper.ToRow(blob));
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public sealed class AssetRepository(VistaraDbContext context) : IAssetRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<Asset?> GetAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId;
        AssetRow? row = await _context.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.TenantId == key && candidate.Id == assetId,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        AssetRevisionRow[] revisions = await _context.AssetRevisions
            .AsNoTracking()
            .Where(revision => revision.TenantId == key && revision.AssetId == assetId)
            .OrderBy(revision => revision.RevisionNumber)
            .ToArrayAsync(cancellationToken);
        Guid[] blobIds = revisions.Select(revision => revision.BlobId).Distinct().ToArray();
        Dictionary<Guid, BlobRow> blobs = await _context.Blobs
            .AsNoTracking()
            .Where(blob => blobIds.Contains(blob.Id))
            .ToDictionaryAsync(blob => blob.Id, cancellationToken);
        return DomainMapper.ToDomain(
            row,
            revisions.Select(revision => (revision, blobs[revision.BlobId])));
    }

    public async ValueTask AddAsync(Asset asset, CancellationToken cancellationToken)
    {
        AssetRow row = DomainMapper.ToRow(asset);
        row.CurrentRevisionId = null;
        _context.Assets.Add(row);
        _context.AssetRevisions.AddRange(asset.Revisions.Select(DomainMapper.ToRow));
        await _context.SaveChangesAsync(cancellationToken);
        if (asset.CurrentRevision is not null)
        {
            row.CurrentRevisionId = asset.CurrentRevision.Id;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async ValueTask SaveAsync(
        Asset asset,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        AssetRow current = await _context.Assets
            .SingleOrDefaultAsync(row => row.Id == asset.Id, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The asset no longer exists.");
        TenantRepository.EnsureVersion(current.Version, expectedVersion, "asset");
        AssetRow source = DomainMapper.ToRow(asset);
        current.Title = source.Title;
        current.Description = source.Description;
        current.Status = source.Status;
        current.Visibility = source.Visibility;
        current.CurrentRevisionId = source.CurrentRevisionId;
        current.UpdatedAtUtc = source.UpdatedAtUtc;
        current.Version = source.Version;
        _context.Entry(current).Property(row => row.Version).OriginalValue = expectedVersion;

        Guid[] persistedRevisionIds = await _context.AssetRevisions
            .Where(row => row.AssetId == asset.Id)
            .Select(row => row.Id)
            .ToArrayAsync(cancellationToken);
        _context.AssetRevisions.AddRange(
            asset.Revisions
                .Where(revision => !persistedRevisionIds.Contains(revision.Id))
                .Select(DomainMapper.ToRow));
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public sealed class UploadSessionRepository(VistaraDbContext context)
    : IUploadSessionRepository
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<UploadSession?> GetAsync(
        Guid tenantId,
        Guid uploadSessionId,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId;
        UploadSessionRow? row = await _context.UploadSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TenantId == key &&
                    candidate.Id == uploadSessionId,
                cancellationToken);
        return row is null ? null : await HydrateAsync(row, cancellationToken);
    }

    public async ValueTask<UploadSession?> FindByIdempotencyAsync(
        Guid tenantId,
        Guid actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId;
        Guid? uploadId = await _context.IdempotencyRequests
            .AsNoTracking()
            .Where(row =>
                row.TenantId == key &&
                row.PrincipalId == actorId &&
                row.Key == idempotencyKey)
            .Select(row => row.UploadSessionId)
            .SingleOrDefaultAsync(cancellationToken);
        return uploadId.HasValue
            ? await GetAsync(tenantId, uploadId.Value, cancellationToken)
            : null;
    }

    public async ValueTask AddAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        _context.UploadSessions.Add(DomainMapper.ToRow(session));
        _context.UploadParts.AddRange(DomainMapper.ToPartRows(session));
        _context.QuotaReservations.Add(DomainMapper.ToReservationRow(
            session.Id,
            session.TenantId,
            session.Reservation));
        _context.IdempotencyRequests.Add(DomainMapper.ToIdempotencyRow(session));
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask SaveAsync(
        UploadSession session,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        UploadSessionRow current = await _context.UploadSessions
            .SingleOrDefaultAsync(row => row.Id == session.Id, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The upload session no longer exists.");
        TenantRepository.EnsureVersion(current.Version, expectedVersion, "upload session");
        UploadSessionRow source = DomainMapper.ToRow(session);
        current.ProviderUploadId = source.ProviderUploadId;
        current.State = source.State;
        current.LastKnownState = source.LastKnownState;
        current.UpdatedAtUtc = source.UpdatedAtUtc;
        current.Version = source.Version;
        _context.Entry(current).Property(row => row.Version).OriginalValue = expectedVersion;

        QuotaReservationRow reservation = await _context.QuotaReservations
            .SingleAsync(row => row.UploadSessionId == session.Id, cancellationToken);
        reservation.State = session.Reservation.State.ToString();

        UploadPartRow[] existingParts = await _context.UploadParts
            .Where(row => row.UploadSessionId == session.Id)
            .ToArrayAsync(cancellationToken);
        HashSet<int> existingNumbers = existingParts
            .Select(row => row.PartNumber)
            .ToHashSet();
        _context.UploadParts.AddRange(
            DomainMapper.ToPartRows(session)
                .Where(row => !existingNumbers.Contains(row.PartNumber)));
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async ValueTask<UploadSession> HydrateAsync(
        UploadSessionRow row,
        CancellationToken cancellationToken)
    {
        QuotaReservationRow reservation = await _context.QuotaReservations
            .AsNoTracking()
            .SingleAsync(
                candidate => candidate.UploadSessionId == row.Id,
                cancellationToken);
        IdempotencyRequestRow idempotency = await _context.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(
                candidate => candidate.UploadSessionId == row.Id,
                cancellationToken);
        UploadPartRow[] parts = await _context.UploadParts
            .AsNoTracking()
            .Where(candidate => candidate.UploadSessionId == row.Id)
            .ToArrayAsync(cancellationToken);
        return DomainMapper.ToDomain(row, reservation, idempotency, parts);
    }
}
