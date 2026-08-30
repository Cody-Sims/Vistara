using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Application.Jobs;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Derivatives.Worker;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.DerivativeWorker;

public sealed class RelationalDerivativeStatePortTests
{
    [Fact]
    public async Task Relational_state_survives_scope_restart_with_staged_output()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment assignment = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        DerivativeJobPayloadV1 payload = database.Payload;

        DerivativeAcquireResult first;
        await using (DerivativeStatePortScope scope = database.CreatePort())
        {
            first = await scope.Port.AcquireAsync(
                database.AcquireRequest(assignment, payload),
                CancellationToken.None);
            Assert.Equal(DerivativeAcquireDisposition.Acquired, first.Disposition);
            Assert.Equal(
                DerivativeStateWriteResult.Applied,
                await scope.Port.RecordStagedAsync(
                    first.Fence!.Value,
                    database.Staged,
                    CancellationToken.None));
        }

        JobRow persisted = await database.ReadJobAsync();
        Assert.True(DerivativeJobContract.TryParse(
            new JobType(persisted.Type),
            persisted.PayloadVersion,
            persisted.Payload,
            out DerivativeJobPayloadV1? persistedPayload));
        Assert.Equal(payload, persistedPayload);

        database.Clock.Advance(TimeSpan.FromMinutes(3));
        await using DerivativeStatePortScope restarted = database.CreatePort();
        DerivativeAcquireResult recovered = await restarted.Port.AcquireAsync(
            database.AcquireRequest(assignment, payload),
            CancellationToken.None);

