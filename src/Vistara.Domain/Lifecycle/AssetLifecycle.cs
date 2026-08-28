using Vistara.Domain.Common;

namespace Vistara.Domain.Lifecycle;

public enum AssetLifecycleState
{
    Ready,
    Trashed,
    Purging,
    Purged,
}

public enum PurgeBarrier
{
    NotTrashed,
    InvalidTimestamp,
    RetentionPeriod,
    ActiveHold,
    RevisionChanged,
    BlockingReference,
}

public enum PurgeInitiatorKind
{
    Human,
    RetentionPolicy,
}

public sealed record PurgeEvaluation(
    DateTimeOffset EvaluatedAtUtc,
    long ObservedRevision,
    bool HasBlockingReferences);

public sealed record PurgeRequest(
    PurgeBatchId BatchId,
    LifecycleUserId RequestedBy,
    PurgeInitiatorKind InitiatorKind,
    PurgeEvaluation Evaluation);

public sealed class PurgeEligibility
{
    internal PurgeEligibility(IEnumerable<PurgeBarrier> barriers)
    {
        Barriers = barriers.Distinct().ToArray();
    }

    public IReadOnlyList<PurgeBarrier> Barriers { get; }

    public bool IsEligible => Barriers.Count == 0;
}

public sealed class PurgeExecutionToken
{
    internal PurgeExecutionToken(
        LifecycleAssetId assetId,
        LifecycleTenantId tenantId,
        PurgeBatchId batchId,
        long observedRevision,
        long lifecycleVersion,
        AssetLifecycleState state,
        DateTimeOffset retentionPurgeAtUtc,
        int activeHoldCount,
        bool hasBlockingReferences,
        string relationshipDigest,
        DateTimeOffset evaluatedAtUtc)
    {
        AssetId = assetId;
        TenantId = tenantId;
        BatchId = batchId;
        ObservedRevision = observedRevision;
        LifecycleVersion = lifecycleVersion;
        State = state;
        RetentionPurgeAtUtc = retentionPurgeAtUtc;
        ActiveHoldCount = activeHoldCount;
        HasBlockingReferences = hasBlockingReferences;
        RelationshipDigest = relationshipDigest;
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    public LifecycleAssetId AssetId { get; }

    public LifecycleTenantId TenantId { get; }

    public PurgeBatchId BatchId { get; }

    public long ObservedRevision { get; }

    public long LifecycleVersion { get; }

    public AssetLifecycleState State { get; }

    public DateTimeOffset RetentionPurgeAtUtc { get; }

    public int ActiveHoldCount { get; }

    public bool HasBlockingReferences { get; }

    public string RelationshipDigest { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }
}

public sealed record TrashMetadata(
    LifecycleUserId DeletedBy,
    DateTimeOffset DeletedAtUtc,
    DateTimeOffset PurgeAtUtc,
    string Reason);

public sealed record RestorationMetadata(
    LifecycleUserId RestoredBy,
    DateTimeOffset RestoredAtUtc);

public sealed class AssetLifecycle
{
    private readonly List<RetentionHold> _holds = [];
    private RelationshipSnapshot _relationships = RelationshipSnapshot.Empty;
    private bool _hasBeenTrashed;

    private AssetLifecycle(
        LifecycleAssetId assetId,
        LifecycleTenantId tenantId,
        long currentRevision)
    {
        AssetId = assetId;
        TenantId = tenantId;
        CurrentRevision = currentRevision;
        State = AssetLifecycleState.Ready;
        Version = 1;
    }

    public LifecycleAssetId AssetId { get; }

    public LifecycleTenantId TenantId { get; }

    public long CurrentRevision { get; private set; }

    public AssetLifecycleState State { get; private set; }

    public TrashMetadata? TrashEntry { get; private set; }

    public RestorationMetadata? LastRestoration { get; private set; }

    public RelationshipSnapshot Relationships => _relationships;

    public IReadOnlyList<RetentionHold> Holds => _holds.AsReadOnly();

    public PurgeRequest? ActivePurgeRequest { get; private set; }

    public DeletionTombstone? Tombstone { get; private set; }

    public long Version { get; private set; }

    public static AssetLifecycle Create(
        LifecycleAssetId assetId,
        LifecycleTenantId tenantId,
        long currentRevision)
    {
        if (assetId.Value == Guid.Empty || tenantId.Value == Guid.Empty || currentRevision < 1)
        {
            throw new ArgumentException("Asset lifecycle identity and revision must be valid.");
        }

        return new AssetLifecycle(assetId, tenantId, currentRevision);
    }

