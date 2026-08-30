using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Derivatives;
using Vistara.Application.Jobs;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

public sealed class JobQueueTests
{
    private static readonly string[] StandardDerivativePresets =
        ["thumb", "grid", "viewer", "download-web"];

    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    internal static readonly JobTenantId DefaultTenantId =
        new(Guid.Parse("01991f9e-522b-7c80-a109-7f764ae57985"));

    [Fact]
    public async Task JobQueue_enqueue_deduplicates_within_tenant()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        DurableJob first = CreateJob("same-key", tenantId: tenantId);
        DurableJob duplicate = CreateJob("same-key", tenantId: tenantId);

        JobEnqueueResult created = Required(await queue.EnqueueAsync(first, default));
        JobEnqueueResult existing = Required(await queue.EnqueueAsync(duplicate, default));

        Assert.True(created.WasCreated);
        Assert.False(existing.WasCreated);
        Assert.Equal(first.Id, existing.JobId);
        Assert.Equal(1, await database.CountAsync());
    }

    [Fact]
    public async Task JobQueue_concurrent_enqueue_creates_one_tenant_dedupe_identity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId tenantId = DefaultTenantId;
        RelationalJobQueue firstQueue = database.CreateQueue();
        RelationalJobQueue secondQueue = database.CreateQueue();

        JobEnqueueResult[] results = await Task.WhenAll(
            Enqueue(firstQueue, CreateJob("racing-key", tenantId: tenantId)),
            Enqueue(secondQueue, CreateJob("racing-key", tenantId: tenantId)));

        Assert.Single(results, result => result.WasCreated);
        Assert.Equal(results[0].JobId, results[1].JobId);
        Assert.Equal(1, await database.CountAsync());
    }

    [Fact]
    public async Task JobQueue_concurrent_claims_never_assign_a_job_twice()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue enqueueQueue = database.CreateQueue();
        for (int index = 0; index < 12; index++)
        {
            Required(await enqueueQueue.EnqueueAsync(CreateJob($"job-{index}"), default));
        }

        RelationalJobQueue firstQueue = database.CreateQueue();
        RelationalJobQueue secondQueue = database.CreateQueue();
        Task<IReadOnlyList<JobLeaseAssignment>> first = Lease(firstQueue, "worker-1", 8);
        Task<IReadOnlyList<JobLeaseAssignment>> second = Lease(secondQueue, "worker-2", 8);
        IReadOnlyList<JobLeaseAssignment>[] claimed = await Task.WhenAll(first, second);

        JobId[] ids = claimed.SelectMany(items => items).Select(item => item.Job.Id).ToArray();
        Assert.Equal(12, ids.Length);
        Assert.Equal(12, ids.Distinct().Count());
    }

    [Fact]
    public async Task JobQueue_claims_only_the_explicit_tenant_scope()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId firstTenant = DefaultTenantId;
        var secondTenant = new JobTenantId(Guid.CreateVersion7());
        DurableJob first = CreateJob("same-cross-tenant", tenantId: firstTenant);
        DurableJob second = CreateJob("same-cross-tenant", tenantId: secondTenant);
        Required(await database.CreateQueue(firstTenant).EnqueueAsync(first, default));
        Required(await database.CreateQueue(secondTenant).EnqueueAsync(second, default));

        JobLeaseAssignment firstClaim = Assert.Single(
            Required(await database.CreateQueue(firstTenant).LeaseAsync(
                Request("first-worker", UtcNow, maximumCount: 10),
                default)));
        JobLeaseAssignment secondClaim = Assert.Single(
            Required(await database.CreateQueue(secondTenant).LeaseAsync(
                Request("second-worker", UtcNow, maximumCount: 10),
                default)));

        Assert.Equal(first.Id, firstClaim.Job.Id);
        Assert.Equal(firstTenant, firstClaim.Job.TenantId);
        Assert.Equal(second.Id, secondClaim.Job.Id);
        Assert.Equal(secondTenant, secondClaim.Job.TenantId);
    }

    [Fact]
    public async Task JobQueue_rejects_enqueue_outside_the_explicit_tenant_scope()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        var otherTenant = new JobTenantId(Guid.CreateVersion7());
        DurableJob job = CreateJob(
            "wrong-tenant",
            tenantId: otherTenant);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await database.CreateQueue().EnqueueAsync(
                    job,
                    CancellationToken.None));

        Assert.Contains(
            "tenant scope",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await database.CountAsync());
    }

    [Fact]
    public async Task JobQueue_heartbeat_advances_fence_and_rejects_stale_completion()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        DurableJob job = CreateJob("heartbeat");
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));

        JobLease heartbeat = Required(await queue.HeartbeatAsync(
            new JobHeartbeatRequest(
                job.Id,
                new JobLeaseOwner("worker"),
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(10),
                TimeSpan.FromMinutes(1)),
            default));
        JobRow heartbeated = await database.SingleAsync();
        var stale = await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                new JobLeaseOwner("worker"),
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(20)),
            default);
        var current = await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                new JobLeaseOwner("worker"),
                heartbeat.JobVersion,
                UtcNow.AddSeconds(20)),
            default);

        Assert.Equal(UtcNow.AddSeconds(10), heartbeated.LeaseHeartbeatAtUtc);
        Assert.True(stale.IsFailure);
        Assert.Equal("jobs.lease_conflict", stale.Error?.Code);
        Assert.True(current.IsSuccess);
    }

    [Fact]
    public async Task JobQueue_recovers_expired_lease_and_fences_old_worker()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        DurableJob job = CreateJob("expired", maxAttempts: 3);
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("old", UtcNow), default)));
        DateTimeOffset recoveredAt = UtcNow.AddMinutes(2);

        var recovery = await queue.RecoverExpiredAsync(
            new JobExpiredLeaseRequest(
                job.Id,
                assignment.Lease.JobVersion,
                new JobFailure(JobFailureReason.LeaseExpired),
                recoveredAt,
                new JobRetryPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1))),
            default);
        var stale = await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                new JobLeaseOwner("old"),
                assignment.Lease.JobVersion,
                recoveredAt),
            default);
        IReadOnlyList<JobLeaseAssignment> replacement = Required(await queue.LeaseAsync(
            Request("new", recoveredAt.AddSeconds(5)),
            default));

        Assert.True(recovery.IsSuccess);
        Assert.True(stale.IsFailure);
        Assert.Single(replacement);
        Assert.Equal(new JobLeaseOwner("new"), replacement[0].Lease.Owner);
    }

    [Fact]
    public async Task JobQueue_claim_atomically_reclaims_expired_lease_and_fences_old_worker()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        DurableJob job = CreateJob("claim-expired", maxAttempts: 3);
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment abandoned = Assert.Single(
            Required(await queue.LeaseAsync(Request("old", UtcNow), default)));
        DateTimeOffset reclaimedAt = UtcNow.AddMinutes(2);

        JobLeaseAssignment reclaimed = Assert.Single(
            Required(await queue.LeaseAsync(Request("new", reclaimedAt), default)));
        var stale = await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                abandoned.Lease.Owner,
                abandoned.Lease.JobVersion,
                reclaimedAt),
            default);

        Assert.Equal(job.Id, reclaimed.Job.Id);
        Assert.Equal(new JobLeaseOwner("new"), reclaimed.Lease.Owner);
        Assert.Equal(2, reclaimed.Job.Attempts);
        Assert.True(stale.IsFailure);
        Assert.Equal(JobErrors.LeaseConflict.Code, stale.Error?.Code);
    }

    [Fact]
    public async Task JobQueue_claim_dead_letters_expired_final_attempt()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        DurableJob job = CreateJob("claim-expired-final", maxAttempts: 1);
        Required(await queue.EnqueueAsync(job, default));
        _ = Assert.Single(
            Required(await queue.LeaseAsync(Request("old", UtcNow), default)));

        IReadOnlyList<JobLeaseAssignment> claimed = Required(
            await queue.LeaseAsync(Request("new", UtcNow.AddMinutes(2)), default));
        JobRow persisted = await database.SingleAsync();

        Assert.Empty(claimed);
        Assert.Equal(JobState.DeadLettered.ToString(), persisted.State);
        Assert.Equal("jobs.lease_expired", persisted.FailureCode);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task JobQueue_retries_then_dead_letters_with_typed_failure_code_only()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        DurableJob job = CreateJob("dead-letter", maxAttempts: 2);
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment first = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));
        var policy = new JobRetryPolicy(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1));

        Assert.True((await queue.FailAsync(
            new JobFailureRequest(
                job.Id,
                first.Lease.Owner,
                first.Lease.JobVersion,
                new JobFailure(JobFailureReason.ProviderUnavailable),
                UtcNow.AddSeconds(1),
                policy),
            default)).IsSuccess);
        JobLeaseAssignment second = Assert.Single(Required(await queue.LeaseAsync(
            Request("worker", UtcNow.AddSeconds(4)),
            default)));
        Assert.True((await queue.FailAsync(
            new JobFailureRequest(
                job.Id,
                second.Lease.Owner,
                second.Lease.JobVersion,
                new JobFailure(JobFailureReason.MediaDecodeFailed),
                UtcNow.AddSeconds(5),
                policy),
            default)).IsSuccess);

        JobRow persisted = await database.SingleAsync();
        Assert.Equal(JobState.DeadLettered.ToString(), persisted.State);
        Assert.Equal("jobs.media_decode_failed", persisted.FailureCode);
        Assert.DoesNotContain("media could not", persisted.FailureCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JobQueue_sqlite_rejects_multiworker_configuration()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var tenantScope = new FixedTenantScope(DefaultTenantId.Value);
        using var context = new JobDbContext(options, tenantScope);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new RelationalJobQueue(
                context,
                new JobQueueOptions { ConfiguredWorkerCount = 2 }));

        Assert.Contains("single worker", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JobQueue_postgresql_claim_sql_uses_skip_locked()
    {
        Assert.Contains(
            "FOR UPDATE SKIP LOCKED",
            PostgreSqlJobClaimSql.Statement,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "state = 'Leased'",
            PostgreSqlJobClaimSql.Statement,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "lease_expires_at_utc <=",
            PostgreSqlJobClaimSql.Statement,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "tenant_id = {0}",
            PostgreSqlJobClaimSql.Statement,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JobQueue_cancelled_claim_does_not_change_persisted_job()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        DurableJob job = CreateJob("cancelled");
        Required(await queue.EnqueueAsync(job, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await queue.LeaseAsync(
                Request("worker", UtcNow),
                cancellation.Token));

        JobRow persisted = await database.SingleAsync();
        Assert.Equal(JobState.Pending.ToString(), persisted.State);
        Assert.Equal(0, persisted.Attempts);
    }

    [Fact]
    public async Task JobQueue_completion_is_idempotent_after_success()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        DurableJob job = CreateJob("idempotent");
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));
        var request = new JobCompletionRequest(
            job.Id,
            assignment.Lease.Owner,
            assignment.Lease.JobVersion,
            UtcNow.AddSeconds(1));

        Assert.True((await queue.CompleteAsync(request, default)).IsSuccess);
        Assert.True((await queue.CompleteAsync(request, default)).IsSuccess);
    }

    [Fact]
    public async Task JobQueue_completed_upload_job_releases_committed_capacity_once()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        Guid uploadSessionId = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            uploadSessionId,
            state: "Consumed");
        DurableJob job = CreateJob(
            $"upload:{uploadSessionId:D}:ingest:v1",
            tenantId: tenantId,
            type: new JobType("upload.ingest"),
            payload: $$"""{"uploadSessionId":"{{uploadSessionId:D}}"}""");
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));
        var request = new JobCompletionRequest(
            job.Id,
            assignment.Lease.Owner,
            assignment.Lease.JobVersion,
            UtcNow.AddSeconds(1));

        Assert.True((await queue.CompleteAsync(request, default)).IsSuccess);
        Assert.True((await queue.CompleteAsync(request, default)).IsSuccess);

        Assert.Equal(0, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(1, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_retry_keeps_capacity_until_derivative_dead_letters()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        Guid revisionId = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: revisionId);
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            revisionId,
            "thumb");
        DurableJob job = CreateJob(
            DerivativeJobContract.CreateDedupeKey(payload).Value,
            maxAttempts: 2,
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment first = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));
        var policy = new JobRetryPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.True((await queue.FailAsync(
            new JobFailureRequest(
                job.Id,
                first.Lease.Owner,
                first.Lease.JobVersion,
                new JobFailure(JobFailureReason.ProcessingFailed),
                UtcNow.AddSeconds(1),
                policy),
            default)).IsSuccess);
        Assert.Equal(1, await database.CommittedJobsAsync(tenantId));

        JobLeaseAssignment second = Assert.Single(
            Required(await queue.LeaseAsync(
                Request("worker", UtcNow.AddSeconds(2)),
                default)));
        Assert.True((await queue.FailAsync(
            new JobFailureRequest(
                job.Id,
                second.Lease.Owner,
                second.Lease.JobVersion,
                new JobFailure(JobFailureReason.ProcessingFailed),
                UtcNow.AddSeconds(3),
                policy),
            default)).IsSuccess);

        Assert.Equal(0, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(1, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_expired_final_derivative_lease_releases_committed_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        Guid revisionId = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: revisionId);
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            revisionId,
            "thumb");
        DurableJob job = CreateJob(
            DerivativeJobContract.CreateDedupeKey(payload).Value,
            maxAttempts: 1,
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        Required(await queue.EnqueueAsync(job, default));
        _ = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));

        Assert.Empty(Required(await queue.LeaseAsync(
            Request("replacement", UtcNow.AddMinutes(2)),
            default)));

        Assert.Equal(0, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(1, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_non_upload_job_does_not_consume_upload_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        await database.SeedCommittedJobsAsync(tenantId, 1);
        DurableJob job = CreateJob("unaccounted", tenantId: tenantId);
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));

        Assert.True((await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(1)),
            default)).IsSuccess);

        Assert.Equal(1, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(0, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_rejected_ingest_does_not_steal_another_uploads_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        Guid consumedUpload = Guid.CreateVersion7();
        Guid rejectedUpload = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            consumedUpload,
            state: "Consumed");
        await database.SeedUploadCorrelationAsync(
            tenantId,
            rejectedUpload,
            state: "Released");
        DurableJob job = CreateJob(
            "rejected-ingest",
            tenantId: tenantId,
            type: new JobType("upload.ingest"),
            payload: $$"""{"uploadSessionId":"{{rejectedUpload:D}}"}""");
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));

        Assert.True((await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(1)),
            default)).IsSuccess);

        Assert.Equal(1, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(0, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_unrelated_derivative_does_not_steal_upload_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: Guid.CreateVersion7());
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "thumb");
        DurableJob job = CreateJob(
            "unrelated-derivative",
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));

        Assert.True((await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(1)),
            default)).IsSuccess);

        Assert.Equal(1, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(0, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_nonstandard_derivative_does_not_consume_standard_job_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        Guid revisionId = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: revisionId);
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            revisionId,
            "custom");
        DurableJob job = CreateJob(
            "nonstandard-derivative",
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));

        Assert.True((await queue.CompleteAsync(
            new JobCompletionRequest(
                job.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(1)),
            default)).IsSuccess);

        Assert.Equal(1, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(0, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_duplicate_derivative_identity_cannot_release_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        RelationalJobQueue queue = database.CreateQueue();
        JobTenantId tenantId = DefaultTenantId;
        Guid revisionId = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: revisionId);
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            revisionId,
            "thumb");
        DurableJob duplicate = CreateJob(
            "forged-duplicate-derivative",
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        Required(await queue.EnqueueAsync(duplicate, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("worker", UtcNow), default)));

        Assert.True((await queue.CompleteAsync(
            new JobCompletionRequest(
                duplicate.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(1)),
            default)).IsSuccess);

        Assert.Equal(1, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(0, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_two_uploads_concurrent_terminals_release_only_owned_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId tenantId = DefaultTenantId;
        Guid activeUpload = Guid.CreateVersion7();
        Guid rejectedUpload = Guid.CreateVersion7();
        Guid activeRevision = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 5);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            activeUpload,
            state: "Consumed",
            activatedRevisionId: activeRevision);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            rejectedUpload,
            state: "Released");
        var derivativePayload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            activeRevision,
            "thumb");
        DurableJob activeDerivative = CreateJob(
            DerivativeJobContract.CreateDedupeKey(derivativePayload).Value,
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(derivativePayload));
        DurableJob rejectedIngest = CreateJob(
            $"upload:{rejectedUpload:D}:ingest:v1",
            tenantId: tenantId,
            type: new JobType("upload.ingest"),
            payload: $$"""{"uploadSessionId":"{{rejectedUpload:D}}"}""");
        RelationalJobQueue enqueue = database.CreateQueue();
        Required(await enqueue.EnqueueAsync(activeDerivative, default));
        Required(await enqueue.EnqueueAsync(rejectedIngest, default));
        JobLeaseAssignment[] assignments = Required(await enqueue.LeaseAsync(
                Request("worker", UtcNow, maximumCount: 2),
                default))
            .ToArray();

        var completed = await Task.WhenAll(
            assignments.Select(assignment =>
                database.CreateQueue().CompleteAsync(
                    new JobCompletionRequest(
                        assignment.Job.Id,
                        assignment.Lease.Owner,
                        assignment.Lease.JobVersion,
                        UtcNow.AddSeconds(1)),
                    default).AsTask()));

        Assert.All(completed, result => Assert.True(result.IsSuccess));
        Assert.Equal(4, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(1, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_two_consumed_uploads_release_concurrent_owned_receipts()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId tenantId = DefaultTenantId;
        Guid firstUpload = Guid.CreateVersion7();
        Guid secondUpload = Guid.CreateVersion7();
        Guid secondRevision = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 10);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            firstUpload,
            state: "Consumed",
            activatedRevisionId: Guid.CreateVersion7());
        await database.SeedUploadCorrelationAsync(
            tenantId,
            secondUpload,
            state: "Consumed",
            activatedRevisionId: secondRevision);
        var derivativePayload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            secondRevision,
            "grid");
        DurableJob firstIngest = CreateJob(
            $"upload:{firstUpload:D}:ingest:v1",
            tenantId: tenantId,
            type: new JobType("upload.ingest"),
            payload: $$"""{"uploadSessionId":"{{firstUpload:D}}"}""");
        DurableJob secondDerivative = CreateJob(
            DerivativeJobContract.CreateDedupeKey(derivativePayload).Value,
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(derivativePayload));
        RelationalJobQueue enqueue = database.CreateQueue();
        Required(await enqueue.EnqueueAsync(firstIngest, default));
        Required(await enqueue.EnqueueAsync(secondDerivative, default));
        JobLeaseAssignment[] assignments = Required(await enqueue.LeaseAsync(
                Request("worker", UtcNow, maximumCount: 2),
                default))
            .ToArray();

        var completed = await Task.WhenAll(
            assignments.Select(assignment =>
                database.CreateQueue().CompleteAsync(
                    new JobCompletionRequest(
                        assignment.Job.Id,
                        assignment.Lease.Owner,
                        assignment.Lease.JobVersion,
                        UtcNow.AddSeconds(1)),
                    default).AsTask()));

        Assert.All(completed, result => Assert.True(result.IsSuccess));
        Assert.Equal(8, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(2, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_concurrent_duplicate_recovery_releases_capacity_once()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId tenantId = DefaultTenantId;
        Guid revisionId = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: revisionId);
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            revisionId,
            "thumb");
        DurableJob job = CreateJob(
            DerivativeJobContract.CreateDedupeKey(payload).Value,
            maxAttempts: 1,
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        RelationalJobQueue enqueue = database.CreateQueue();
        Required(await enqueue.EnqueueAsync(job, default));
        _ = Assert.Single(
            Required(await enqueue.LeaseAsync(Request("owner", UtcNow), default)));

        var recoveryResults = await Task.WhenAll(
            database.CreateQueue().LeaseAsync(
                Request("first", UtcNow.AddMinutes(2)),
                default).AsTask(),
            database.CreateQueue().LeaseAsync(
                Request("second", UtcNow.AddMinutes(2)),
                default).AsTask());
        IReadOnlyList<JobLeaseAssignment>[] recovered =
            recoveryResults.Select(Required).ToArray();

        Assert.All(recovered, Assert.Empty);
        Assert.Equal(0, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(1, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_duplicate_deadletter_cannot_consume_other_upload_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId tenantId = DefaultTenantId;
        Guid firstRevision = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 6);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: firstRevision);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: Guid.CreateVersion7());
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            firstRevision,
            "thumb");
        DurableJob job = CreateJob(
            DerivativeJobContract.CreateDedupeKey(payload).Value,
            maxAttempts: 1,
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        RelationalJobQueue queue = database.CreateQueue();
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("owner", UtcNow), default)));
        var request = new JobFailureRequest(
            job.Id,
            assignment.Lease.Owner,
            assignment.Lease.JobVersion,
            new JobFailure(JobFailureReason.ProcessingFailed),
            UtcNow.AddSeconds(1),
            new JobRetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        Assert.True((await queue.FailAsync(request, default)).IsSuccess);
        Assert.True((await queue.FailAsync(request, default)).IsFailure);

        Assert.Equal(5, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(1, await database.QuotaVersionAsync(tenantId));
    }

    [Fact]
    public async Task JobQueue_five_receipts_cannot_consume_a_second_uploads_capacity()
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId tenantId = DefaultTenantId;
        Guid firstUpload = Guid.CreateVersion7();
        Guid secondUpload = Guid.CreateVersion7();
        Guid firstRevision = Guid.CreateVersion7();
        Guid secondRevision = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 10);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            firstUpload,
            state: "Consumed",
            activatedRevisionId: firstRevision);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            secondUpload,
            state: "Consumed",
            activatedRevisionId: secondRevision);

        JobCompletionRequest[] firstReceipts = await EnqueueAndCompleteCapacityJobsAsync(
            database,
            tenantId,
            firstUpload,
            firstRevision);
        Assert.Equal(5, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(5, await database.QuotaVersionAsync(tenantId));

        RelationalJobQueue duplicateCompletion = database.CreateQueue();
        foreach (JobCompletionRequest receipt in firstReceipts)
        {
            Assert.True(
                (await duplicateCompletion.CompleteAsync(receipt, default)).IsSuccess);
        }

        Assert.Equal(5, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(5, await database.QuotaVersionAsync(tenantId));

        _ = await EnqueueAndCompleteCapacityJobsAsync(
            database,
            tenantId,
            secondUpload,
            secondRevision);
        Assert.Equal(0, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(10, await database.QuotaVersionAsync(tenantId));
    }

    [Theory]
    [InlineData(TerminalPath.Complete)]
    [InlineData(TerminalPath.DeadLetter)]
    [InlineData(TerminalPath.ExpiredReclaim)]
    public async Task JobQueue_terminal_receipt_failure_rolls_back_job_and_quota(
        TerminalPath path)
    {
        await using JobDatabase database = await JobDatabase.CreateAsync();
        JobTenantId tenantId = DefaultTenantId;
        Guid revisionId = Guid.CreateVersion7();
        await database.SeedCommittedJobsAsync(tenantId, 1);
        await database.SeedUploadCorrelationAsync(
            tenantId,
            Guid.CreateVersion7(),
            state: "Consumed",
            activatedRevisionId: revisionId);
        var payload = new DerivativeJobPayloadV1(
            Guid.CreateVersion7(),
            revisionId,
            "thumb");
        DurableJob job = CreateJob(
            DerivativeJobContract.CreateDedupeKey(payload).Value,
            maxAttempts: 1,
            tenantId: tenantId,
            type: DerivativeJobContract.Type,
            payload: DerivativeJobContract.Serialize(payload));
        RelationalJobQueue queue = database.CreateQueue();
        Required(await queue.EnqueueAsync(job, default));
        JobLeaseAssignment assignment = Assert.Single(
            Required(await queue.LeaseAsync(Request("owner", UtcNow), default)));
        await database.FailQuotaUpdatesAsync();

        await Assert.ThrowsAsync<SqliteException>(
            path switch
            {
                TerminalPath.Complete => async () => _ = await queue.CompleteAsync(
                    new JobCompletionRequest(
                        job.Id,
                        assignment.Lease.Owner,
                        assignment.Lease.JobVersion,
                        UtcNow.AddSeconds(1)),
                    default),
                TerminalPath.DeadLetter => async () => _ = await queue.FailAsync(
                    new JobFailureRequest(
                        job.Id,
                        assignment.Lease.Owner,
                        assignment.Lease.JobVersion,
                        new JobFailure(JobFailureReason.ProcessingFailed),
                        UtcNow.AddSeconds(1),
                        new JobRetryPolicy(
                            TimeSpan.FromSeconds(1),
                            TimeSpan.FromSeconds(1))),
                    default),
                TerminalPath.ExpiredReclaim => async () => _ = await queue.LeaseAsync(
                    Request("replacement", UtcNow.AddMinutes(2)),
                    default),
                _ => throw new ArgumentOutOfRangeException(nameof(path)),
            });

        JobRow persisted = await database.SingleAsync();
        Assert.Equal(JobState.Leased.ToString(), persisted.State);
        Assert.Equal(1, await database.CommittedJobsAsync(tenantId));
        Assert.Equal(0, await database.QuotaVersionAsync(tenantId));
    }

    private static async Task<IReadOnlyList<JobLeaseAssignment>> Lease(
        RelationalJobQueue queue,
        string owner,
        int maximumCount) =>
        Required(await queue.LeaseAsync(Request(owner, UtcNow, maximumCount), default));

    private static async Task<JobEnqueueResult> Enqueue(
        RelationalJobQueue queue,
        DurableJob job) =>
        Required(await queue.EnqueueAsync(job, default));

    private static async Task<JobCompletionRequest[]> EnqueueAndCompleteCapacityJobsAsync(
        JobDatabase database,
        JobTenantId tenantId,
        Guid uploadSessionId,
        Guid revisionId)
    {
        RelationalJobQueue queue = database.CreateQueue();
        var jobs = new List<DurableJob>
        {
            CreateJob(
                $"upload:{uploadSessionId:D}:ingest:v1",
                tenantId: tenantId,
                type: new JobType("upload.ingest"),
                payload: $$"""{"uploadSessionId":"{{uploadSessionId:D}}"}"""),
        };
        Guid assetId = Guid.CreateVersion7();
        foreach (string preset in StandardDerivativePresets)
        {
            var payload = new DerivativeJobPayloadV1(
                assetId,
                revisionId,
                preset);
            jobs.Add(CreateJob(
                DerivativeJobContract.CreateDedupeKey(payload).Value,
                tenantId: tenantId,
                type: DerivativeJobContract.Type,
                payload: DerivativeJobContract.Serialize(payload)));
        }

        foreach (DurableJob job in jobs)
        {
            Required(await queue.EnqueueAsync(job, default));
        }

        JobLeaseAssignment[] assignments = Required(await queue.LeaseAsync(
                Request("capacity-worker", UtcNow, maximumCount: jobs.Count),
                default))
            .ToArray();
        var receipts = new List<JobCompletionRequest>(assignments.Length);
        foreach (JobLeaseAssignment assignment in assignments)
        {
            var receipt = new JobCompletionRequest(
                assignment.Job.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                UtcNow.AddSeconds(1));
            Assert.True((await queue.CompleteAsync(receipt, default)).IsSuccess);
            receipts.Add(receipt);
        }

        return receipts.ToArray();
    }

    private static JobLeaseRequest Request(
        string owner,
        DateTimeOffset now,
        int maximumCount = 1) =>
        new(new JobLeaseOwner(owner), now, TimeSpan.FromMinutes(1), maximumCount);

    private static DurableJob CreateJob(
        string key,
        int maxAttempts = 3,
        JobTenantId? tenantId = null,
        JobType? type = null,
        string payload = """{"safe":true}""") =>
        DurableJob.Create(
            new JobId(Guid.CreateVersion7()),
            tenantId ?? DefaultTenantId,
            type ?? new JobType("test.job"),
            payload,
            1,
            new JobDedupeKey(key),
            10,
            maxAttempts,
            UtcNow,
            UtcNow,
            "00-safe-trace");

    private static T Required<T>(Vistara.Domain.Common.Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Code);
        return value;
    }

    public enum TerminalPath
    {
        Complete,
        DeadLetter,
        ExpiredReclaim,
    }
}

