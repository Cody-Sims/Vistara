namespace Vistara.Domain.Jobs;

public sealed record JobSnapshot(
    JobId Id,
    JobTenantId TenantId,
    JobType Type,
    string Payload,
    int PayloadVersion,
    JobDedupeKey DedupeKey,
    int Priority,
    int MaxAttempts,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset CreatedAtUtc,
    string? TraceParent,
    JobState State,
    int Attempts,
    JobVersion Version,
    JobLease? Lease,
    JobFailure? LastFailure,
    DateTimeOffset? CompletedAtUtc);