    public Result Trash(
        LifecycleUserId deletedBy,
        DateTimeOffset deletedAtUtc,
        DateTimeOffset purgeAtUtc,
        string reason,
        RelationshipSnapshot relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);

        if (State == AssetLifecycleState.Trashed)
        {
            return Result.Success();
        }

        if (State is AssetLifecycleState.Purging or AssetLifecycleState.Purged)
        {
            return Result.Failure(LifecycleErrors.InvalidLifecycleTransition());
        }

        if (deletedBy.Value == Guid.Empty)
        {
            return Result.Failure(LifecycleErrors.InvalidIdentifier());
        }

        if (!LifecycleTime.IsUtc(deletedAtUtc) || !LifecycleTime.IsUtc(purgeAtUtc))
        {
            return Result.Failure(LifecycleErrors.TimestampMustBeUtc());
        }

        if (purgeAtUtc <= deletedAtUtc)
        {
            return Result.Failure(LifecycleErrors.PurgeDeadlineInvalid());
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(LifecycleErrors.TrashReasonRequired());
        }

        TrashEntry = new TrashMetadata(deletedBy, deletedAtUtc, purgeAtUtc, reason.Trim());
        LastRestoration = null;
        _relationships = relationships;
        _hasBeenTrashed = true;
        State = AssetLifecycleState.Trashed;
        Version++;
        return Result.Success();
    }

    public Result<RelationshipSnapshot> Restore(
        LifecycleUserId restoredBy,
        DateTimeOffset restoredAtUtc)
    {
        if (State == AssetLifecycleState.Ready && _hasBeenTrashed)
        {
            return Result.Success(_relationships);
        }

        if (State != AssetLifecycleState.Trashed)
        {
            return Result.Failure<RelationshipSnapshot>(LifecycleErrors.InvalidLifecycleTransition());
        }

        if (restoredBy.Value == Guid.Empty)
        {
            return Result.Failure<RelationshipSnapshot>(LifecycleErrors.InvalidIdentifier());
        }

        if (!LifecycleTime.IsUtc(restoredAtUtc))
        {
            return Result.Failure<RelationshipSnapshot>(LifecycleErrors.TimestampMustBeUtc());
        }

        State = AssetLifecycleState.Ready;
        LastRestoration = new RestorationMetadata(restoredBy, restoredAtUtc);
        TrashEntry = null;
        Version++;
        return Result.Success(_relationships);
    }

    public Result PlaceHold(RetentionHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);

        if (hold.TenantId != TenantId || hold.AssetId != AssetId)
        {
            return Result.Failure(LifecycleErrors.CrossTenantOrAssetReference());
        }

        if (_holds.Any(candidate => candidate.Id == hold.Id))
        {
            return Result.Success();
        }

