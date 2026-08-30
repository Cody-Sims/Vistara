using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Sharing;

namespace Vistara.Persistence.Sharing;

public sealed class RelationalShareStore(SharingDbContext context) : IShareStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SharingDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<ShareIdempotencyRecord?> FindIdempotencyAsync(
        string idempotencyKeyHash,
        CancellationToken cancellationToken)
    {
        ValidateDigest(idempotencyKeyHash, nameof(idempotencyKeyHash));
        SharingIdempotencyRow? row = await _context.Idempotency
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.KeyHash == idempotencyKeyHash,
                cancellationToken);
        return row is null
            ? null
            : new ShareIdempotencyRecord(row.ShareId, row.RequestHash);
    }

    public async ValueTask<ShareAddResult> AddAsync(
        ShareRecord share,
        string idempotencyKeyHash,
        string requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        ValidateDigest(idempotencyKeyHash, nameof(idempotencyKeyHash));
        ValidateDigest(requestHash, nameof(requestHash));
        SharingIdempotencyRow? existing = await _context.Idempotency
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.KeyHash == idempotencyKeyHash,
                cancellationToken);
        if (existing is not null)
        {
            ShareRecord? replayed = await FindByIdAsync(
                existing.ShareId,
                cancellationToken);
            return existing.RequestHash == requestHash && replayed is not null
                ? ShareAddResult.Replayed(replayed)
                : ShareAddResult.IdempotencyConflict();
        }

        _context.Shares.Add(ToRow(share));
        _context.Idempotency.Add(new SharingIdempotencyRow
        {
            KeyHash = idempotencyKeyHash,
            RequestHash = requestHash,
            ShareId = share.Id,
        });
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return ShareAddResult.Created(share);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            existing = await _context.Idempotency
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.KeyHash == idempotencyKeyHash,
                    cancellationToken);
            ShareRecord? replayed = existing is null
                ? null
                : await FindByIdAsync(existing.ShareId, cancellationToken);
            return existing?.RequestHash == requestHash && replayed is not null
                ? ShareAddResult.Replayed(replayed)
                : ShareAddResult.IdempotencyConflict();
        }
    }

    public async ValueTask<ShareRecord?> FindAsync(
        Guid tenantId,
        Guid shareId,
        CancellationToken cancellationToken)
    {
        SharingShareRow? row = await _context.Shares
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TenantId == tenantId &&
                    candidate.Id == shareId,
                cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async ValueTask<ShareRecord?> FindByIdAsync(
        Guid shareId,
        CancellationToken cancellationToken)
    {
        SharingShareRow? row = await _context.Shares
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == shareId,
                cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async ValueTask<ShareRecord?> FindByTokenDigestAsync(
        string pepperVersionId,
        string digestHex,
        CancellationToken cancellationToken)
    {
        ValidateDigest(digestHex, nameof(digestHex));
        SharingShareRow? row = await _context.Shares
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.PepperVersionId == pepperVersionId &&
                    candidate.TokenDigestHex == digestHex,
                cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async ValueTask<IReadOnlyList<ShareRecord>> ListAsync(
        Guid tenantId,
        int limit,
        string? status,
        DateTimeOffset nowUtc,
        DateTimeOffset? beforeCreatedAtUtc,
        Guid? beforeId,
        CancellationToken cancellationToken)
    {
        IQueryable<SharingShareRow> query = _context.Shares
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId);
        query = status?.ToLowerInvariant() switch
        {
            "active" => query.Where(row =>
                row.RevokedAtUtc == null &&
                (row.ExpiresAtUtc == null || row.ExpiresAtUtc > nowUtc)),
            "expired" => query.Where(row =>
                row.RevokedAtUtc == null &&
                row.ExpiresAtUtc != null &&
                row.ExpiresAtUtc <= nowUtc),
            "revoked" => query.Where(row => row.RevokedAtUtc != null),
            _ => query,
        };
        if (beforeCreatedAtUtc.HasValue && beforeId.HasValue)
        {
            query = query.Where(row =>
                row.CreatedAtUtc < beforeCreatedAtUtc.Value ||
                row.CreatedAtUtc == beforeCreatedAtUtc.Value &&
                row.Id.CompareTo(beforeId.Value) < 0);
        }

        SharingShareRow[] rows = await query
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToRecord).ToArray();
    }

    public async ValueTask<ShareUpdateResult> UpdateAsync(
        ShareRecord updated,
        long expectedVersion,
        string idempotencyKeyHash,
        string requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ValidateDigest(idempotencyKeyHash, nameof(idempotencyKeyHash));
        ValidateDigest(requestHash, nameof(requestHash));
        SharingIdempotencyRow? idempotent = await _context.Idempotency
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.KeyHash == idempotencyKeyHash,
                cancellationToken);
        if (idempotent is not null)
        {
            ShareRecord? replayed = await FindByIdAsync(
                idempotent.ShareId,
                cancellationToken);
            return idempotent.RequestHash == requestHash &&
                replayed is not null
                ? ShareUpdateResult.Replayed(replayed)
                : ShareUpdateResult.IdempotencyConflict();
        }

        SharingShareRow? row = await _context.Shares.SingleOrDefaultAsync(
            candidate =>
                candidate.TenantId == updated.TenantId &&
                candidate.Id == updated.Id,
            cancellationToken);
        if (row is null)
        {
            return ShareUpdateResult.NotFound();
        }

        if (row.Version != expectedVersion)
        {
            return ShareUpdateResult.VersionConflict();
        }

        Apply(row, updated);
        _context.Idempotency.Add(new SharingIdempotencyRow
        {
            KeyHash = idempotencyKeyHash,
            RequestHash = requestHash,
            ShareId = updated.Id,
        });
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return updated.Version == expectedVersion
                ? ShareUpdateResult.Unchanged(updated)
                : ShareUpdateResult.Updated(updated);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            idempotent = await _context.Idempotency
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.KeyHash == idempotencyKeyHash,
                    cancellationToken);
            ShareRecord? replayed = idempotent is null
                ? null
                : await FindByIdAsync(idempotent.ShareId, cancellationToken);
            if (idempotent?.RequestHash == requestHash && replayed is not null)
            {
                return ShareUpdateResult.Replayed(replayed);
            }

            SharingShareRow? current = await _context.Shares
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.TenantId == updated.TenantId &&
                        candidate.Id == updated.Id,
                    cancellationToken);
            return current is null
                ? ShareUpdateResult.NotFound()
                : idempotent is null
                    ? ShareUpdateResult.VersionConflict()
                    : ShareUpdateResult.IdempotencyConflict();
        }
    }

    public async ValueTask AddSessionAsync(
        ShareSessionRecord session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _context.Sessions
            .Where(row => row.ExpiresAtUtc <= session.CreatedAtUtc)
            .ExecuteDeleteAsync(cancellationToken);
        _context.Sessions.Add(new SharingSessionRow
        {
            Id = session.Id,
            TenantId = session.TenantId,
            ShareId = session.ShareId,
            ShareVersion = session.ShareVersion,
            PepperVersionId = session.PepperVersionId,
            DigestHex = session.DigestHex,
            CreatedAtUtc = session.CreatedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc,
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<ShareSessionRecord?> FindSessionAsync(
        string pepperVersionId,
        string digestHex,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ValidateDigest(digestHex, nameof(digestHex));
        await _context.Sessions
            .Where(row => row.ExpiresAtUtc <= nowUtc)
            .ExecuteDeleteAsync(cancellationToken);
        SharingSessionRow? row = await _context.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.PepperVersionId == pepperVersionId &&
                    candidate.DigestHex == digestHex,
                cancellationToken);
        return row is null
            ? null
            : new ShareSessionRecord(
                row.Id,
                row.TenantId,
                row.ShareId,
                row.ShareVersion,
                row.PepperVersionId,
                row.DigestHex,
                row.CreatedAtUtc,
                row.ExpiresAtUtc);
    }

    private static SharingShareRow ToRow(ShareRecord share)
    {
        var row = new SharingShareRow();
        Apply(row, share);
        return row;
    }

    private static void Apply(
        SharingShareRow row,
        ShareRecord share)
    {
        row.Id = share.Id;
        row.TenantId = share.TenantId;
        row.CreatedByActorId = share.CreatedByActorId;
        row.Name = share.Name;
        row.TargetType = share.TargetType.ToString();
        row.AlbumId = share.AlbumId;
        row.AssetsJson = JsonSerializer.Serialize(share.Assets, JsonOptions);
        row.Permissions = (int)share.Permissions;
        row.MetadataExposure = share.MetadataExposure.ToString();
        row.PepperVersionId = share.PepperVersionId;
        row.TokenDigestHex = share.TokenDigestHex;
        row.PasswordHash = share.PasswordHash;
        row.CreatedAtUtc = share.CreatedAtUtc;
        row.ExpiresAtUtc = share.ExpiresAtUtc;
        row.RevokedAtUtc = share.RevokedAtUtc;
        row.RevokedByActorId = share.RevokedByActorId;
        row.Version = share.Version;
        row.RequestHash = share.RequestHash;
    }

    private static ShareRecord ToRecord(SharingShareRow row)
    {
        ShareAssetSnapshot[] assets =
            JsonSerializer.Deserialize<ShareAssetSnapshot[]>(
                row.AssetsJson,
                JsonOptions) ??
            throw new InvalidOperationException(
                "The persisted share snapshot is invalid.");
        return new ShareRecord(
            row.Id,
            row.TenantId,
            row.CreatedByActorId,
            row.Name,
            Enum.Parse<ShareTargetType>(row.TargetType),
            row.AlbumId,
            assets,
            (ShareAccess)row.Permissions,
            Enum.Parse<ShareMetadataExposure>(row.MetadataExposure),
            row.PepperVersionId,
            row.TokenDigestHex,
            row.PasswordHash,
            row.CreatedAtUtc,
            row.ExpiresAtUtc,
            row.RevokedAtUtc,
            row.RevokedByActorId,
            row.Version,
            row.RequestHash);
    }

    private static void ValidateDigest(string value, string parameterName)
    {
        if (value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Sharing persistence keys must be SHA-256 hex digests.",
                parameterName);
        }
    }
}