        Assert.Equal(DerivativeAcquireDisposition.Acquired, recovered.Disposition);
        Assert.Equal(database.Staged, recovered.Staged);
        Assert.Equal(database.RevisionId, recovered.Work?.Generation.Source.RevisionId);
        Assert.Equal(DerivativeFormat.WebP, recovered.Work?.Generation.Recipe.Format);
    }

    [Fact]
    public async Task Relational_worker_uses_the_descriptor_recipe_not_current_defaults()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync(
                presetRevision: 9,
                recipeSchemaVersion: 7,
                quality: 73);
        JobLeaseAssignment assignment = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        await using DerivativeStatePortScope scope = database.CreatePort();

        DerivativeAcquireResult acquired = await scope.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);

        Assert.Equal(DerivativeAcquireDisposition.Acquired, acquired.Disposition);
        Assert.Equal(9, acquired.Work?.Generation.Preset.Id.Revision);
        Assert.Equal(7, acquired.Work?.Generation.Recipe.SchemaVersion);
        Assert.Equal(73, acquired.Work?.Generation.Recipe.Quality);
        Assert.Equal(
            database.Payload.Generation.GenerationIdentity,
            acquired.Work?.Generation.Identity.Value);
    }

    [Fact]
    public async Task Relational_publication_fence_rejects_expired_job_owner_before_copy()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment abandoned = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(1));
        DerivativeFence oldFence;
        await using (DerivativeStatePortScope scope = database.CreatePort())
        {
            DerivativeAcquireResult acquired = await scope.Port.AcquireAsync(
                database.AcquireRequest(abandoned, database.Payload),
                CancellationToken.None);
            oldFence = acquired.Fence!.Value;
            Assert.Equal(
                DerivativeStateWriteResult.Applied,
                await scope.Port.RecordStagedAsync(
                    oldFence,
                    database.Staged,
                    CancellationToken.None));
        }

        database.Clock.Advance(TimeSpan.FromMinutes(2));
        JobLeaseAssignment replacement = Assert.Single(
            Required(await database.CreateQueue().LeaseAsync(
                new JobLeaseRequest(
                    new JobLeaseOwner("worker-two"),
                    database.Clock.UtcNow,
                    TimeSpan.FromMinutes(5),
                    MaximumCount: 1),
                CancellationToken.None)));
        bool invoked = false;
        await using DerivativeStatePortScope restarted = database.CreatePort();

        DerivativePublicationOutcome stale =
            await restarted.Port.PublishIfOwnedAsync(
                oldFence,
                database.Staged,
                _ =>
                {
                    invoked = true;
                    return ValueTask.FromResult(
                        DerivativePublicationAttemptOutcome.Published);
                },
                CancellationToken.None);
        DerivativeAcquireResult reacquired = await restarted.Port.AcquireAsync(
            database.AcquireRequest(replacement, database.Payload),
            CancellationToken.None);

        Assert.Equal(DerivativePublicationOutcome.Stale, stale);
        Assert.False(invoked);
        Assert.Equal(DerivativeAcquireDisposition.Acquired, reacquired.Disposition);
        Assert.Equal(database.Staged, reacquired.Staged);
    }

    [Fact]
    public async Task Relational_derivative_fence_remains_valid_across_job_heartbeat()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment assignment = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        await using DerivativeStatePortScope scope = database.CreatePort();
        DerivativeAcquireResult acquired = await scope.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);
        database.Clock.Advance(TimeSpan.FromSeconds(10));

        JobLease heartbeat = Required(await database.CreateQueue().HeartbeatAsync(
            new JobHeartbeatRequest(
                assignment.Job.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                database.Clock.UtcNow,
                TimeSpan.FromMinutes(10)),
            CancellationToken.None));
        DerivativeStateWriteResult staged = await scope.Port.RecordStagedAsync(
            acquired.Fence!.Value,
            database.Staged,
            CancellationToken.None);

        Assert.True(heartbeat.JobVersion.Value > assignment.Lease.JobVersion.Value);
        Assert.Equal(DerivativeStateWriteResult.Applied, staged);
    }

    [Fact]
    public async Task Relational_derivative_fence_excludes_a_live_duplicate_owner()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment assignment = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        await using DerivativeStatePortScope first = database.CreatePort();
        DerivativeAcquireResult acquired = await first.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);
        await using DerivativeStatePortScope duplicate = database.CreatePort();

        DerivativeAcquireResult busy = await duplicate.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);

        Assert.Equal(DerivativeAcquireDisposition.Acquired, acquired.Disposition);
        Assert.Equal(DerivativeAcquireDisposition.Busy, busy.Disposition);
    }

    [Fact]
    public async Task Slow_publication_does_not_hold_the_heartbeat_context_or_transaction()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment assignment = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        await using DerivativeStatePortScope scope = database.CreatePort();
        DerivativeAcquireResult acquired = await scope.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);
        Assert.Equal(
            DerivativeStateWriteResult.Applied,
            await scope.Port.RecordStagedAsync(
                acquired.Fence!.Value,
                database.Staged,
                CancellationToken.None));
        var publicationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<DerivativePublicationOutcome> publication = scope.Port.PublishIfOwnedAsync(
            acquired.Fence.Value,
            database.Staged,
            async cancellationToken =>
            {
                publicationStarted.TrySetResult();
                await allowPublication.Task.WaitAsync(cancellationToken);
                return DerivativePublicationAttemptOutcome.OutcomeUnknown;
            },
            CancellationToken.None).AsTask();
        await publicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        database.Clock.Advance(TimeSpan.FromSeconds(10));
        JobLease heartbeat = Required(await scope.Queue.HeartbeatAsync(
            new JobHeartbeatRequest(
                assignment.Job.Id,
                assignment.Lease.Owner,
                assignment.Lease.JobVersion,
                database.Clock.UtcNow,
                TimeSpan.FromMinutes(10)),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        JobRow visibleDuringPublication = await database.ReadJobAsync();
        PersistedDerivativeRequest intent =
            await database.ReadDerivativeRequestAsync();

        allowPublication.TrySetResult();
        DerivativePublicationOutcome outcome =
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
        PersistedDerivativeRequest completed =
            await database.ReadDerivativeRequestAsync();

        Assert.Equal(heartbeat.JobVersion.Value, visibleDuringPublication.Version);
        Assert.StartsWith(
            "derivative.publication.intent:",
            intent.FailureCode,
            StringComparison.Ordinal);
        Assert.Equal(DerivativePublicationOutcome.OutcomeUnknown, outcome);
        Assert.StartsWith(
            "derivative.publication.outcome_unknown:",
            completed.FailureCode);
    }

    [Fact]
    public async Task Fence_theft_during_publication_makes_the_old_outcome_stale()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment assignment = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(1));
        await using DerivativeStatePortScope owner = database.CreatePort();
        DerivativeAcquireResult acquired = await owner.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);
        Assert.Equal(
            DerivativeStateWriteResult.Applied,
            await owner.Port.RecordStagedAsync(
                acquired.Fence!.Value,
                database.Staged,
                CancellationToken.None));
        var publicationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DerivativePublicationOutcome> publication = owner.Port.PublishIfOwnedAsync(
            acquired.Fence.Value,
            database.Staged,
            async cancellationToken =>
            {
                publicationStarted.TrySetResult();
                await allowPublication.Task.WaitAsync(cancellationToken);
                return DerivativePublicationAttemptOutcome.Published;
            },
            CancellationToken.None).AsTask();
        await publicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        database.Clock.Advance(TimeSpan.FromMinutes(2));
        JobLeaseAssignment replacement = Assert.Single(
            Required(await database.CreateQueue().LeaseAsync(
                new JobLeaseRequest(
                    new JobLeaseOwner("worker-two"),
                    database.Clock.UtcNow,
                    TimeSpan.FromMinutes(5),
                    MaximumCount: 1),
                CancellationToken.None)));
        await using DerivativeStatePortScope thief = database.CreatePort();
        DerivativeAcquireResult stolen = await thief.Port.AcquireAsync(
            database.AcquireRequest(replacement, database.Payload),
            CancellationToken.None);
        allowPublication.TrySetResult();
        DerivativePublicationOutcome oldOutcome =
            await publication.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DerivativeAcquireDisposition.Acquired, stolen.Disposition);
        Assert.Equal(database.Staged, stolen.Staged);
        Assert.Equal(DerivativePublicationOutcome.Stale, oldOutcome);
        Assert.Equal(
            DerivativeStateWriteResult.Applied,
            await thief.Port.RecordStagedAsync(
                stolen.Fence!.Value,
                database.Staged,
                CancellationToken.None));
    }

    [Fact]
    public async Task Crash_after_publication_intent_recovers_the_staged_candidate()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment assignment = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        await using DerivativeStatePortScope first = database.CreatePort();
        DerivativeAcquireResult acquired = await first.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);
        Assert.Equal(
            DerivativeStateWriteResult.Applied,
            await first.Port.RecordStagedAsync(
                acquired.Fence!.Value,
                database.Staged,
                CancellationToken.None));

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await first.Port.PublishIfOwnedAsync(
                acquired.Fence.Value,
                database.Staged,
                _ => throw new OperationCanceledException("simulated crash"),
                CancellationToken.None));

        database.Clock.Advance(TimeSpan.FromMinutes(3));
        await using DerivativeStatePortScope restarted = database.CreatePort();
        DerivativeAcquireResult recovered = await restarted.Port.AcquireAsync(
            database.AcquireRequest(assignment, database.Payload),
            CancellationToken.None);
        DerivativePublicationOutcome retried =
            await restarted.Port.PublishIfOwnedAsync(
                recovered.Fence!.Value,
                recovered.Staged!,
                _ => ValueTask.FromResult(
                    DerivativePublicationAttemptOutcome.Published),
                CancellationToken.None);

        Assert.Equal(DerivativeAcquireDisposition.Acquired, recovered.Disposition);
        Assert.Equal(database.Staged, recovered.Staged);
        Assert.Equal(DerivativePublicationOutcome.Published, retried);
    }

    [Fact]
    public async Task Api_submission_attaches_to_the_existing_pre_generated_job()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment preGenerated = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        DerivativeGenerationRequest generation = Assert.IsType<DerivativeGenerationRequest>(
            DerivativePresetRegistry.Standard.ResolveDefault(
                new DerivativeSourceIdentity(
                    database.TenantId,
                    database.AssetId,
                    database.RevisionId,
                    revisionNumber: 1,
                    new ImageSha256(database.SourceSha256)),
                new DerivativePresetId("thumb", 1),
                new ImagePipelineFingerprint("durable-pipeline"))
            .GenerationRequest);
        Guid requestId = Guid.CreateVersion7();
        await using DerivativeRequestStoreScope scope =
            database.CreateRequestStore();

        PersistedDerivativeSubmissionResult result = await scope.Store.SubmitAsync(
            new PersistedDerivativeSubmission(
                requestId,
                requestId,
                "api-request-1",
                new string('c', 64),
                DerivativeJobContract.CreatePayload(generation),
                isPublic: false,
                database.Clock.UtcNow),
            CancellationToken.None);

        Assert.Equal(
            PersistedDerivativeSubmissionStatus.Attached,
            result.Status);
        Assert.Equal(preGenerated.Job.Id.Value, result.Request?.JobId);
        Assert.Equal(1, await database.CountJobsAsync());
    }

    [Fact]
    public async Task Api_submission_claims_the_existing_pre_generated_request()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment preGenerated = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        DerivativeAcquireResult acquired;
        await using (DerivativeStatePortScope worker = database.CreatePort())
        {
            acquired = await worker.Port.AcquireAsync(
                database.AcquireRequest(preGenerated, database.Payload),
                CancellationToken.None);
        }

        await using DerivativeRequestStoreScope api =
            database.CreateRequestStore();
        PersistedDerivativeSubmission submission = new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "api-request-claimed",
            new string('d', 64),
            database.Payload,
            isPublic: false,
            database.Clock.UtcNow);
        PersistedDerivativeSubmissionResult attached =
            await api.Store.SubmitAsync(submission, CancellationToken.None);
        PersistedDerivativeSubmissionResult conflict =
            await api.Store.SubmitAsync(
                new PersistedDerivativeSubmission(
                    Guid.CreateVersion7(),
                    Guid.CreateVersion7(),
                    "api-request-claimed",
                    new string('e', 64),
                    database.Payload,
                    isPublic: false,
                    database.Clock.UtcNow),
                CancellationToken.None);

        Assert.Equal(DerivativeAcquireDisposition.Acquired, acquired.Disposition);
        Assert.Equal(PersistedDerivativeSubmissionStatus.Attached, attached.Status);
        Assert.Equal(preGenerated.Job.Id.Value, attached.Request?.RequestId);
        Assert.Equal("api-request-claimed", attached.Request?.IdempotencyKey);
        Assert.Equal(
            PersistedDerivativeSubmissionStatus.IdempotencyConflict,
            conflict.Status);
        Assert.Equal(1, await database.CountDerivativeRequestsAsync());
    }

    [Fact]
    public async Task Concurrent_pre_generation_and_api_attachment_create_one_request()
    {
        await using DerivativeStateDatabase database =
            await DerivativeStateDatabase.CreateAsync();
        JobLeaseAssignment preGenerated = await database.EnqueueAndLeaseAsync(
            "worker-one",
            TimeSpan.FromMinutes(10));
        await using DerivativeStatePortScope worker = database.CreatePort();
        await using DerivativeRequestStoreScope api =
            database.CreateRequestStore();
        PersistedDerivativeSubmission submission = new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "api-request-raced",
            new string('f', 64),
            database.Payload,
            isPublic: false,
            database.Clock.UtcNow);

        Task<DerivativeAcquireResult> acquire = worker.Port.AcquireAsync(
            database.AcquireRequest(preGenerated, database.Payload),
            CancellationToken.None).AsTask();
        Task<PersistedDerivativeSubmissionResult> attach = api.Store.SubmitAsync(
            submission,
            CancellationToken.None).AsTask();
        await Task.WhenAll(acquire, attach).WaitAsync(TimeSpan.FromSeconds(10));
        DerivativeAcquireResult acquired = await acquire;
        PersistedDerivativeSubmissionResult attached = await attach;

        Assert.Equal(
            DerivativeAcquireDisposition.Acquired,
            acquired.Disposition);
        Assert.Equal(
            PersistedDerivativeSubmissionStatus.Attached,
            attached.Status);
        Assert.Equal(preGenerated.Job.Id.Value, attached.Request?.JobId);
        Assert.Equal(1, await database.CountJobsAsync());
        Assert.Equal(1, await database.CountDerivativeRequestsAsync());
    }

    private static T Required<T>(Vistara.Domain.Common.Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Code);
        return value;
    }
}

