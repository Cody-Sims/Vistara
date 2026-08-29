using System.Globalization;
using Vistara.Application.Common.Storage;
using Vistara.Worker.Features.Reconciliation.Uploads;

namespace Vistara.IntegrationTests.UploadReconciliation;

internal sealed class TestReconciliationState(
    IReadOnlyList<UploadReconciliationCandidate> candidates)
    : IUploadReconciliationStatePort
{
    private readonly List<UploadReconciliationCandidate> _candidates = [.. candidates];
    private int _revalidationCount;

    internal int ReservationReleaseCount { get; private set; }

    internal int MutationCount { get; private set; }

    internal int? StealOnRevalidation { get; set; }

    internal bool CanonicalPreserved { get; private set; }

    internal bool Quarantined { get; private set; }

    internal string? SavedCursor { get; private set; }

    internal bool IsTerminal => _candidates.All(
        item => item.State is UploadReconciliationSessionState.Expired
            or UploadReconciliationSessionState.Aborted
            or UploadReconciliationSessionState.Accepted
            or UploadReconciliationSessionState.Quarantined);

    public ValueTask<UploadReconciliationPage> ScanAsync(
        UploadReconciliationScanRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int start = request.Cursor is null
            ? 0
            : int.Parse(
                request.Cursor.AsSpan("cursor-".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
        UploadReconciliationCandidate[] page = _candidates
            .Skip(start)
            .Take(request.MaximumSessions)
            .ToArray();
        string? next = page.Length == 0
            ? request.Cursor
            : page[^1].ContinuationCursor;
        return ValueTask.FromResult(new UploadReconciliationPage(page, next));
    }

    public ValueTask<UploadReconciliationCandidate?> RevalidateAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _revalidationCount++;
        if (StealOnRevalidation == _revalidationCount)
        {
            UploadReconciliationCandidate stolen = Find(fence) with
            {
                Fence = fence with { Version = fence.Version + 1, LeaseToken = "stolen" },
            };
            Replace(fence.UploadSessionId, stolen);
            return ValueTask.FromResult<UploadReconciliationCandidate?>(null);
        }

        UploadReconciliationCandidate? current = _candidates.SingleOrDefault(
            item => item.Fence.UploadSessionId == fence.UploadSessionId &&
                item.Fence.Version == fence.Version &&
                item.Fence.LeaseToken == fence.LeaseToken &&
                item.Fence.LeaseExpiresAtUtc > utcNow);
        return ValueTask.FromResult(current);
    }

    public ValueTask<UploadReconciliationMutationResult> ExpireAndReleaseAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            current => current with
            {
                State = UploadReconciliationSessionState.Expired,
                ReservationReleased = true,
            },
            releaseReservation: true,
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> CompleteAbortAndReleaseAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            current => current with
            {
                State = UploadReconciliationSessionState.Aborted,
                ReservationReleased = true,
            },
            releaseReservation: true,
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> RecordAbortOutcomeUnknownAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            current => current with
            {
                State = UploadReconciliationSessionState.OutcomeUnknownAbort,
            },
            releaseReservation: false,
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> CompleteCommitAsync(
        UploadReconciliationFence fence,
        BlobIdentity stagingIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            current => current with
            {
                State = UploadReconciliationSessionState.Accepted,
                ExpectedStagingVersion = stagingIdentity.Version,
            },
            releaseReservation: false,
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> CompleteCleanupAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            current => current,
            releaseReservation: false,
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> PreserveCanonicalAsync(
        UploadReconciliationFence fence,
        BlobIdentity canonicalIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        CanonicalPreserved = true;
        return MutateAsync(
            fence,
            current => current with
            {
                State = UploadReconciliationSessionState.Accepted,
            },
            releaseReservation: false,
            cancellationToken);
    }

    public ValueTask<UploadReconciliationMutationResult> QuarantineAsync(
        UploadReconciliationFence fence,
        ReconciliationQuarantineReason reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        Quarantined = true;
        return MutateAsync(
            fence,
            current => current with
            {
                State = UploadReconciliationSessionState.Quarantined,
            },
            releaseReservation: false,
            cancellationToken);
    }

    public ValueTask SaveCheckpointAsync(
        Guid runId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        SavedCursor = cursor;
        return ValueTask.CompletedTask;
    }

    internal void Replace(Guid uploadSessionId, UploadReconciliationCandidate replacement)
    {
        int index = _candidates.FindIndex(
            item => item.Fence.UploadSessionId == uploadSessionId);
        _candidates[index] = replacement;
    }

    private ValueTask<UploadReconciliationMutationResult> MutateAsync(
        UploadReconciliationFence fence,
        Func<UploadReconciliationCandidate, UploadReconciliationCandidate> mutation,
        bool releaseReservation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UploadReconciliationCandidate? current = _candidates.SingleOrDefault(
            item => item.Fence == fence);
        if (current is null)
        {
            return ValueTask.FromResult(UploadReconciliationMutationResult.Stale());
        }

        MutationCount++;
        bool released = releaseReservation && !current.ReservationReleased;
        if (released)
        {
            ReservationReleaseCount++;
        }

        UploadReconciliationCandidate updated = mutation(current) with
        {
            Fence = current.Fence with { Version = current.Fence.Version + 1 },
        };
        Replace(fence.UploadSessionId, updated);
        return ValueTask.FromResult(
            UploadReconciliationMutationResult.Applied(updated, released));
    }

    private UploadReconciliationCandidate Find(UploadReconciliationFence fence) =>
        _candidates.Single(item => item.Fence.UploadSessionId == fence.UploadSessionId);
}

internal sealed class TestReconciliationStorage : IUploadReconciliationStoragePort
{
    private readonly Dictionary<BlobKey, UploadReconciliationObjectHead> _objects = [];

    internal ReconciliationMultipartState MultipartState { get; set; } =
        ReconciliationMultipartState.Missing;

    internal ReconciliationProviderMutationOutcome AbortOutcome { get; set; } =
        ReconciliationProviderMutationOutcome.Succeeded;

    internal bool HeadReturnsRetry { get; set; }

    internal int AbortCalls { get; private set; }

    internal int CompleteCalls { get; private set; }

    internal int DeleteCalls { get; private set; }

    internal int DeleteCanonicalAttempts { get; private set; }

    public ValueTask<UploadReconciliationHeadResult> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HeadReturnsRetry)
        {
            return ValueTask.FromResult(UploadReconciliationHeadResult.Retry());
        }

        return ValueTask.FromResult(
            _objects.TryGetValue(key, out UploadReconciliationObjectHead? head)
                ? UploadReconciliationHeadResult.Found(head)
                : UploadReconciliationHeadResult.Missing());
    }

    public ValueTask<ReconciliationMultipartState> InspectMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MultipartState);
    }

    public ValueTask<ReconciliationProviderMutationOutcome> AbortMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AbortCalls++;
        if (AbortOutcome == ReconciliationProviderMutationOutcome.Succeeded)
        {
            MultipartState = ReconciliationMultipartState.Aborted;
        }

        return ValueTask.FromResult(AbortOutcome);
    }

    public ValueTask<ReconciliationProviderMutationOutcome> CompleteMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompleteCalls++;
        MultipartState = ReconciliationMultipartState.Completed;
        return ValueTask.FromResult(
            ReconciliationProviderMutationOutcome.Succeeded);
    }

    public ValueTask<ReconciliationProviderMutationOutcome> DeleteStagingAsync(
        BlobIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!identity.Key.Value.StartsWith("staging/", StringComparison.Ordinal))
        {
            DeleteCanonicalAttempts++;
            return ValueTask.FromResult(ReconciliationProviderMutationOutcome.Stale);
        }

        DeleteCalls++;
        if (!_objects.TryGetValue(identity.Key, out UploadReconciliationObjectHead? head))
        {
            return ValueTask.FromResult(ReconciliationProviderMutationOutcome.Missing);
        }

        if (head.Identity.Version != identity.Version)
        {
            return ValueTask.FromResult(ReconciliationProviderMutationOutcome.Stale);
        }

        _objects.Remove(identity.Key);
        return ValueTask.FromResult(ReconciliationProviderMutationOutcome.Succeeded);
    }

    internal void AddStaging(UploadReconciliationCandidate candidate, bool aged)
    {
        DateTimeOffset modified = aged
            ? UploadReconciliationTestsTime.UtcNow.AddHours(-7)
            : UploadReconciliationTestsTime.UtcNow.AddMinutes(-2);
        _objects[candidate.StagingKey] = Head(
            candidate.StagingKey,
            candidate.ExpectedStagingVersion!,
            candidate,
            modified,
            matching: true);
    }

    internal void AddCanonical(
        UploadReconciliationCandidate candidate,
        bool matching) =>
        _objects[candidate.CanonicalKey!] = Head(
            candidate.CanonicalKey!,
            new BlobVersion("canonical-v1"),
            candidate,
            UploadReconciliationTestsTime.UtcNow.AddHours(-2),
            matching);

    internal void RemoveOwnershipMetadata(BlobKey key)
    {
        UploadReconciliationObjectHead head = _objects[key];
        _objects[key] = head with { OwnerTenantId = null, OwnerUploadSessionId = null };
    }

    internal bool Contains(BlobKey key) => _objects.ContainsKey(key);

    internal void Move(BlobKey oldKey, BlobKey newKey)
    {
        UploadReconciliationObjectHead head = _objects[oldKey];
        _objects.Remove(oldKey);
        _objects[newKey] = head with
        {
            Identity = new BlobIdentity(newKey, head.Identity.Version),
        };
    }

    private static UploadReconciliationObjectHead Head(
        BlobKey key,
        BlobVersion version,
        UploadReconciliationCandidate candidate,
        DateTimeOffset modified,
        bool matching) =>
        new(
            new BlobIdentity(key, version),
            modified,
            matching ? candidate.ExpectedSizeBytes : candidate.ExpectedSizeBytes + 1,
            matching ? candidate.ExpectedSha256 : null,
            candidate.Fence.TenantId,
            candidate.Fence.UploadSessionId);
}

