using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vistara.Api.Composition.Platform;
using Vistara.Application.Common;
using Vistara.Application.Gallery.Queries;
using Vistara.Application.Sharing;
using Vistara.Persistence;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Model;

namespace Vistara.Api.Composition.Gallery;

internal sealed class GalleryShareAssetCatalog(
    VistaraDbContext context,
    IAssetQueryStore assets,
    IHttpContextAccessor httpContextAccessor,
    IPlatformTenantContext tenantContext) : IShareAssetCatalog
{
    public async ValueTask<IReadOnlyList<ShareAssetSnapshot>?> CaptureSnapshotAsync(
        Guid tenantId,
        ShareTargetType targetType,
        Guid? albumId,
        IReadOnlyList<ShareAssetReference> requestedAssets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedAssets);
        HttpContext? httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null ||
            tenantContext.TenantId != tenantId ||
            !GalleryPrincipalReader.TryRead(
                httpContext.User,
                tenantContext,
                out Guid principalTenantId,
                out Guid actorId) ||
            principalTenantId != tenantId)
        {
            return null;
        }

        ShareAssetReference[] references;
        if (targetType == ShareTargetType.Album)
        {
            if (albumId is not { } id || !GalleryPrincipalReader.IsUuid7(id))
            {
                return null;
            }

            AlbumRow? album = await context.Albums
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == id,
                    cancellationToken);
            string? role = httpContext.User.FindFirstValue(ClaimTypes.Role);
            if (album is null ||
                (album.OwnerId != actorId &&
                 role is not ("TenantOwner" or "TenantAdmin")))
            {
                return null;
            }

            references = await (
                    from item in context.AlbumItems.AsNoTracking()
                    join asset in context.Assets.AsNoTracking()
                        on item.AssetId equals asset.Id
                    where item.AlbumId == id
                    orderby item.Position, item.AssetId
                    select new ShareAssetReference(
                        item.AssetId,
                        asset.Version))
                .Take(201)
                .ToArrayAsync(cancellationToken);
        }
        else if (targetType == ShareTargetType.Snapshot &&
                 albumId is null)
        {
            references = requestedAssets.ToArray();
        }
        else
        {
            return null;
        }

        if (references.Length is < 1 or > 200 ||
            references.Select(reference => reference.AssetId)
                .Distinct()
                .Count() != references.Length)
        {
            return null;
        }

        var scope = new AssetQueryScope(tenantId, actorId);
        var snapshots = new List<ShareAssetSnapshot>(references.Length);
        foreach (ShareAssetReference reference in references)
        {
            AssetDetail? detail = await assets.GetAsync(
                scope,
                reference.AssetId,
                cancellationToken);
            Guid? revisionId = await context.Assets
                .AsNoTracking()
                .Where(row =>
                    row.Id == reference.AssetId &&
                    row.Status != "Trashed" &&
                    row.Status != "Purged")
                .Select(row => row.CurrentRevisionId)
                .SingleOrDefaultAsync(cancellationToken);
            if (detail is null ||
                revisionId is null ||
                detail.Asset.Version != reference.Version)
            {
                return null;
            }

            snapshots.Add(new ShareAssetSnapshot(
                detail.Asset.Id,
                revisionId.Value,
                detail.Asset.RevisionNumber,
                detail.Asset.Version,
                detail.Asset.Title,
                detail.Asset.Description,
                detail.Asset.CapturedAt,
                detail.Asset.Width,
                detail.Asset.Height,
                detail.Asset.Renditions
                    .Select(rendition => ToShareRendition(
                        detail.Asset.Id,
                        rendition))
                    .OfType<ShareRendition>()
                    .ToArray()));
        }

        return snapshots;
    }

    /// <summary>
    /// Captures a rendition the share can actually deliver. A public derivative
    /// keeps its immutable <c>/media/</c> path, while a private Ready rendition
    /// records the derivative request identifier so the share can publish a
    /// revocable, share-scoped delivery URL instead of a path that only the
    /// owning tenant may fetch. Anything else is not deliverable and is
    /// dropped.
    /// </summary>
    private static ShareRendition? ToShareRendition(
        Guid assetId,
        AssetDeliverySource rendition)
    {
        ShareAccess required = rendition.Kind.StartsWith(
            "download",
            StringComparison.OrdinalIgnoreCase)
            ? ShareAccess.DownloadRenditions
            : ShareAccess.View;
        if (rendition.Path.StartsWith("/media/", StringComparison.Ordinal))
        {
            return new(
                rendition.Kind,
                rendition.Path,
                rendition.Width,
                rendition.Height,
                rendition.ContentType,
                required);
        }

        return TryReadRenditionId(assetId, rendition.Path, out Guid renditionId)
            ? new(
                rendition.Kind,
                rendition.Path,
                rendition.Width,
                rendition.Height,
                rendition.ContentType,
                required,
                renditionId.ToString("D"))
            : null;
    }

    private static bool TryReadRenditionId(
        Guid assetId,
        string path,
        out Guid renditionId)
    {
        renditionId = default;
        string[] segments = path.Split('/');
        return segments.Length == 5 &&
            segments[0].Length == 0 &&
            string.Equals(segments[1], "delivery", StringComparison.Ordinal) &&
            string.Equals(segments[2], "assets", StringComparison.Ordinal) &&
            Guid.TryParse(segments[3], out Guid pathAssetId) &&
            pathAssetId == assetId &&
            Guid.TryParse(segments[4], out renditionId) &&
            renditionId != Guid.Empty;
    }
}

internal sealed class GalleryShareAuditSink(
    VistaraDbContext context,
    IMutableTenantScope tenantScope,
    IUuid7Generator ids,
    IHttpContextAccessor httpContextAccessor) : IShareAuditSink
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async ValueTask WriteAsync(
        ShareAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (auditEvent.TenantId is not { } tenantId)
        {
            return;
        }

        tenantScope.Establish(tenantId);
        context.AuditEvents.Add(new AuditEventRow
        {
            Id = ids.NewId(),
            TenantId = tenantId,
            ActorKind = ActorKind(auditEvent, httpContextAccessor.HttpContext),
            ActorIdentifier =
                auditEvent.ActorId?.ToString("D") ?? "public-share-recipient",
            Action = $"Share{auditEvent.Action}",
            ResourceType = "Share",
            ResourceIdentifier =
                auditEvent.ShareId?.ToString("D") ?? "unresolved",
            BeforeJson = "{}",
            AfterJson = JsonSerializer.Serialize(
                new { auditEvent.ReasonCode },
                JsonOptions),
            Outcome = auditEvent.ReasonCode is null ? "Succeeded" : "Rejected",
            OccurredAtUtc = auditEvent.OccurredAtUtc,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string ActorKind(
        ShareAuditEvent auditEvent,
        HttpContext? context) =>
        !auditEvent.ActorId.HasValue
            ? "System"
            : string.Equals(
                context?.User.FindFirstValue("vistara_auth_kind"),
                PlatformAuthenticationKind.ApiKey.ToString(),
                StringComparison.Ordinal)
                ? "ApiKey"
                : "User";
}
