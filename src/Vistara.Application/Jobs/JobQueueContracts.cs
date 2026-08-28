using Vistara.Domain.Jobs;

namespace Vistara.Application.Jobs;

public sealed record JobEnqueueResult(JobId JobId, bool WasCreated);

public sealed record JobLeaseRequest(
    JobLeaseOwner Owner,
    DateTimeOffset NowUtc,
    TimeSpan LeaseDuration,
    int MaximumCount);

public sealed record JobLeaseAssignment(DurableJob Job, JobLease Lease);

public sealed record JobHeartbeatRequest(
    JobId JobId,
    JobLeaseOwner Owner,
    JobVersion ExpectedVersion,
    DateTimeOffset NowUtc,
    TimeSpan LeaseDuration);

public sealed record JobCompletionRequest(
    JobId JobId,
    JobLeaseOwner Owner,
    JobVersion ExpectedVersion,
    DateTimeOffset CompletedAtUtc);

public sealed record JobFailureRequest(
    JobId JobId,
    JobLeaseOwner Owner,
    JobVersion ExpectedVersion,
    JobFailure Failure,
    DateTimeOffset FailedAtUtc,
    JobRetryPolicy RetryPolicy);

public sealed record JobExpiredLeaseRequest(
    JobId JobId,
    JobVersion ExpectedVersion,
    JobFailure Failure,
    DateTimeOffset RecoveredAtUtc,
    JobRetryPolicy RetryPolicy);
