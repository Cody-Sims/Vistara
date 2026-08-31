using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.UnitTests.Jobs;

public sealed class JobRecoveryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset RecoveredAt =
        new(2026, 8, 30, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Recovery_returns_a_dead_lettered_job_to_the_retry_queue()
    {
        DurableJob job = DeadLetter(maxAttempts: 2);
        long version = job.Version.Value;

        Result granted = job.GrantRecoveryAttempts(
            additionalAttempts: 3,
            maximumAttempts: 8,
            RecoveredAt);

        Assert.True(granted.IsSuccess);
        Assert.Equal(JobState.RetryScheduled, job.State);
        Assert.Equal(5, job.MaxAttempts);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(RecoveredAt, job.AvailableAtUtc);
        Assert.Null(job.Lease);
        Assert.Equal(version + 1, job.Version.Value);
    }

    [Fact]
    public void Recovery_can_be_leased_again_after_the_grant()
    {
        DurableJob job = DeadLetter(maxAttempts: 2);
        Assert.True(
            job.GrantRecoveryAttempts(3, 8, RecoveredAt).IsSuccess);

        Result<JobLease> leased = job.TryLease(
            new JobLeaseOwner("worker-b"),
            RecoveredAt,
            TimeSpan.FromMinutes(5));

        Assert.True(leased.IsSuccess);
        Assert.Equal(JobState.Leased, job.State);
    }

    [Fact]
    public void Recovery_never_exceeds_the_configured_attempt_ceiling()
    {
        DurableJob job = DeadLetter(maxAttempts: 2);

        Assert.True(
            job.GrantRecoveryAttempts(5, 4, RecoveredAt).IsSuccess);

        Assert.Equal(4, job.MaxAttempts);
    }

    [Fact]
    public void Recovery_stops_once_the_ceiling_is_reached()
    {
        DurableJob job = DeadLetter(maxAttempts: 4);
        long version = job.Version.Value;

        Result granted = job.GrantRecoveryAttempts(
            additionalAttempts: 3,
            maximumAttempts: 4,
            RecoveredAt);

        Assert.True(granted.IsFailure);
        Assert.Equal("jobs.attempt_limit_reached", granted.Error?.Code);
        Assert.Equal(JobState.DeadLettered, job.State);
        Assert.Equal(version, job.Version.Value);
    }

    [Fact]
    public void Recovery_refuses_jobs_that_are_not_dead_lettered()
    {
        DurableJob job = CreateJob(maxAttempts: 4);

        Result granted = job.GrantRecoveryAttempts(3, 8, RecoveredAt);

        Assert.True(granted.IsFailure);
        Assert.Equal("jobs.invalid_state", granted.Error?.Code);
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(4, job.MaxAttempts);
    }

    [Fact]
    public void Recovery_requires_a_positive_grant_and_a_utc_instant()
    {
        DurableJob job = DeadLetter(maxAttempts: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            job.GrantRecoveryAttempts(0, 8, Now));
        Assert.Throws<ArgumentException>(() =>
            job.GrantRecoveryAttempts(
                3,
                8,
                new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.FromHours(2))));
    }

    private static DurableJob DeadLetter(int maxAttempts)
    {
        DurableJob job = CreateJob(maxAttempts);
        JobLeaseOwner owner = new("worker-a");
        JobRetryPolicy policy = new(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            DateTimeOffset at = Now.AddHours(attempt);
            Assert.True(
                job.TryLease(owner, at, TimeSpan.FromMinutes(5)).IsSuccess);
            Assert.True(
                job.Fail(
                    owner,
                    new JobFailure(JobFailureReason.ProcessingFailed),
                    at.AddSeconds(10),
                    policy).IsSuccess);
        }

        Assert.Equal(JobState.DeadLettered, job.State);
        return job;
    }

    private static DurableJob CreateJob(int maxAttempts) =>
        DurableJob.Create(
            new JobId(Guid.Parse("01990a2a-bc00-7000-8000-0000000000a1")),
            new JobTenantId(Guid.Parse("01990a2a-bc00-7000-8000-0000000000a2")),
            new JobType("asset.derivative.generate"),
            """{"generation":{}}""",
            payloadVersion: 2,
            new JobDedupeKey("derivative:identity"),
            priority: 0,
            maxAttempts,
            availableAtUtc: Now,
            createdAtUtc: Now);
}
