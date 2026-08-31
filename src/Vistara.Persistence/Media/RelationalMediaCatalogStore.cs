using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Media;

public sealed record PersistedMediaObject(
    string StorageKey,
    long ContentLength,
    string ContentType,
    string Sha256,
    string? ProviderVersion,
    string? DownloadFileName);

public sealed record PersistedDerivativeMedia(
    Guid RequestId,
    Guid TenantId,
    bool IsPublic,
    string State,
    string StorageKey,
    long? ContentLength,
    string? ContentType,
    string? Sha256);

public sealed record PersistedPublicDerivativeRoute(
    Guid TenantId,
    Guid RequestId);

public sealed class RelationalMediaCatalogStore(
    VistaraDbContext context,
    MediaCatalogDbContext catalog,
    TenantDbContextFactory tenantContexts,
    ITenantScope tenantScope)
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly MediaCatalogDbContext _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly TenantDbContextFactory _tenantContexts =
        tenantContexts ?? throw new ArgumentNullException(nameof(tenantContexts));
    private readonly ITenantScope _tenantScope =
        tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));

    public async ValueTask RegisterPublicDerivativeAsync(
        Guid tenantId,
        Guid requestId,
        string pipelineId,
        string sourceSha256,
        string recipeSha256,
        string extension,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        string digest = RouteDigest(
            pipelineId,
            sourceSha256,
            recipeSha256,
            extension);
        PublicDerivativeRouteRow? existing = await _context
            .Set<PublicDerivativeRouteRow>()
            .SingleOrDefaultAsync(
                row => row.LookupDigest == digest,
                cancellationToken);
        if (existing is not null)
        {
            if (existing.RoutedTenantId == tenantId)
            {
                existing.RequestId = requestId;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        _context.Add(new PublicDerivativeRouteRow
        {
            LookupDigest = digest,
            RoutedTenantId = tenantId,
            RequestId = requestId,
            CreatedAtUtc = createdAtUtc,
        });
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
        }
    }

    public async ValueTask<PersistedPublicDerivativeRoute?>
        ResolvePublicDerivativeRouteAsync(
            string pipelineId,
            string sourceSha256,
            string recipeSha256,
            string extension,
            CancellationToken cancellationToken)
    {
        string digest = RouteDigest(
            pipelineId,
            sourceSha256,
            recipeSha256,
            extension);
        PublicDerivativeRouteRow? row = await _catalog.PublicDerivativeRoutes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.LookupDigest == digest,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new PersistedPublicDerivativeRoute(
            row.RoutedTenantId,
            row.RequestId);
    }

    public async ValueTask<PersistedDerivativeMedia?> GetDerivativeAsync(
        Guid tenantId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using VistaraDbContext context =
            _tenantContexts.Create(tenantId);
        DerivativeRequestRow? row = await context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == requestId,
                cancellationToken);
        return row is null ? null : ToMedia(row);
    }

    /// <summary>
    /// Resolves a rendition only while its asset is servable. A trashed or
    /// purged asset conceals every rendition URL the gallery advertised while
    /// the asset was ready.
    /// </summary>
    public async ValueTask<PersistedDerivativeMedia?> FindAssetRenditionAsync(
        Guid tenantId,
        Guid assetId,
        Guid renditionId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        TenantKey tenantKey = tenantId;
        DerivativeRequestRow? row = await (
            from derivative in _context.Set<DerivativeRequestRow>().AsNoTracking()
            join asset in _context.Assets.AsNoTracking()
                on derivative.AssetId equals asset.Id
            where derivative.Id == renditionId &&
                derivative.AssetId == assetId &&
                derivative.TenantId == tenantKey &&
                asset.TenantId == tenantKey &&
                asset.Status == "Ready"
            select derivative)
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToMedia(row);
    }

    public async ValueTask<PersistedDerivativeMedia?> FindDerivativeAsync(
        Guid tenantId,
        string pipelineId,
        string sourceSha256,
        string recipeSha256,
        string extension,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        DerivativeRequestRow? row = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(candidate =>
                    candidate.PipelineId == pipelineId &&
                    candidate.SourceSha256 == sourceSha256 &&
                    candidate.RecipeSha256 == recipeSha256 &&
                    candidate.Extension == extension)
            .OrderByDescending(candidate => candidate.State == "Ready")
            .ThenByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToMedia(row);
    }

    public async ValueTask<PersistedMediaObject?> GetOriginalAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await (
            from asset in _context.Assets.AsNoTracking()
            join revision in _context.AssetRevisions.AsNoTracking()
                on new { asset.TenantId, Id = asset.CurrentRevisionId }
                equals new
                {
                    revision.TenantId,
                    Id = (Guid?)revision.Id,
                }
            join blob in _context.Blobs.AsNoTracking()
                on new { revision.TenantId, Id = revision.BlobId }
                equals new { blob.TenantId, blob.Id }
            where asset.Id == assetId &&
                asset.Status == "Ready" &&
                blob.State == "Active"
            select new PersistedMediaObject(
                blob.ObjectKey,
                blob.SizeBytes,
                blob.ContentType,
                blob.Sha256,
                blob.ProviderVersion,
                asset.Title))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async ValueTask<bool> CanReadAssetAsync(
        Guid tenantId,
        Guid userId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        bool userActive = await _context.Users
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.Id == userId &&
                    row.Status == "Active",
                cancellationToken);
        bool member = await _context.TenantMemberships
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.UserId == userId &&
                    row.Status == "Active",
                cancellationToken);
        return userActive &&
            member &&
            await _context.Assets
                .AsNoTracking()
                .AnyAsync(
                    row =>
                        row.Id == assetId &&
                        row.Status != "Purged",
                    cancellationToken);
    }

    public async ValueTask<bool> RevalidateSubjectGrantAsync(
        Guid tenantId,
        Guid userId,
        Guid assetId,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        bool userActive = await _context.Users
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.Id == userId &&
                    row.Status == "Active",
                cancellationToken);
        bool member = await _context.TenantMemberships
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.UserId == userId &&
                    row.Status == "Active",
                cancellationToken);
        return userActive &&
            member &&
            await _context.AssetRevisions
                .AsNoTracking()
                .AnyAsync(
                    row =>
                        row.Id == revisionId &&
                        row.AssetId == assetId,
                    cancellationToken);
    }

    public async ValueTask<bool> RevalidateShareGrantAsync(
        Guid tenantId,
        Guid shareId,
        long shareVersion,
        Guid assetId,
        Guid revisionId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        bool shareActive = await _context.Shares
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.Id == shareId &&
                    row.Version == shareVersion &&
                    row.RevokedAtUtc == null &&
                    (row.ExpiresAtUtc == null || row.ExpiresAtUtc > nowUtc),
                cancellationToken);
        return shareActive &&
            await _context.ShareAssets
                .AsNoTracking()
                .AnyAsync(
                    row =>
                        row.ShareId == shareId &&
                        row.AssetId == assetId &&
                        row.RevisionId == revisionId,
                    cancellationToken);
    }

    private void EnsureTenant(Guid tenantId)
    {
        if (TenantScopeGuard.RequireTenantId(_tenantScope) != tenantId)
        {
            throw new InvalidOperationException(
                "Media catalog access cannot cross tenant scope.");
        }
    }

    private static PersistedDerivativeMedia ToMedia(
        DerivativeRequestRow row) =>
        new(
            row.Id,
            row.TenantId,
            row.IsPublic,
            row.State,
            row.RepresentationStorageKey ?? row.CacheKey,
            row.RepresentationContentLength,
            row.RepresentationContentType,
            row.RepresentationSha256);

    private static string RouteDigest(
        string pipelineId,
        string sourceSha256,
        string recipeSha256,
        string extension)
    {
        byte[] route = Encoding.UTF8.GetBytes(
            string.Join(
                ':',
                "vistara",
                "public-derivative",
                pipelineId,
                sourceSha256,
                recipeSha256,
                extension));
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(route));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(route);
        }
    }
}
