using Vistara.Domain.Jobs;

namespace Vistara.UnitTests.Jobs;

public sealed class JobSnapshotTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Leased_job_round_trips_through_its_durable_snapshot()
    {
        DurableJob job = CreateJob();
        Assert.True(job.TryLease(
            new JobLeaseOwner("worker-a"),
            Now,
            TimeSpan.FromMinutes(5)).IsSuccess);

        JobSnapshot snapshot = job.ToSnapshot();
        var restoredResult = DurableJob.Restore(snapshot);

        Assert.True(restoredResult.TryGetValue(out DurableJob? restored));
        Assert.NotNull(restored);
        Assert.Equal(job.Id, restored.Id);
        Assert.Equal(job.TenantId, restored.TenantId);
        Assert.Equal(job.State, restored.State);
        Assert.Equal(job.Attempts, restored.Attempts);
        Assert.Equal(job.Version, restored.Version);
        Assert.Equal(job.Lease, restored.Lease);
        Assert.Equal(job.Payload, restored.Payload);
    }

    [Fact]
    public void Invalid_persisted_state_is_rejected_explicitly()
    {
        JobSnapshot invalid = CreateJob().ToSnapshot() with
        {
            State = JobState.Leased,
            Attempts = 1,
            Lease = null,
            Version = new JobVersion(2),
        };

        var result = DurableJob.Restore(invalid);

        Assert.True(result.IsFailure);
        Assert.Equal("jobs.invalid_snapshot", result.Error?.Code);
    }

    private static DurableJob CreateJob() =>
        DurableJob.Create(
            new JobId(Guid.Parse("01990a2a-bc00-7000-8000-000000000051")),
            new JobTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000052")),
            new JobType("derivative.render"),
            """{"revisionId":"r1"}""",
            payloadVersion: 1,
            new JobDedupeKey("tenant-1:derivative:r1:recipe-1"),
            priority: 0,
            maxAttempts: 3,
            availableAtUtc: Now,
            createdAtUtc: Now);
}