internal sealed class DerivativeStateDatabase : IAsyncDisposable
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _anchor;
    private readonly DbContextOptions<VistaraDbContext> _vistaraOptions;
    private readonly DbContextOptions<JobDbContext> _jobOptions;

    private DerivativeStateDatabase(
        SqliteConnection anchor,
        DbContextOptions<VistaraDbContext> vistaraOptions,
        DbContextOptions<JobDbContext> jobOptions,
        Guid tenantId,
        Guid assetId,
        Guid revisionId,
        string sourceSha256,
        DerivativeJobPayloadV1 payload,
        MutableClock clock,
        DerivativeStagedOutput staged)
    {
        _anchor = anchor;
        _vistaraOptions = vistaraOptions;
        _jobOptions = jobOptions;
        TenantId = tenantId;
        AssetId = assetId;
        RevisionId = revisionId;
        SourceSha256 = sourceSha256;
        Payload = payload;
        Clock = clock;
        Staged = staged;
    }

    internal Guid TenantId { get; }

    internal Guid AssetId { get; }

    internal Guid RevisionId { get; }

    internal string SourceSha256 { get; }

    internal DerivativeJobPayloadV1 Payload { get; }

    internal MutableClock Clock { get; }

    internal DerivativeStagedOutput Staged { get; }

    internal static async ValueTask<DerivativeStateDatabase> CreateAsync(
        int presetRevision = 1,
        int recipeSchemaVersion = 1,
        int quality = 82)
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid userId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid revisionId = Guid.CreateVersion7();
        Guid blobId = Guid.CreateVersion7();
        byte[] source = "durable-derivative-source"u8.ToArray();
        string sourceSha = Convert.ToHexStringLower(SHA256.HashData(source));
        string connectionString =
            $"Data Source=DerivativeState-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var tenantScope = new TestMutableTenantScope(tenantId);
        var vistaraOptions = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var jobOptions = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var context = new VistaraDbContext(vistaraOptions, tenantScope))
        {
            await context.Database.EnsureCreatedAsync();
            context.Users.Add(new UserRow
            {
                Id = userId,
                NormalizedEmail = $"{userId:N}@example.invalid",
                DisplayName = "Derivative worker",
                Status = "Active",
                CreatedAtUtc = UtcNow,
                UpdatedAtUtc = UtcNow,
                Version = 1,
            });
            context.Tenants.Add(new TenantRow
            {
                Id = tenantId,
                TenantId = tenantId,
                Slug = $"tenant-{tenantId:N}",
                Name = "Derivative tenant",
                Status = "Active",
                CreatedAtUtc = UtcNow,
                UpdatedAtUtc = UtcNow,
                Version = 1,
            });
            await context.SaveChangesAsync();
            context.Blobs.Add(new BlobRow
            {
                Id = blobId,
                TenantId = tenantId,
                Provider = "fake",
                Container = "media",
                ObjectKey = $"originals/aa/{tenantId:N}/{assetId:N}/1/{revisionId:N}.png",
                ProviderVersion = "source-v1",
                Sha256 = sourceSha,
                SizeBytes = source.LongLength,
                ContentType = "image/png",
                State = "Active",
                CreatedAtUtc = UtcNow,
            });
            var asset = new AssetRow
            {
                Id = assetId,
                TenantId = tenantId,
                OwnerId = userId,
                Title = "Derivative source",
                Status = "Ready",
                Visibility = "Private",
                CreatedAtUtc = UtcNow,
                UpdatedAtUtc = UtcNow,
                Version = 1,
            };
            context.Assets.Add(asset);
            await context.SaveChangesAsync();
            context.AssetRevisions.Add(new AssetRevisionRow
            {
                Id = revisionId,
                TenantId = tenantId,
                AssetId = assetId,
                RevisionNumber = 1,
                BlobId = blobId,
                DetectedFormat = "png",
                DetectedContentType = "image/png",
                Width = 32,
                Height = 32,
                FrameCount = 1,
                CreatedAtUtc = UtcNow,
            });
            await context.SaveChangesAsync();
            asset.CurrentRevisionId = revisionId;
            await context.SaveChangesAsync();
        }

        var sourceIdentity = new DerivativeSourceIdentity(
            tenantId,
            assetId,
            revisionId,
            revisionNumber: 1,
            new ImageSha256(sourceSha));
        var recipe = new DerivativeRecipe(
            recipeSchemaVersion,
            new DerivativeDimensions(256, 256),
            DerivativeFit.Cover,
            DerivativeFormat.WebP,
            quality,
            DerivativeBackground.Transparent,
            allowUpscale: false,
            DerivativeMetadataBehavior.StripSensitive);
        var preset = new DerivativePreset(
            new DerivativePresetId("thumb", presetRevision),
            [recipe]);
        DerivativeGenerationRequest generation =
            Assert.IsType<DerivativeGenerationRequest>(
                new DerivativePresetRegistry([preset])
                    .ResolveDefault(
                        sourceIdentity,
                        preset.Id,
                        new ImagePipelineFingerprint("durable-pipeline"))
                .GenerationRequest);
        DerivativeJobPayloadV1 payload =
            DerivativeJobContract.CreatePayload(generation);
        var staged = new DerivativeStagedOutput(
            new BlobIdentity(
                new BlobKey(
                    $"staging/derivatives/{tenantId:N}/{Guid.CreateVersion7():N}/1/output.webp"),
                new BlobVersion("staged-v1")),
            Bytes: 23,
            new ImageSha256(
                Convert.ToHexStringLower(SHA256.HashData("staged-output"u8))),
            new BlobMediaType("image/webp"));
        return new DerivativeStateDatabase(
            anchor,
            vistaraOptions,
            jobOptions,
            tenantId,
            assetId,
            revisionId,
            sourceSha,
            payload,
            new MutableClock(UtcNow),
            staged);
    }

    internal RelationalJobQueue CreateQueue()
    {
        var tenantScope = new FixedTenantScope(TenantId);
        return new RelationalJobQueue(
            new JobDbContext(_jobOptions, tenantScope),
            new JobQueueOptions { ConfiguredWorkerCount = 1 });
    }

    internal async ValueTask<JobRow> ReadJobAsync()
    {
        await using var context = new JobDbContext(
            _jobOptions,
            new FixedTenantScope(TenantId));
        return await context.Jobs.AsNoTracking().SingleAsync();
    }

    internal async ValueTask<int> CountJobsAsync()
    {
        await using var context = new JobDbContext(_jobOptions);
        return await context.Jobs.CountAsync();
    }

    internal async ValueTask<int> CountDerivativeRequestsAsync()
    {
        await using DerivativeRequestStoreScope scope = CreateRequestStore();
        return (await scope.Store.ListAsync(
            TenantId,
            AssetId,
            CancellationToken.None)).Count;
    }

    internal async ValueTask<PersistedDerivativeRequest>
        ReadDerivativeRequestAsync()
    {
        await using DerivativeRequestStoreScope scope = CreateRequestStore();
        return Assert.IsType<PersistedDerivativeRequest>(
            await scope.Store.GetAsync(
                TenantId,
                AssetId,
                (await ReadJobAsync()).Id,
                CancellationToken.None));
    }

    internal async ValueTask<JobLeaseAssignment> EnqueueAndLeaseAsync(
        string owner,
        TimeSpan leaseDuration)
    {
        RelationalJobQueue queue = CreateQueue();
        DurableJob job = DurableJob.Create(
            new JobId(Guid.CreateVersion7()),
            new JobTenantId(TenantId),
            DerivativeJobContract.Type,
            DerivativeJobContract.Serialize(Payload),
            DerivativeJobContract.PayloadVersion,
            DerivativeJobContract.CreateDedupeKey(Payload),
            priority: 0,
            maxAttempts: 3,
            Clock.UtcNow,
            Clock.UtcNow);
        _ = Required(await queue.EnqueueAsync(job, CancellationToken.None));
        return Assert.Single(Required(await queue.LeaseAsync(
            new JobLeaseRequest(
                new JobLeaseOwner(owner),
                Clock.UtcNow,
                leaseDuration,
                MaximumCount: 1),
            CancellationToken.None)));
    }

    internal DerivativeAcquireRequest AcquireRequest(
        JobLeaseAssignment assignment,
        DerivativeJobPayloadV1 payload) =>
        new(
            TenantId,
            assignment.Job.Id.Value,
            payload,
            "fake",
            new ImagePipelineFingerprint("durable-pipeline"),
            assignment.Lease,
            Clock.UtcNow,
            TimeSpan.FromMinutes(2));

    internal DerivativeStatePortScope CreatePort()
    {
        var tenantScope = new TestMutableTenantScope(TenantId);
        var vistara = new VistaraDbContext(_vistaraOptions, tenantScope);
        var jobs = new JobDbContext(_jobOptions, tenantScope);
        return new DerivativeStatePortScope(
            vistara,
            jobs,
            new RelationalDerivativeStatePort(
                _vistaraOptions,
                tenantScope,
                Clock));
    }

    internal DerivativeRequestStoreScope CreateRequestStore()
    {
        var tenantScope = new TestMutableTenantScope(TenantId);
        var context = new VistaraDbContext(_vistaraOptions, tenantScope);
        return new DerivativeRequestStoreScope(
            context,
            new RelationalDerivativeRequestStore(context, tenantScope));
    }

    public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();

    private static T Required<T>(Vistara.Domain.Common.Result<T> result)
        where T : notnull
    {
        if (!result.TryGetValue(out T? value))
        {
            throw new InvalidOperationException(result.Error?.Code);
        }

        return value;
    }
}

internal sealed class DerivativeRequestStoreScope(
    VistaraDbContext context,
    RelationalDerivativeRequestStore store) : IAsyncDisposable
{
    internal RelationalDerivativeRequestStore Store { get; } = store;

    public async ValueTask DisposeAsync() => await context.DisposeAsync();
}

internal sealed class DerivativeStatePortScope(
    VistaraDbContext vistara,
    JobDbContext jobs,
    RelationalDerivativeStatePort port) : IAsyncDisposable
{
    internal RelationalDerivativeStatePort Port { get; } = port;

    internal RelationalJobQueue Queue { get; } =
        new(jobs, new JobQueueOptions { ConfiguredWorkerCount = 1 });

    public async ValueTask DisposeAsync()
    {
        await jobs.DisposeAsync();
        await vistara.DisposeAsync();
    }
}

internal sealed class TestMutableTenantScope(Guid tenantId) : IMutableTenantScope
{
    public Guid TenantId { get; private set; } = tenantId;

    public void Establish(Guid tenantId) => TenantId = tenantId;
}
