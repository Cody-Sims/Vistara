using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Application.Jobs;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
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
        Payload = payload;
        Clock = clock;
        Staged = staged;
    }

    internal Guid TenantId { get; }

    internal Guid AssetId { get; }

    internal Guid RevisionId { get; }

    internal DerivativeJobPayloadV1 Payload { get; }

    internal MutableClock Clock { get; }

    internal DerivativeStagedOutput Staged { get; }

    internal static async ValueTask<DerivativeStateDatabase> CreateAsync()
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

        var payload = new DerivativeJobPayloadV1(assetId, revisionId, "thumb");
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
                jobs,
                vistara,
                tenantScope,
                DerivativePresetRegistry.Standard,
                Clock));
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

internal sealed class DerivativeStatePortScope(
    VistaraDbContext vistara,
    JobDbContext jobs,
    RelationalDerivativeStatePort port) : IAsyncDisposable
{
    internal RelationalDerivativeStatePort Port { get; } = port;

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
