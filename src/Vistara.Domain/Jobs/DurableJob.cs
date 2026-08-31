using Vistara.Domain.Common;

namespace Vistara.Domain.Jobs;

public sealed class DurableJob
{
    private DurableJob(
        JobId id,
        JobTenantId tenantId,
        JobType type,
        string payload,
        int payloadVersion,
        JobDedupeKey dedupeKey,
        int priority,
        int maxAttempts,
        DateTimeOffset availableAtUtc,
        DateTimeOffset createdAtUtc,
        string? traceParent)
    {
        Id = id;
        TenantId = tenantId;
        Type = type;
        Payload = payload;
        PayloadVersion = payloadVersion;
        DedupeKey = dedupeKey;
        Priority = priority;
        MaxAttempts = maxAttempts;
        AvailableAtUtc = availableAtUtc;
        CreatedAtUtc = createdAtUtc;
        TraceParent = traceParent;
        State = JobState.Pending;
        Version = new JobVersion(1);
    }

    public JobId Id { get; }

    public JobTenantId TenantId { get; }

    public JobType Type { get; }

    public string Payload { get; }

    public int PayloadVersion { get; }

    public JobDedupeKey DedupeKey { get; }

    public JobDedupeIdentity DedupeIdentity => new(TenantId, DedupeKey);

