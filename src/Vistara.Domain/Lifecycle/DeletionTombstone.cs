using Vistara.Domain.Common;

namespace Vistara.Domain.Lifecycle;

public sealed class DeletionTombstone
{
    private DeletionTombstone(
        LifecycleAssetId formerAssetId,
        LifecycleTenantId tenantId,
        DateTimeOffset purgedAtUtc,
        DateTimeOffset backupExpiresAtUtc,
        int relationshipCount,
        string relationshipDigest)
    {
        FormerAssetId = formerAssetId;
        TenantId = tenantId;
        PurgedAtUtc = purgedAtUtc;
        BackupExpiresAtUtc = backupExpiresAtUtc;
        RelationshipCount = relationshipCount;
        RelationshipDigest = relationshipDigest;
    }

    public LifecycleAssetId FormerAssetId { get; }

    public LifecycleTenantId TenantId { get; }

    public DateTimeOffset PurgedAtUtc { get; }

    public DateTimeOffset BackupExpiresAtUtc { get; }

    public int RelationshipCount { get; }

    public string RelationshipDigest { get; }

    public static Result<DeletionTombstone> Create(
        LifecycleAssetId formerAssetId,
        LifecycleTenantId tenantId,
        DateTimeOffset purgedAtUtc,
        DateTimeOffset backupExpiresAtUtc,
        int relationshipCount,
        string relationshipDigest)
    {
        if (formerAssetId.Value == Guid.Empty || tenantId.Value == Guid.Empty)
        {
            return Result.Failure<DeletionTombstone>(LifecycleErrors.InvalidIdentifier());
        }

        if (!LifecycleTime.IsUtc(purgedAtUtc) || !LifecycleTime.IsUtc(backupExpiresAtUtc))
        {
            return Result.Failure<DeletionTombstone>(LifecycleErrors.TimestampMustBeUtc());
        }

        if (backupExpiresAtUtc < purgedAtUtc)
        {
            return Result.Failure<DeletionTombstone>(LifecycleErrors.TombstoneBackupExpiryInvalid());
        }

        if (relationshipCount < 0 ||
            string.IsNullOrEmpty(relationshipDigest) ||
            relationshipDigest.Length != 64 ||
            !relationshipDigest.All(Uri.IsHexDigit))
        {
            return Result.Failure<DeletionTombstone>(LifecycleErrors.TombstoneRelationshipsInvalid());
        }

        return Result.Success(new DeletionTombstone(
            formerAssetId,
            tenantId,
            purgedAtUtc,
            backupExpiresAtUtc,
            relationshipCount,
            relationshipDigest));
    }
}