        _holds.Add(hold);
        Version++;
        return Result.Success();
    }

    public Result ReleaseHold(
        RetentionHoldId holdId,
        LifecycleUserId releasedBy,
        DateTimeOffset releasedAtUtc)
    {
        RetentionHold? hold = _holds.SingleOrDefault(candidate => candidate.Id == holdId);
        if (hold is null)
        {
            return Result.Failure(LifecycleErrors.HoldNotFound());
        }

        bool wasActive = hold.IsActive;
        Result result = hold.Release(releasedBy, releasedAtUtc);
        if (result.IsSuccess && wasActive && !hold.IsActive)
        {
            Version++;
        }

        return result;
    }

    public PurgeEligibility EvaluatePurge(PurgeEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var barriers = new List<PurgeBarrier>();

        if (State != AssetLifecycleState.Trashed)
        {
            barriers.Add(PurgeBarrier.NotTrashed);
        }

        AddPurgeBarriers(evaluation, barriers);

        return new PurgeEligibility(barriers);
    }

    public Result BeginPurge(PurgeRequest request, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (expectedVersion != Version)
        {
            return Result.Failure(LifecycleErrors.VersionConflict());
        }

        if (request.BatchId.Value == Guid.Empty || request.RequestedBy.Value == Guid.Empty)
        {
            return Result.Failure(LifecycleErrors.InvalidIdentifier());
        }

        if (!Enum.IsDefined(request.InitiatorKind))
        {
            return Result.Failure(LifecycleErrors.PurgeInitiatorInvalid());
        }

        PurgeEligibility eligibility = EvaluatePurge(request.Evaluation);
        if (!eligibility.IsEligible)
        {
            return Result.Failure(LifecycleErrors.PurgeBlocked());
        }

        ActivePurgeRequest = request;
        State = AssetLifecycleState.Purging;
        Version++;
        return Result.Success();
    }

    /// <summary>
    /// Re-evaluates purge while the persistence boundary holds the lifecycle and
    /// reference state stable immediately before physical deletion.
    /// </summary>
    public Result<PurgeExecutionToken> EvaluatePurgeForExecution(
        PurgeEvaluation evaluation,
        long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (expectedVersion != Version)
        {
            return Result.Failure<PurgeExecutionToken>(LifecycleErrors.VersionConflict());
        }

        if (State != AssetLifecycleState.Purging ||
            ActivePurgeRequest is null ||
            TrashEntry is null)
        {
            return Result.Failure<PurgeExecutionToken>(
                LifecycleErrors.InvalidLifecycleTransition());
        }

        var barriers = new List<PurgeBarrier>();
        AddPurgeBarriers(evaluation, barriers);
        if (barriers.Count > 0)
        {
            return Result.Failure<PurgeExecutionToken>(LifecycleErrors.PurgeBlocked());
        }

        return Result.Success(new PurgeExecutionToken(
            AssetId,
            TenantId,
            ActivePurgeRequest.BatchId,
            evaluation.ObservedRevision,
            Version,
            State,
            TrashEntry.PurgeAtUtc,
            _holds.Count(hold => hold.IsActive),
            evaluation.HasBlockingReferences,
            _relationships.Digest,
            evaluation.EvaluatedAtUtc));
    }

    public Result<DeletionTombstone> CompletePurge(
        PurgeExecutionToken executionToken,
        DateTimeOffset purgedAtUtc,
        DateTimeOffset backupExpiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(executionToken);

        if (!Matches(executionToken) ||
            purgedAtUtc < executionToken.EvaluatedAtUtc)
        {
            return Result.Failure<DeletionTombstone>(
                LifecycleErrors.PurgeEvaluationStale());
        }

        if (_holds.Any(hold => hold.IsActive) ||
            executionToken.HasBlockingReferences ||
            purgedAtUtc < executionToken.RetentionPurgeAtUtc)
        {
            return Result.Failure<DeletionTombstone>(LifecycleErrors.PurgeBlocked());
        }

        Result<DeletionTombstone> tombstoneResult = DeletionTombstone.Create(
            AssetId,
            TenantId,
            purgedAtUtc,
            backupExpiresAtUtc,
            _relationships.Count,
            _relationships.Digest);
        if (!tombstoneResult.TryGetValue(out DeletionTombstone? tombstone))
        {
            return tombstoneResult;
        }

        Tombstone = tombstone;
        State = AssetLifecycleState.Purged;
        TrashEntry = null;
        ActivePurgeRequest = null;
        Version++;
        return Result.Success(tombstone);
    }

    private void AddPurgeBarriers(
        PurgeEvaluation evaluation,
        List<PurgeBarrier> barriers)
    {
        if (!LifecycleTime.IsUtc(evaluation.EvaluatedAtUtc))
        {
            barriers.Add(PurgeBarrier.InvalidTimestamp);
        }

        if (TrashEntry is not null && evaluation.EvaluatedAtUtc < TrashEntry.PurgeAtUtc)
        {
            barriers.Add(PurgeBarrier.RetentionPeriod);
        }

        if (_holds.Any(hold => hold.IsActive))
        {
            barriers.Add(PurgeBarrier.ActiveHold);
        }

        if (evaluation.ObservedRevision != CurrentRevision)
        {
            barriers.Add(PurgeBarrier.RevisionChanged);
        }

        if (evaluation.HasBlockingReferences)
        {
            barriers.Add(PurgeBarrier.BlockingReference);
        }
    }

    private bool Matches(PurgeExecutionToken token) =>
        State == AssetLifecycleState.Purging &&
        ActivePurgeRequest is not null &&
        TrashEntry is not null &&
        token.AssetId == AssetId &&
        token.TenantId == TenantId &&
        token.BatchId == ActivePurgeRequest.BatchId &&
        token.ObservedRevision == CurrentRevision &&
        token.LifecycleVersion == Version &&
        token.State == State &&
        token.RetentionPurgeAtUtc == TrashEntry.PurgeAtUtc &&
        token.ActiveHoldCount == _holds.Count(hold => hold.IsActive) &&
        !token.HasBlockingReferences &&
        token.RelationshipDigest == _relationships.Digest;
}