internal sealed class JobDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _anchor;
    private readonly DbContextOptions<JobDbContext> _options;

    private JobDatabase(SqliteConnection anchor, DbContextOptions<JobDbContext> options)
    {
        _anchor = anchor;
        _options = options;
    }

    internal static async ValueTask<JobDatabase> CreateAsync()
    {
        string name = $"JobQueue-{Guid.NewGuid():N}";
        string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var context = new JobDbContext(
            options,
            new FixedTenantScope(JobQueueTests.DefaultTenantId.Value));
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE quota_usage (
                tenant_id TEXT NOT NULL PRIMARY KEY,
                committed_jobs INTEGER NOT NULL,
                version INTEGER NOT NULL
            );
            CREATE TABLE upload_sessions (
                tenant_id TEXT NOT NULL,
                id TEXT NOT NULL,
                activated_revision_id TEXT NULL,
                PRIMARY KEY (tenant_id, id)
            );
            CREATE TABLE quota_reservations (
                tenant_id TEXT NOT NULL,
                upload_session_id TEXT NOT NULL,
                state TEXT NOT NULL,
                reserved_jobs INTEGER NOT NULL,
                PRIMARY KEY (tenant_id, upload_session_id)
            )
            """);
        return new JobDatabase(anchor, options);
    }

    internal RelationalJobQueue CreateQueue() =>
        CreateQueue(JobQueueTests.DefaultTenantId);

    internal RelationalJobQueue CreateQueue(JobTenantId tenantId)
    {
        var tenantScope = new FixedTenantScope(tenantId.Value);
        return new RelationalJobQueue(
            new JobDbContext(_options, tenantScope),
            new JobQueueOptions { ConfiguredWorkerCount = 1 });
    }

    internal async Task<int> CountAsync()
    {
        await using var context = new JobDbContext(
            _options,
            new FixedTenantScope(JobQueueTests.DefaultTenantId.Value));
        return await context.Jobs.CountAsync();
    }

    internal async Task<JobRow> SingleAsync()
    {
        await using var context = new JobDbContext(
            _options,
            new FixedTenantScope(JobQueueTests.DefaultTenantId.Value));
        return await context.Jobs.AsNoTracking().SingleAsync();
    }

    internal async ValueTask SeedCommittedJobsAsync(
        JobTenantId tenantId,
        long committedJobs)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            """
            INSERT INTO quota_usage (tenant_id, committed_jobs, version)
            VALUES ($tenant_id, $committed_jobs, 0)
            """;
        _ = command.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        _ = command.Parameters.AddWithValue("$committed_jobs", committedJobs);
        _ = await command.ExecuteNonQueryAsync();
    }

    internal async ValueTask<long> CommittedJobsAsync(JobTenantId tenantId)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            """
            SELECT committed_jobs
            FROM quota_usage
            WHERE tenant_id = $tenant_id
            """;
        _ = command.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal async ValueTask<long> QuotaVersionAsync(JobTenantId tenantId)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            """
            SELECT version
            FROM quota_usage
            WHERE tenant_id = $tenant_id
            """;
        _ = command.Parameters.AddWithValue("$tenant_id", tenantId.Value);
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal async ValueTask SeedUploadCorrelationAsync(
        JobTenantId tenantId,
        Guid uploadSessionId,
        string state,
        Guid? activatedRevisionId = null)
    {
        await using SqliteTransaction transaction = _anchor.BeginTransaction();
        await using (SqliteCommand upload = _anchor.CreateCommand())
        {
            upload.Transaction = transaction;
            upload.CommandText =
                """
                INSERT INTO upload_sessions (
                    tenant_id,
                    id,
                    activated_revision_id
                )
                VALUES ($tenant_id, $id, $activated_revision_id)
                """;
            _ = upload.Parameters.AddWithValue("$tenant_id", tenantId.Value);
            _ = upload.Parameters.AddWithValue("$id", uploadSessionId);
            _ = upload.Parameters.AddWithValue(
                "$activated_revision_id",
                activatedRevisionId.HasValue
                    ? activatedRevisionId.Value
                    : DBNull.Value);
            _ = await upload.ExecuteNonQueryAsync();
        }

        await using (SqliteCommand reservation = _anchor.CreateCommand())
        {
            reservation.Transaction = transaction;
            reservation.CommandText =
                """
                INSERT INTO quota_reservations (
                    tenant_id,
                    upload_session_id,
                    state,
                    reserved_jobs
                )
                VALUES ($tenant_id, $upload_session_id, $state, 5)
                """;
            _ = reservation.Parameters.AddWithValue("$tenant_id", tenantId.Value);
            _ = reservation.Parameters.AddWithValue(
                "$upload_session_id",
                uploadSessionId);
            _ = reservation.Parameters.AddWithValue("$state", state);
            _ = await reservation.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    internal async ValueTask FailQuotaUpdatesAsync()
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            """
            CREATE TRIGGER fail_quota_update
            BEFORE UPDATE ON quota_usage
            BEGIN
                SELECT RAISE(ABORT, 'injected quota update failure');
            END
            """;
        _ = await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
}
