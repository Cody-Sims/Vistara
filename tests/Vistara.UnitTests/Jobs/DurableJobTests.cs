using Vistara.Domain.Jobs;

namespace Vistara.UnitTests.Jobs;

public sealed class DurableJobTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_job_is_pending_and_available_without_a_lease()
    {
        DurableJob job = CreateJob();

        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(new JobVersion(1), job.Version);
        Assert.Null(job.Lease);
        Assert.Equal(Now, job.AvailableAtUtc);
    }

    [Fact]
    public void Active_lease_rejects_a_different_owner()
    {
        DurableJob job = CreateJob();
        JobLeaseOwner firstOwner = new("worker-a");
        JobLeaseOwner secondOwner = new("worker-b");
        Assert.True(job.TryLease(firstOwner, Now, TimeSpan.FromMinutes(5)).IsSuccess);

        var conflict = job.TryLease(secondOwner, Now.AddMinutes(1), TimeSpan.FromMinutes(5));

        Assert.True(conflict.IsFailure);
        Assert.Equal("jobs.lease_conflict", conflict.Error?.Code);
        Assert.Equal(firstOwner, job.Lease?.Owner);
    }

    [Fact]
    public void Expired_lease_can_be_acquired_by_another_owner()
    {
        DurableJob job = CreateJob(maxAttempts: 3);
        Assert.True(job.TryLease(
            new JobLeaseOwner("worker-a"),
            Now,
            TimeSpan.FromMinutes(5)).IsSuccess);

        var reacquired = job.TryLease(
            new JobLeaseOwner("worker-b"),
            Now.AddMinutes(5),
            TimeSpan.FromMinutes(2));

        Assert.True(reacquired.TryGetValue(out JobLease? lease));
        Assert.Equal(new JobLeaseOwner("worker-b"), lease.Owner);
        Assert.Equal(Now.AddMinutes(7), lease.ExpiresAtUtc);
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public void Heartbeat_extends_only_the_current_unexpired_owners_lease()
    {
        DurableJob job = CreateJob();
        JobLeaseOwner owner = new("worker-a");
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);

        var heartbeat = job.Heartbeat(
            owner,
            Now.AddMinutes(2),
            TimeSpan.FromMinutes(5));

        Assert.True(heartbeat.IsSuccess);
        Assert.Equal(Now.AddMinutes(7), job.Lease?.ExpiresAtUtc);

        var staleOwner = job.Heartbeat(
            new JobLeaseOwner("worker-b"),
            Now.AddMinutes(3),
            TimeSpan.FromMinutes(5));

        Assert.True(staleOwner.IsFailure);
        Assert.Equal("jobs.lease_conflict", staleOwner.Error?.Code);
    }

    [Fact]
    public void Heartbeat_after_expiry_is_rejected()
    {
        DurableJob job = CreateJob();
        JobLeaseOwner owner = new("worker-a");
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);

        var result = job.Heartbeat(
            owner,
            Now.AddMinutes(5),
            TimeSpan.FromMinutes(5));

        Assert.True(result.IsFailure);
        Assert.Equal("jobs.lease_expired", result.Error?.Code);
    }

    [Fact]
    public void Heartbeat_never_shortens_an_existing_lease()
    {
        DurableJob job = CreateJob();
        JobLeaseOwner owner = new("worker-a");
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(10)).IsSuccess);

        var result = job.Heartbeat(
            owner,
            Now.AddMinutes(1),
            TimeSpan.FromMinutes(2));

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(10), job.Lease?.ExpiresAtUtc);
    }

    [Fact]
    public void Completion_is_idempotent_and_does_not_change_version_twice()
    {
        DurableJob job = CreateJob();
        JobLeaseOwner owner = new("worker-a");
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);
        Assert.True(job.Complete(owner, Now.AddMinutes(1)).IsSuccess);
        JobVersion completedVersion = job.Version;

        var repeated = job.Complete(owner, Now.AddMinutes(2));

        Assert.True(repeated.IsSuccess);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(completedVersion, job.Version);
        Assert.Equal(Now.AddMinutes(1), job.CompletedAtUtc);
    }

    [Fact]
    public void Pending_job_cannot_complete_without_a_lease()
    {
        DurableJob job = CreateJob();

        var result = job.Complete(new JobLeaseOwner("worker-a"), Now);

        Assert.True(result.IsFailure);
        Assert.Equal("jobs.invalid_state", result.Error?.Code);
        Assert.Equal(JobState.Pending, job.State);
    }

    private static DurableJob CreateJob(int maxAttempts = 5) =>
        DurableJob.Create(
            new JobId(Guid.Parse("01990a2a-bc00-7000-8000-000000000001")),
            new JobTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000002")),
            new JobType("derivative.render"),
            """{"revisionId":"asset-revision-1","credential":"not-for-errors"}""",
            payloadVersion: 1,
            new JobDedupeKey("tenant-1:derivative:asset-revision-1:recipe-1"),
            priority: 10,
            maxAttempts,
            availableAtUtc: Now,
            createdAtUtc: Now,
            traceParent: "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01");
}
