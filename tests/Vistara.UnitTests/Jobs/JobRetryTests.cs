using Vistara.Domain.Jobs;

namespace Vistara.UnitTests.Jobs;

public sealed class JobRetryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retry_delay_grows_exponentially_and_is_bounded()
    {
        JobRetryPolicy policy = new(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(1), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(4), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.GetDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.GetDelay(30));
    }

    [Fact]
    public void Failed_attempt_is_scheduled_for_retry_before_the_attempt_limit()
    {
        DurableJob job = CreateJob(maxAttempts: 2);
        JobLeaseOwner owner = new("worker-a");
        JobRetryPolicy policy = new(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);

        var result = job.Fail(
            owner,
            new JobFailure(JobFailureReason.MediaDecodeFailed),
            Now.AddSeconds(10),
            policy);

        Assert.True(result.IsSuccess);
        Assert.Equal(JobState.RetryScheduled, job.State);
        Assert.Equal(Now.AddMinutes(1).AddSeconds(10), job.AvailableAtUtc);
        Assert.Equal("jobs.media_decode_failed", job.LastFailure?.Code);
        Assert.Equal("The media could not be decoded.", job.LastFailure?.Summary);
        Assert.Null(job.Lease);
    }

    [Fact]
    public void Last_allowed_failed_attempt_moves_job_to_dead_letter()
    {
        DurableJob job = CreateJob(maxAttempts: 2);
        JobLeaseOwner owner = new("worker-a");
        JobRetryPolicy policy = new(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);
        Assert.True(job.Fail(
            owner,
            new JobFailure(JobFailureReason.ProviderUnavailable),
            Now.AddSeconds(10),
            policy).IsSuccess);
        Assert.True(job.TryLease(
            owner,
            job.AvailableAtUtc,
            TimeSpan.FromMinutes(5)).IsSuccess);

        var result = job.Fail(
            owner,
            new JobFailure(JobFailureReason.ProcessingFailed),
            job.AvailableAtUtc.AddSeconds(10),
            policy);

        Assert.True(result.IsSuccess);
        Assert.Equal(JobState.DeadLettered, job.State);
        Assert.Equal("jobs.processing_failed", job.LastFailure?.Code);
        Assert.Null(job.Lease);
    }

    [Fact]
    public void Typed_authorization_failure_reason_is_safe_to_persist()
    {
        DurableJob job = CreateJob(maxAttempts: 2);
        JobLeaseOwner owner = new("worker-a");
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);

        var result = job.Fail(
            owner,
            new JobFailure(JobFailureReason.ProviderAuthorizationDenied),
            Now.AddSeconds(1),
            new JobRetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "jobs.provider_authorization_denied",
            job.LastFailure?.Code);
        Assert.Equal(
            "Authorization was denied by the configured provider.",
            job.LastFailure?.Summary);
    }

    [Fact]
    public void Arbitrary_opaque_values_cannot_enter_persisted_failure_state()
    {
        string opaqueSecret = new('x', 192);
        JobFailure failure = new(JobFailureReason.ProviderUnavailable);

        Assert.DoesNotContain(
            typeof(JobFailure).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(string));
        Assert.All(
            typeof(JobFailure).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
        Assert.DoesNotContain(opaqueSecret, failure.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(opaqueSecret, failure.Summary, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JobFailure((JobFailureReason)999));
    }

    [Fact]
    public void Expired_final_attempt_is_recovered_to_dead_letter()
    {
        DurableJob job = CreateJob(maxAttempts: 1);
        JobLeaseOwner owner = new("worker-a");
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);

        var result = job.RecoverExpiredLease(
            new JobFailure(JobFailureReason.LeaseExpired),
            Now.AddMinutes(5),
            new JobRetryPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));

        Assert.True(result.IsSuccess);
        Assert.Equal(JobState.DeadLettered, job.State);
        Assert.Null(job.Lease);
        Assert.Equal("jobs.lease_expired", job.LastFailure?.Code);
    }

    [Fact]
    public void Active_lease_cannot_be_recovered_as_expired()
    {
        DurableJob job = CreateJob(maxAttempts: 2);
        JobLeaseOwner owner = new("worker-a");
        Assert.True(job.TryLease(owner, Now, TimeSpan.FromMinutes(5)).IsSuccess);

        var result = job.RecoverExpiredLease(
            new JobFailure(JobFailureReason.LeaseExpired),
            Now.AddMinutes(4),
            new JobRetryPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));

        Assert.True(result.IsFailure);
        Assert.Equal("jobs.lease_not_expired", result.Error?.Code);
        Assert.Equal(JobState.Leased, job.State);
    }

    private static DurableJob CreateJob(int maxAttempts) =>
        DurableJob.Create(
            new JobId(Guid.Parse("01990a2a-bc00-7000-8000-000000000011")),
            new JobTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000012")),
            new JobType("derivative.render"),
            """{"revisionId":"r1"}""",
            payloadVersion: 1,
            new JobDedupeKey("tenant-1:derivative:r1:recipe-1"),
            priority: 0,
            maxAttempts,
            availableAtUtc: Now,
            createdAtUtc: Now);
}