internal sealed class TestReconciliationCheckpoints : IUploadReconciliationCheckpointObserver
{
    private bool _crashed;
    private int _itemsStarted;

    internal List<ReconciliationCheckpoint> Reached { get; } = [];

    internal ReconciliationCheckpoint? CrashOnceAt { get; set; }

    internal int? CancelAtItem { get; set; }

    public ValueTask ReachedAsync(
        ReconciliationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        Reached.Add(checkpoint);
        if (checkpoint == ReconciliationCheckpoint.CandidateRevalidated)
        {
            _itemsStarted++;
            if (CancelAtItem == _itemsStarted)
            {
                throw new OperationCanceledException("Injected cancellation.");
            }
        }

        if (!_crashed && CrashOnceAt == checkpoint)
        {
            _crashed = true;
            throw new OperationCanceledException($"Injected crash at {checkpoint}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class TestReconciliationObserver : IUploadReconciliationObserver
{
    internal List<(ReconciliationActionKind Action, ReconciliationActionOutcome Outcome)>
        Records
    { get; } = [];

    public void Record(
        ReconciliationActionKind action,
        ReconciliationActionOutcome outcome) =>
        Records.Add((action, outcome));
}

internal static class UploadReconciliationTestsTime
{
    internal static readonly DateTimeOffset UtcNow =
        new(2026, 8, 29, 2, 0, 0, TimeSpan.Zero);
}
