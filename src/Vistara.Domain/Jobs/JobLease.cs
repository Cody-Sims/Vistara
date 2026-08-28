namespace Vistara.Domain.Jobs;

public sealed record JobLease(
    JobId JobId,
    JobLeaseOwner Owner,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc,
    JobVersion JobVersion);
