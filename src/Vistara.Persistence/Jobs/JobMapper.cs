using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Persistence.Jobs;

internal static class JobMapper
{
    internal static JobRow ToRow(DurableJob job)
    {
        var row = new JobRow();
        Copy(job, row);
        return row;
    }

    internal static Result<DurableJob> ToDomain(JobRow row)
    {
        JobFailure? failure = row.FailureCode is null ? null : Failure(row.FailureCode);
        if (row.FailureCode is not null && failure is null)
        {
            return Result.Failure<DurableJob>(JobErrors.InvalidSnapshot);
        }

        JobLease? lease = null;
        if (row.LeaseOwner is not null &&
            row.LeaseAcquiredAtUtc.HasValue &&
            row.LeaseExpiresAtUtc.HasValue)
        {
            lease = new JobLease(
                new JobId(row.Id),
                new JobLeaseOwner(row.LeaseOwner),
                row.LeaseAcquiredAtUtc.Value,
                row.LeaseExpiresAtUtc.Value,
                new JobVersion(row.Version));
        }

        if (!Enum.TryParse(row.State, out JobState state))
        {
            return Result.Failure<DurableJob>(JobErrors.InvalidSnapshot);
        }

        try
        {
            return DurableJob.Restore(new JobSnapshot(
                new JobId(row.Id),
                new JobTenantId(row.TenantId),
                new JobType(row.Type),
                row.Payload,
                row.PayloadVersion,
                new JobDedupeKey(row.DedupeKey),
                row.Priority,
                row.MaxAttempts,
                row.AvailableAtUtc,
                row.CreatedAtUtc,
                row.TraceParent,
                state,
                row.Attempts,
                new JobVersion(row.Version),
                lease,
                failure,
                row.CompletedAtUtc));
        }
        catch (ArgumentException)
        {
            return Result.Failure<DurableJob>(JobErrors.InvalidSnapshot);
        }
    }

    internal static void Copy(DurableJob job, JobRow row)
    {
        JobSnapshot snapshot = job.ToSnapshot();
        row.Id = snapshot.Id.Value;
        row.TenantId = snapshot.TenantId.Value;
        row.Type = snapshot.Type.Value;
        row.Payload = snapshot.Payload;
        row.PayloadVersion = snapshot.PayloadVersion;
        row.DedupeKey = snapshot.DedupeKey.Value;
        row.Priority = snapshot.Priority;
        row.MaxAttempts = snapshot.MaxAttempts;
        row.Attempts = snapshot.Attempts;
        row.State = snapshot.State.ToString();
        row.AvailableAtUtc = snapshot.AvailableAtUtc;
        row.CreatedAtUtc = snapshot.CreatedAtUtc;
        row.TraceParent = snapshot.TraceParent;
        row.LeaseOwner = snapshot.Lease?.Owner.Value;
        row.LeaseAcquiredAtUtc = snapshot.Lease?.AcquiredAtUtc;
        row.LeaseHeartbeatAtUtc = snapshot.Lease is null
            ? null
            : row.LeaseHeartbeatAtUtc ?? snapshot.Lease.AcquiredAtUtc;
        row.LeaseExpiresAtUtc = snapshot.Lease?.ExpiresAtUtc;
        row.FailureCode = snapshot.LastFailure?.Code;
        row.CompletedAtUtc = snapshot.CompletedAtUtc;
        row.Version = snapshot.Version.Value;
    }

    private static JobFailure? Failure(string code) =>
        code switch
        {
            "jobs.processing_failed" => new JobFailure(JobFailureReason.ProcessingFailed),
            "jobs.provider_unavailable" => new JobFailure(JobFailureReason.ProviderUnavailable),
            "jobs.provider_rate_limited" => new JobFailure(JobFailureReason.ProviderRateLimited),
            "jobs.provider_authorization_denied" =>
                new JobFailure(JobFailureReason.ProviderAuthorizationDenied),
            "jobs.media_decode_failed" => new JobFailure(JobFailureReason.MediaDecodeFailed),
            "jobs.lease_expired" => new JobFailure(JobFailureReason.LeaseExpired),
            _ => null,
        };
}
