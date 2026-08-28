using Vistara.Domain.Common;

namespace Vistara.Domain.Lifecycle;

public sealed class RetentionHold
{
    private RetentionHold(
        RetentionHoldId id,
        LifecycleTenantId tenantId,
        LifecycleAssetId assetId,
        string reason,
        LifecycleUserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        AssetId = assetId;
        Reason = reason;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public RetentionHoldId Id { get; }

    public LifecycleTenantId TenantId { get; }

    public LifecycleAssetId AssetId { get; }

    public string Reason { get; }

    public LifecycleUserId CreatedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public LifecycleUserId? ReleasedBy { get; private set; }

    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public bool IsActive => !ReleasedAtUtc.HasValue;

    public long Version { get; private set; }

    public static Result<RetentionHold> Create(
        RetentionHoldId id,
        LifecycleTenantId tenantId,
        LifecycleAssetId assetId,
        string reason,
        LifecycleUserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        if (id.Value == Guid.Empty ||
            tenantId.Value == Guid.Empty ||
            assetId.Value == Guid.Empty ||
            createdBy.Value == Guid.Empty)
        {
            return Result.Failure<RetentionHold>(LifecycleErrors.InvalidIdentifier());
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure<RetentionHold>(LifecycleErrors.HoldReasonRequired());
        }

        if (!LifecycleTime.IsUtc(createdAtUtc))
        {
            return Result.Failure<RetentionHold>(LifecycleErrors.TimestampMustBeUtc());
        }

        return Result.Success(new RetentionHold(
            id,
            tenantId,
            assetId,
            reason.Trim(),
            createdBy,
            createdAtUtc));
    }

    public Result Release(LifecycleUserId releasedBy, DateTimeOffset releasedAtUtc)
    {
        if (!LifecycleTime.IsUtc(releasedAtUtc))
        {
            return Result.Failure(LifecycleErrors.TimestampMustBeUtc());
        }

        if (releasedBy.Value == Guid.Empty)
        {
            return Result.Failure(LifecycleErrors.InvalidIdentifier());
        }

        if (!IsActive)
        {
            return Result.Success();
        }

        if (releasedAtUtc < CreatedAtUtc)
        {
            return Result.Failure(LifecycleErrors.HoldReleaseBeforeCreation());
        }

        ReleasedBy = releasedBy;
        ReleasedAtUtc = releasedAtUtc;
        Version++;
        return Result.Success();
    }
}