    public int Priority { get; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset AvailableAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string? TraceParent { get; }

    public JobState State { get; private set; }

    public int Attempts { get; private set; }

    public JobVersion Version { get; private set; }

    public JobLease? Lease { get; private set; }

    public JobFailure? LastFailure { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static DurableJob Create(
        JobId id,
        JobTenantId tenantId,
        JobType type,
        string payload,
        int payloadVersion,
        JobDedupeKey dedupeKey,
        int priority,
        int maxAttempts,
        DateTimeOffset availableAtUtc,
        DateTimeOffset createdAtUtc,
        string? traceParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (payloadVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadVersion),
                "Payload version must be positive.");
        }

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts),
                "Maximum attempts must be positive.");
        }

        EnsureUtc(availableAtUtc, nameof(availableAtUtc));
        EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        if (availableAtUtc < createdAtUtc)
        {
            throw new ArgumentException(
                "Availability cannot precede creation.",
                nameof(availableAtUtc));
        }

        return new DurableJob(
            id,
            tenantId,
            type,
            payload,
            payloadVersion,
            dedupeKey,
            priority,
            maxAttempts,
            availableAtUtc,
            createdAtUtc,
            traceParent);
    }

    public static Result<DurableJob> Restore(JobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        DurableJob job;
        try
        {
            job = Create(
                snapshot.Id,
                snapshot.TenantId,
                snapshot.Type,
                snapshot.Payload,
                snapshot.PayloadVersion,
                snapshot.DedupeKey,
                snapshot.Priority,
                snapshot.MaxAttempts,
                snapshot.AvailableAtUtc,
                snapshot.CreatedAtUtc,
                snapshot.TraceParent);
        }
        catch (ArgumentException)
        {
            return Result.Failure<DurableJob>(JobErrors.InvalidSnapshot);
        }

        if (!IsValidSnapshot(snapshot))
        {
            return Result.Failure<DurableJob>(JobErrors.InvalidSnapshot);
        }

        job.State = snapshot.State;
        job.Attempts = snapshot.Attempts;
        job.Version = snapshot.Version;
        job.Lease = snapshot.Lease;
        job.LastFailure = snapshot.LastFailure;
        job.CompletedAtUtc = snapshot.CompletedAtUtc;
        return Result.Success(job);
    }

    public JobSnapshot ToSnapshot() =>
        new(
            Id,
            TenantId,
            Type,
            Payload,
            PayloadVersion,
            DedupeKey,
            Priority,
            MaxAttempts,
            AvailableAtUtc,
            CreatedAtUtc,
            TraceParent,
            State,
            Attempts,
            Version,
            Lease,
            LastFailure,
            CompletedAtUtc);

    public Result<JobLease> TryLease(
        JobLeaseOwner owner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        EnsurePositiveDuration(leaseDuration, nameof(leaseDuration));

        if (State is JobState.Completed or JobState.DeadLettered)
        {
            return Result.Failure<JobLease>(JobErrors.InvalidState);
        }

        if (State == JobState.Leased && Lease is not null && nowUtc < Lease.ExpiresAtUtc)
        {
            return Result.Failure<JobLease>(JobErrors.LeaseConflict);
        }

        if (State != JobState.Leased && nowUtc < AvailableAtUtc)
        {
            return Result.Failure<JobLease>(JobErrors.NotAvailable);
        }

        if (Attempts >= MaxAttempts)
        {
            return Result.Failure<JobLease>(JobErrors.AttemptLimitReached);
        }

        Attempts++;
        State = JobState.Leased;
        Version = Version.Next();
        Lease = new JobLease(
            Id,
            owner,
            nowUtc,
            nowUtc.Add(leaseDuration),
            Version);
        return Result.Success(Lease);
    }

    public Result Heartbeat(
        JobLeaseOwner owner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        EnsurePositiveDuration(leaseDuration, nameof(leaseDuration));

        Result? leaseCheck = ValidateActiveLease(owner, nowUtc);
        if (leaseCheck is not null)
        {
            return leaseCheck;
        }

        Version = Version.Next();
        Lease = Lease! with
        {
            ExpiresAtUtc = Max(Lease.ExpiresAtUtc, nowUtc.Add(leaseDuration)),
            JobVersion = Version,
        };
        return Result.Success();
    }

    public Result Complete(JobLeaseOwner owner, DateTimeOffset completedAtUtc)
    {
        EnsureUtc(completedAtUtc, nameof(completedAtUtc));
        if (State == JobState.Completed)
        {
            return Result.Success();
        }

        Result? leaseCheck = ValidateActiveLease(owner, completedAtUtc);
        if (leaseCheck is not null)
        {
            return leaseCheck;
        }

        State = JobState.Completed;
        CompletedAtUtc = completedAtUtc;
        Lease = null;
        Version = Version.Next();
        return Result.Success();
    }

    public Result Fail(
        JobLeaseOwner owner,
        JobFailure failure,
        DateTimeOffset failedAtUtc,
        JobRetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        EnsureUtc(failedAtUtc, nameof(failedAtUtc));

        Result? leaseCheck = ValidateActiveLease(owner, failedAtUtc);
        if (leaseCheck is not null)
        {
            return leaseCheck;
        }

        ScheduleAfterFailure(failure, failedAtUtc, retryPolicy);
        return Result.Success();
    }

    public Result RecoverExpiredLease(
        JobFailure failure,
        DateTimeOffset recoveredAtUtc,
        JobRetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        EnsureUtc(recoveredAtUtc, nameof(recoveredAtUtc));

        if (State != JobState.Leased || Lease is null)
        {
            return Result.Failure(JobErrors.InvalidState);
        }

        if (recoveredAtUtc < Lease.ExpiresAtUtc)
        {
            return Result.Failure(JobErrors.LeaseNotExpired);
        }

        ScheduleAfterFailure(failure, recoveredAtUtc, retryPolicy);
        return Result.Success();
    }

    /// <summary>
    /// Returns a dead-lettered job to the retry queue with an explicitly
    /// granted attempt budget. Recovery is bounded by
    /// <paramref name="maximumAttempts"/> so a permanently failing job cannot
    /// be revived forever, and the version advances so concurrent recovery
    /// attempts fence against each other.
    /// </summary>
    public Result GrantRecoveryAttempts(
        int additionalAttempts,
        int maximumAttempts,
        DateTimeOffset recoveredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additionalAttempts);
        EnsureUtc(recoveredAtUtc, nameof(recoveredAtUtc));

        if (State != JobState.DeadLettered)
        {
            return Result.Failure(JobErrors.InvalidState);
        }

        if (MaxAttempts >= maximumAttempts)
        {
            return Result.Failure(JobErrors.AttemptLimitReached);
        }

        MaxAttempts = Math.Min(MaxAttempts + additionalAttempts, maximumAttempts);
        State = JobState.RetryScheduled;
        AvailableAtUtc = recoveredAtUtc;
        Lease = null;
        Version = Version.Next();
        return Result.Success();
    }

    private void ScheduleAfterFailure(
        JobFailure failure,
        DateTimeOffset failedAtUtc,
        JobRetryPolicy retryPolicy)
    {
        LastFailure = failure;
        Lease = null;
        Version = Version.Next();

        if (Attempts >= MaxAttempts)
        {
            State = JobState.DeadLettered;
            return;
        }

        State = JobState.RetryScheduled;
        AvailableAtUtc = failedAtUtc.Add(retryPolicy.GetDelay(Attempts));
    }

    private Result? ValidateActiveLease(JobLeaseOwner owner, DateTimeOffset nowUtc)
    {
        if (State != JobState.Leased || Lease is null)
        {
            return Result.Failure(JobErrors.InvalidState);
        }

        if (Lease.Owner != owner)
        {
            return Result.Failure(JobErrors.LeaseConflict);
        }

        if (nowUtc >= Lease.ExpiresAtUtc)
        {
            return Result.Failure(JobErrors.LeaseExpired);
        }

        return null;
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }

    private static void EnsurePositiveDuration(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Duration must be positive.");
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static bool IsValidSnapshot(JobSnapshot snapshot)
    {
        if (!Enum.IsDefined(snapshot.State) ||
            snapshot.Attempts < 0 ||
            snapshot.Attempts > snapshot.MaxAttempts ||
            snapshot.Version.Value < 1 ||
            !IsUtc(snapshot.CompletedAtUtc))
        {
            return false;
        }

        if (snapshot.LastFailure is not null && !snapshot.LastFailure.IsValid())
        {
            return false;
        }

        bool hasValidLease =
            snapshot.Lease is not null &&
            snapshot.Lease.JobId == snapshot.Id &&
            snapshot.Lease.JobVersion == snapshot.Version &&
            snapshot.Lease.AcquiredAtUtc.Offset == TimeSpan.Zero &&
            snapshot.Lease.ExpiresAtUtc.Offset == TimeSpan.Zero &&
            snapshot.Lease.ExpiresAtUtc > snapshot.Lease.AcquiredAtUtc;

        return snapshot.State switch
        {
            JobState.Pending =>
                snapshot.Attempts == 0 &&
                snapshot.Lease is null &&
                snapshot.LastFailure is null &&
                snapshot.CompletedAtUtc is null,
            JobState.Leased =>
                snapshot.Attempts > 0 &&
                hasValidLease &&
                snapshot.CompletedAtUtc is null,
            JobState.RetryScheduled =>
                snapshot.Attempts > 0 &&
                snapshot.Attempts < snapshot.MaxAttempts &&
                snapshot.Lease is null &&
                snapshot.LastFailure is not null &&
                snapshot.CompletedAtUtc is null,
            JobState.Completed =>
                snapshot.Attempts > 0 &&
                snapshot.Lease is null &&
                snapshot.CompletedAtUtc is not null,
            JobState.DeadLettered =>
                snapshot.Attempts == snapshot.MaxAttempts &&
                snapshot.Lease is null &&
                snapshot.LastFailure is not null &&
                snapshot.CompletedAtUtc is null,
            _ => false,
        };
    }

    private static bool IsUtc(DateTimeOffset? value) =>
        value is null || value.Value.Offset == TimeSpan.Zero;
}
