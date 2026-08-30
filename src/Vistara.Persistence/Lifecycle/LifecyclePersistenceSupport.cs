using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Lifecycle;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Lifecycle;

internal static class LifecyclePersistenceSupport
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static async ValueTask<string?> GetActiveRoleAsync(
        VistaraDbContext context,
        Guid actorId,
        CancellationToken cancellationToken) =>
        await context.TenantMemberships
            .AsNoTracking()
            .Where(row => row.UserId == actorId && row.Status == "Active")
            .Select(row => row.Role)
            .SingleOrDefaultAsync(cancellationToken);

    internal static bool CanMutateOwnedAsset(
        string? role,
        Guid actorId,
        Guid ownerId) =>
        role is "TenantOwner" or "TenantAdmin" || actorId == ownerId;

    internal static async ValueTask<RelationshipSnapshot> LoadLiveRelationshipsAsync(
        VistaraDbContext context,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var relationships = new List<RelationshipReference>();
        relationships.AddRange(await context.AlbumItems
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .Select(row => new RelationshipReference(
                RelationshipKind.Album,
                row.AlbumId))
            .ToListAsync(cancellationToken));
        relationships.AddRange(await context.AssetTags
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .Select(row => new RelationshipReference(
                RelationshipKind.Tag,
                row.TagId))
            .ToListAsync(cancellationToken));
        relationships.AddRange(await context.AssetFavorites
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .Select(row => new RelationshipReference(
                RelationshipKind.Favorite,
                row.UserId))
            .ToListAsync(cancellationToken));
        relationships.AddRange(await context.ShareAssets
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .Select(row => new RelationshipReference(
                RelationshipKind.Share,
                row.ShareId))
            .ToListAsync(cancellationToken));
        relationships.AddRange(await context.ResourceGrants
            .AsNoTracking()
            .Where(row =>
                row.ResourceKind == "Asset" &&
                row.ResourceId == assetId)
            .Select(row => new RelationshipReference(
                RelationshipKind.Grant,
                row.Id))
            .ToListAsync(cancellationToken));
        return RelationshipSnapshot.Create(relationships);
    }

    internal static async ValueTask<RelationshipSnapshot> LoadFrozenRelationshipsAsync(
        VistaraDbContext context,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        List<RelationshipReference> relationships = await context.RelationshipSnapshots
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .Select(row => new RelationshipReference(
                Enum.Parse<RelationshipKind>(row.Kind),
                row.ResourceId))
            .ToListAsync(cancellationToken);
        return RelationshipSnapshot.Create(relationships);
    }

    internal static void AddAudit(
        VistaraDbContext context,
        IUuid7Generator ids,
        Guid tenantId,
        Guid actorId,
        string action,
        Guid resourceId,
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        string outcome,
        DateTimeOffset occurredAtUtc,
        string resourceType = "asset")
    {
        AddAudit(
            context,
            ids,
            tenantId,
            actorId,
            action,
            resourceId.ToString("D"),
            before,
            after,
            outcome,
            occurredAtUtc,
            resourceType);
    }

    internal static void AddAudit(
        VistaraDbContext context,
        IUuid7Generator ids,
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceIdentifier,
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        string outcome,
        DateTimeOffset occurredAtUtc,
        string resourceType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceIdentifier);
        context.AuditEvents.Add(new AuditEventRow
        {
            Id = ids.NewId(),
            TenantId = tenantId,
            ActorKind = "User",
            ActorIdentifier = actorId.ToString("D"),
            Action = action,
            ResourceType = resourceType,
            ResourceIdentifier = resourceIdentifier,
            BeforeJson = JsonSerializer.Serialize(before, JsonOptions),
            AfterJson = JsonSerializer.Serialize(after, JsonOptions),
            Outcome = outcome,
            OccurredAtUtc = occurredAtUtc,
        });
    }

    internal static string PurgeBatchItemResourceIdentifier(
        Guid batchId,
        Guid assetId) =>
        $"{batchId:D}:{assetId:D}";

    internal static string? ReadAuditErrorCode(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(
                "errorCode",
                out JsonElement errorCode)
                ? errorCode.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> StateSummary(
        string state,
        string? relationshipDigest = null,
        long? reclaimedBytes = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["state"] = state,
            ["reason"] = AuditField.RedactedValue,
            ["privateMetadata"] = AuditField.RedactedValue,
            ["storageObjectKeys"] = AuditField.RedactedValue,
        };
        if (relationshipDigest is not null)
        {
            values["relationshipDigest"] = relationshipDigest;
        }

        if (reclaimedBytes.HasValue)
        {
            values["reclaimedBytes"] = reclaimedBytes.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        return values;
    }

    internal static string HashRequest(
        string operation,
        IEnumerable<LifecycleAssetTarget> targets)
    {
        string canonical = operation + "\n" + string.Join(
            '\n',
            targets
                .OrderBy(target => target.AssetId)
                .Select(target => $"{target.AssetId:N}:{target.Version}"));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string IdempotencyStorageKey(
        string operation,
        string idempotencyKey) =>
        $"{operation}:{Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))}";

    internal static async ValueTask<LifecyclePurgeCandidateState?>
        LoadPurgeCandidateAsync(
            VistaraDbContext context,
            Guid assetId,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken)
    {
        AssetRow? asset = await context.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == assetId, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        AssetLifecycleRow? lifecycle = await context.AssetLifecycles
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.AssetId == assetId, cancellationToken);
        TrashEntryRow? trash = await context.TrashEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.AssetId == assetId, cancellationToken);
        AssetRevisionRow? currentRevision = asset.CurrentRevisionId is null
            ? null
            : await context.AssetRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == asset.CurrentRevisionId,
                    cancellationToken);
        int activeHolds = await context.RetentionHolds
            .AsNoTracking()
            .CountAsync(
                row => row.AssetId == assetId && row.ReleasedAtUtc == null,
                cancellationToken);
        int activeShares = await (
            from shareAsset in context.ShareAssets.AsNoTracking()
            join share in context.Shares.AsNoTracking()
                on new { shareAsset.TenantId, Id = shareAsset.ShareId }
                equals new { share.TenantId, share.Id }
            where shareAsset.AssetId == assetId &&
                share.RevokedAtUtc == null &&
                (share.ExpiresAtUtc == null || share.ExpiresAtUtc > evaluatedAtUtc)
            select shareAsset.ShareId)
            .Distinct()
            .CountAsync(cancellationToken);
        int activeGrants = await context.ResourceGrants
            .AsNoTracking()
            .CountAsync(
                row =>
                    row.ResourceKind == "Asset" &&
                    row.ResourceId == assetId &&
                    row.RevokedAtUtc == null,
                cancellationToken);
        RelationshipSnapshot live = await LoadLiveRelationshipsAsync(
            context,
            assetId,
            cancellationToken);
        RelationshipSnapshot frozen = await LoadFrozenRelationshipsAsync(
            context,
            assetId,
            cancellationToken);
        var barriers = new List<string>();
        bool lifecycleStateAllowed = lifecycle?.State is "Trashed" or "Purging";
        if (asset.Status != "Trashed" ||
            lifecycle is null ||
            !lifecycleStateAllowed ||
            trash is null)
        {
            barriers.Add("notTrashed");
        }

        if (trash is not null && evaluatedAtUtc < trash.PurgeAtUtc)
        {
            barriers.Add("retentionPeriod");
        }

        if (activeHolds > 0)
        {
            barriers.Add("activeHold");
        }

        long revisionNumber = currentRevision?.RevisionNumber ?? 0;
        if (currentRevision is null ||
            lifecycle is null ||
            lifecycle.CurrentRevision != revisionNumber)
        {
            barriers.Add("revisionChanged");
        }

        int blockingReferences = checked(activeShares + activeGrants);
        if (blockingReferences > 0)
        {
            barriers.Add("blockingReference");
        }

        if (live != frozen)
        {
            barriers.Add("referencesChanged");
        }

        long derivativeBytes = await context.Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(row =>
                row.AssetId == assetId &&
                row.RepresentationStorageKey != null &&
                row.RepresentationContentLength != null)
            .SumAsync(
                row => row.RepresentationContentLength ?? 0,
                cancellationToken);
        List<Guid> blobIds = await context.AssetRevisions
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .Select(row => row.BlobId)
            .Distinct()
            .ToListAsync(cancellationToken);
        long originalBytes = 0;
        foreach (Guid blobId in blobIds)
        {
            int references = await context.AssetRevisions
                .AsNoTracking()
                .CountAsync(row => row.BlobId == blobId, cancellationToken);
            if (references == 1)
            {
                originalBytes = checked(
                    originalBytes +
                    await context.Blobs
                        .AsNoTracking()
                        .Where(row => row.Id == blobId)
                        .Select(row => row.SizeBytes)
                        .SingleAsync(cancellationToken));
            }
        }

        return new LifecyclePurgeCandidateState(
            asset,
            lifecycle,
            trash,
            revisionNumber,
            activeHolds,
            blockingReferences,
            activeShares,
            live,
            frozen,
            barriers,
            checked(derivativeBytes + originalBytes));
    }

    internal static string ComputePurgeDigest(
        Guid tenantId,
        Guid batchId,
        DateTimeOffset expiresAtUtc,
        IEnumerable<LifecyclePurgeCandidateState> candidates)
    {
        string canonical = string.Join(
            '\n',
            new[]
            {
                $"tenant:{tenantId:N}",
                $"batch:{batchId:N}",
                $"expires:{expiresAtUtc:O}",
            }.Concat(
                candidates
                    .OrderBy(candidate => candidate.Asset.Id)
                    .Select(candidate =>
                        string.Join(
                            ':',
                            candidate.Asset.Id.ToString("N"),
                            candidate.Asset.Version,
                            candidate.RevisionNumber,
                            candidate.Lifecycle?.Version ?? 0,
                            candidate.FrozenRelationships.Digest,
                            candidate.LiveRelationships.Digest,
                            candidate.ActiveHoldCount,
                            candidate.BlockingReferenceCount,
                            candidate.SharedLinkImpact,
                            candidate.EstimatedReclaimBytes,
                            string.Join(
                                ',',
                                candidate.Barriers.Order(StringComparer.Ordinal))))));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool FixedTimeEqualsHex(string left, string right)
    {
        if (left.Length != 64 ||
            right.Length != 64 ||
            left.Any(character => !Uri.IsHexDigit(character)) ||
            right.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    internal static string BarrierErrorCode(
        LifecyclePurgeCandidateState candidate) =>
        (candidate.Barriers.Count == 0 ? null : candidate.Barriers[0]) switch
        {
            "activeHold" => "purge.active_hold",
            "revisionChanged" => "purge.revision_changed",
            "blockingReference" => "purge.blocking_reference",
            "referencesChanged" => "purge.references_changed",
            "retentionPeriod" => "purge.retention_period",
            _ => "purge.blocked",
        };
}

internal sealed record LifecyclePurgeCandidateState(
    AssetRow Asset,
    AssetLifecycleRow? Lifecycle,
    TrashEntryRow? Trash,
    long RevisionNumber,
    int ActiveHoldCount,
    int BlockingReferenceCount,
    int SharedLinkImpact,
    RelationshipSnapshot LiveRelationships,
    RelationshipSnapshot FrozenRelationships,
    IReadOnlyList<string> Barriers,
    long EstimatedReclaimBytes)
{
    internal bool Eligible => Barriers.Count == 0;
}
