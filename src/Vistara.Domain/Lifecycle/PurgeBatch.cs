using Vistara.Domain.Common;

namespace Vistara.Domain.Lifecycle;

public enum PurgeBatchState
{
    Draft,
    DryRunCompleted,
    Approved,
    Executing,
    Completed,
    Cancelled,
}

public enum PurgeItemResult
{
    Purged,
    Blocked,
    Failed,
}

public sealed record PurgeBatchItem(
    LifecycleAssetId AssetId,
    long Revision,
    PurgeItemResult Result,
    long ReclaimedBytes);

public sealed class PurgeBatch
{
    private readonly List<PurgeBatchItem> _items = [];

    private PurgeBatch(
        PurgeBatchId id,
        LifecycleTenantId tenantId,
        LifecycleUserId requestedBy,
        DateTimeOffset requestedAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        RequestedBy = requestedBy;
        RequestedAtUtc = requestedAtUtc;
        State = PurgeBatchState.Draft;
        Version = 1;
    }

    public PurgeBatchId Id { get; }

    public LifecycleTenantId TenantId { get; }

    public LifecycleUserId RequestedBy { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    public LifecycleUserId? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public string? DryRunHash { get; private set; }

    public DateTimeOffset? DryRunCompletedAtUtc { get; private set; }

    public int CandidateCount { get; private set; }

    public int EligibleCount { get; private set; }

    public int ProcessedCount => _items.Count;

    public long ReclaimedBytes => _items.Sum(item => item.ReclaimedBytes);

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public PurgeBatchState State { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<PurgeBatchItem> Items => _items.AsReadOnly();

    public static PurgeBatch Create(
        PurgeBatchId id,
        LifecycleTenantId tenantId,
        LifecycleUserId requestedBy,
        DateTimeOffset requestedAtUtc)
    {
        if (id.Value == Guid.Empty ||
            tenantId.Value == Guid.Empty ||
            requestedBy.Value == Guid.Empty ||
            !LifecycleTime.IsUtc(requestedAtUtc))
        {
            throw new ArgumentException("Purge batch metadata is invalid.");
        }

        return new PurgeBatch(id, tenantId, requestedBy, requestedAtUtc);
    }

    public Result RecordDryRun(
        string dryRunHash,
        int candidateCount,
        int eligibleCount,
        DateTimeOffset recordedAtUtc,
        long expectedVersion)
    {
        Result validation = ValidateMutation(expectedVersion, recordedAtUtc);
        if (validation.IsFailure)
        {
            return validation;
        }

        if (State != PurgeBatchState.Draft)
        {
            return Result.Failure(LifecycleErrors.InvalidPurgeBatchTransition());
        }

        if (string.IsNullOrWhiteSpace(dryRunHash) ||
            candidateCount < 0 ||
            eligibleCount < 0 ||
            eligibleCount > candidateCount)
        {
            return Result.Failure(LifecycleErrors.DryRunInvalid());
        }

        DryRunHash = dryRunHash;
        DryRunCompletedAtUtc = recordedAtUtc;
        CandidateCount = candidateCount;
        EligibleCount = eligibleCount;
        State = PurgeBatchState.DryRunCompleted;
        Version++;
        return Result.Success();
    }

    public Result Approve(
        LifecycleUserId approvedBy,
        DateTimeOffset approvedAtUtc,
        long expectedVersion)
    {
        Result validation = ValidateMutation(expectedVersion, approvedAtUtc);
        if (validation.IsFailure)
        {
            return validation;
        }

        if (State != PurgeBatchState.DryRunCompleted)
        {
            return Result.Failure(LifecycleErrors.InvalidPurgeBatchTransition());
        }

        if (approvedBy.Value == Guid.Empty)
        {
            return Result.Failure(LifecycleErrors.InvalidIdentifier());
        }

        if (approvedBy == RequestedBy)
        {
            return Result.Failure(LifecycleErrors.SeparatePurgeApproverRequired());
        }

        ApprovedBy = approvedBy;
        ApprovedAtUtc = approvedAtUtc;
        State = PurgeBatchState.Approved;
        Version++;
        return Result.Success();
    }

    public Result Start(DateTimeOffset startedAtUtc, long expectedVersion)
    {
        Result validation = ValidateMutation(expectedVersion, startedAtUtc);
        if (validation.IsFailure)
        {
            return validation;
        }

        if (State != PurgeBatchState.Approved)
        {
            return Result.Failure(LifecycleErrors.InvalidPurgeBatchTransition());
        }

        StartedAtUtc = startedAtUtc;
        State = PurgeBatchState.Executing;
        Version++;
        return Result.Success();
    }

    public Result RecordItem(PurgeBatchItem item, long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            return Result.Failure(LifecycleErrors.VersionConflict());
        }

        if (State != PurgeBatchState.Executing)
        {
            return Result.Failure(LifecycleErrors.InvalidPurgeBatchTransition());
        }

        if (item.AssetId.Value == Guid.Empty || item.Revision < 1 || item.ReclaimedBytes < 0)
        {
            return Result.Failure(LifecycleErrors.PurgeBatchItemInvalid());
        }

        if (_items.Any(candidate => candidate.AssetId == item.AssetId))
        {
            return Result.Failure(LifecycleErrors.PurgeBatchItemDuplicate());
        }

        _items.Add(item);
        Version++;
        return Result.Success();
    }

    public Result Complete(DateTimeOffset completedAtUtc, long expectedVersion)
    {
        Result validation = ValidateMutation(expectedVersion, completedAtUtc);
        if (validation.IsFailure)
        {
            return validation;
        }

        if (State != PurgeBatchState.Executing || ProcessedCount != EligibleCount)
        {
            return Result.Failure(LifecycleErrors.InvalidPurgeBatchTransition());
        }

        CompletedAtUtc = completedAtUtc;
        State = PurgeBatchState.Completed;
        Version++;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset cancelledAtUtc, long expectedVersion)
    {
        Result validation = ValidateMutation(expectedVersion, cancelledAtUtc);
        if (validation.IsFailure)
        {
            return validation;
        }

        if (State is PurgeBatchState.Executing or PurgeBatchState.Completed)
        {
            return Result.Failure(LifecycleErrors.InvalidPurgeBatchTransition());
        }

        State = PurgeBatchState.Cancelled;
        CompletedAtUtc = cancelledAtUtc;
        Version++;
        return Result.Success();
    }

    private Result ValidateMutation(long expectedVersion, DateTimeOffset timestampUtc)
    {
        if (expectedVersion != Version)
        {
            return Result.Failure(LifecycleErrors.VersionConflict());
        }

        return LifecycleTime.IsUtc(timestampUtc)
            ? Result.Success()
            : Result.Failure(LifecycleErrors.TimestampMustBeUtc());
    }
}
