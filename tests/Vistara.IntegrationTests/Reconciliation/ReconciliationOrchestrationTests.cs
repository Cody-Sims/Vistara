using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Worker.Features.Reconciliation.Lifecycle;
using Vistara.Worker.Features.Reconciliation.Storage;
using Vistara.Worker.Runtime.Jobs;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Runtime.Reconciliation;
using Xunit;

namespace Vistara.IntegrationTests.Reconciliation;

public sealed class ReconciliationOrchestrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scheduler_enqueues_each_sweep_once_per_tenant_and_window()
    {
        string connectionString =
            $"Data Source=Reconcile-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(CancellationToken.None);
        Guid routedTenant = Guid.CreateVersion7();
        Guid unroutedTenant = Guid.CreateVersion7();
        var clock = new AdvancingClock(Now);
        await SeedTenantsAsync(
            connectionString,
            routedTenant,
            unroutedTenant,
            Now);
        await using ServiceProvider provider =
            BuildSchedulerProvider(connectionString, clock);
        ReconciliationScheduler scheduler =
            provider.GetRequiredService<ReconciliationScheduler>();
        ReconciliationSchedule blobs = ReconciliationSchedules.BlobIntegrity;

        Assert.Equal(
            1,
            await scheduler.EnqueueWindowAsync(blobs, CancellationToken.None));
        Assert.Equal(
            0,
            await scheduler.EnqueueWindowAsync(blobs, CancellationToken.None));
        clock.Advance(blobs.Interval);
        Assert.Equal(
            1,
            await scheduler.EnqueueWindowAsync(blobs, CancellationToken.None));
        Assert.Equal(
            1,
            await scheduler.EnqueueWindowAsync(
                ReconciliationSchedules.PurgeRecovery,
                CancellationToken.None));

        JobRow[] routed = await ReadJobsAsync(connectionString, routedTenant);
        Assert.Empty(await ReadJobsAsync(connectionString, unroutedTenant));
        Assert.Equal(3, routed.Length);
        Assert.Equal(
            2,
            routed.Count(job => job.Type == "storage.reconcile"));
        Assert.Single(
            routed,
            job => job.Type == "lifecycle.purge.reconcile");
        Assert.All(
            routed,
            job => Assert.Equal(routedTenant, job.TenantId));
        Assert.Equal(3, routed.Select(job => job.DedupeKey).Distinct().Count());
        Assert.Contains(
            routed,
            job => job.Payload.Contains("\"dryRun\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scheduler_rejects_duplicate_sweep_registrations()
    {
        await Task.CompletedTask;
        Assert.Throws<InvalidOperationException>(() =>
            new ReconciliationScheduler(
                new ServiceCollection().BuildServiceProvider()
                    .GetRequiredService<IServiceScopeFactory>(),
                new AdvancingClock(Now),
                new Uuid7Generator(new AdvancingClock(Now)),
                [
                    ReconciliationSchedules.BlobIntegrity,
                    ReconciliationSchedules.BlobIntegrity,
                ]));
    }

    [Fact]
    public async Task Scheduler_loop_stops_when_the_worker_is_cancelled()
    {
        string connectionString =
            $"Data Source=Reconcile-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(CancellationToken.None);
        Guid routedTenant = Guid.CreateVersion7();
        await SeedTenantsAsync(
            connectionString,
            routedTenant,
            Guid.CreateVersion7(),
            Now);
        await using ServiceProvider provider = BuildSchedulerProvider(
            connectionString,
            new AdvancingClock(Now));
        ReconciliationScheduler scheduler =
            provider.GetRequiredService<ReconciliationScheduler>();
        using var stopping = new CancellationTokenSource();

        Task loop = scheduler.RunAsync(stopping.Token);
        await stopping.CancelAsync();
        await loop;

        Assert.True(loop.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Relational_blob_state_marks_missing_once_within_the_tenant()
    {
        string connectionString =
            $"Reconcile-state-{Guid.NewGuid():N}";
        string dataSource =
            $"Data Source={connectionString};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenant = Guid.CreateVersion7();
        Guid blobId = Guid.CreateVersion7();
        await SeedTenantsAsync(dataSource, tenantId, otherTenant, Now);
        await SeedBlobAsync(dataSource, tenantId, blobId, "assets/one", Now);

        ServiceCollection services = [];
        services.AddScoped<ScopedTenant>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddVistaraPersistence(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = dataSource;
        });
        services.AddVistaraBlobIntegrityReconciliation();
        await using ServiceProvider provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IBlobIntegrityStatePort state = scope.ServiceProvider
                .GetRequiredService<IBlobIntegrityStatePort>();
            BlobIntegrityPage page = await state.ScanActiveAsync(
                tenantId,
                cursor: null,
                batchSize: 10,
                CancellationToken.None);

            BlobIntegrityRecord record = Assert.Single(page.Records);
            Assert.Equal("assets/one", record.ObjectKey);
            Assert.True(
                await state.MarkMissingAsync(
                    tenantId,
                    blobId,
                    CancellationToken.None));
            Assert.False(
                await state.MarkMissingAsync(
                    tenantId,
                    blobId,
                    CancellationToken.None));
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IBlobIntegrityStatePort state = scope.ServiceProvider
                .GetRequiredService<IBlobIntegrityStatePort>();
            BlobIntegrityPage page = await state.ScanActiveAsync(
                tenantId,
                cursor: null,
                batchSize: 10,
                CancellationToken.None);
            Assert.Empty(page.Records);
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IBlobIntegrityStatePort state = scope.ServiceProvider
                .GetRequiredService<IBlobIntegrityStatePort>();
            IReadOnlyCollection<string> unknown =
                await state.FilterUnknownObjectKeysAsync(
                    otherTenant,
                    ["assets/one", "assets/orphan"],
                    CancellationToken.None);
            Assert.Equal(
                ["assets/one", "assets/orphan"],
                unknown.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Purge_recovery_requeues_only_stalled_batches_and_deduplicates()
    {
        Guid tenantId = Guid.CreateVersion7();
        var queue = new RecordingJobQueue();
        var state = new FakePurgeState(
            tenantId,
            [
                new StalledPurgeBatch(Guid.CreateVersion7(), Now.AddHours(-2)),
            ]);
        var service = new PurgeRecoveryService(
            state,
            queue,
            new AdvancingClock(Now),
            new Uuid7Generator(new AdvancingClock(Now)),
            new PurgeRecoveryOptions());

        PurgeRecoveryReport dryRun =
            await service.RunAsync(tenantId, dryRun: true, CancellationToken.None);
        PurgeRecoveryReport first =
            await service.RunAsync(tenantId, dryRun: false, CancellationToken.None);
        PurgeRecoveryReport second =
            await service.RunAsync(tenantId, dryRun: false, CancellationToken.None);

        Assert.Equal(1, dryRun.Detected);
        Assert.Equal(0, dryRun.Requeued);
        Assert.Equal(1, first.Requeued);
        Assert.Equal(0, second.Requeued);
        DurableJob job = Assert.Single(queue.Created);
        Assert.Equal(LifecycleJobContracts.PurgeType.Value, job.Type.Value);
        Assert.Equal(tenantId, job.TenantId.Value);
    }

    [Fact]
    public async Task Purge_recovery_refuses_to_cross_tenants()
    {
        Guid tenantId = Guid.CreateVersion7();
        var service = new PurgeRecoveryService(
            new FakePurgeState(tenantId, []),
            new RecordingJobQueue(),
            new AdvancingClock(Now),
            new Uuid7Generator(new AdvancingClock(Now)),
            new PurgeRecoveryOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.RunAsync(
                Guid.CreateVersion7(),
                dryRun: false,
                CancellationToken.None));
    }

    [Fact]
    public void Worker_composition_registers_the_repair_handlers_and_sweeps()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = "Data Source=:memory:",
                ["Worker:InstanceId"] = "reconciliation-test",
            })
            .Build();
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(configuration);

        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IJobHandler) &&
                descriptor.ImplementationType == typeof(BlobIntegrityJobHandler));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IJobHandler) &&
                descriptor.ImplementationType == typeof(PurgeRecoveryJobHandler));
        ReconciliationSchedule[] schedules =
        [
            .. services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(ReconciliationSchedule))
                .Select(descriptor =>
                    (ReconciliationSchedule)descriptor.ImplementationInstance!),
        ];
        Assert.Equal(3, schedules.Length);
        Assert.All(schedules, schedule => Assert.True(schedule.Interval > TimeSpan.Zero));
        Assert.Equal(
            schedules.Length,
            schedules
                .Select(schedule => schedule.DedupePrefix)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Contains(
            schedules,
            schedule => schedule.JobType == "storage.reconcile" && schedule.DryRun);
        Assert.Contains(
            schedules,
            schedule => schedule.JobType == "derivative.reconcile");
    }

    private static ServiceProvider BuildSchedulerProvider(
        string connectionString,
        IClock clock)
    {
        ServiceCollection services = [];
        services.AddScoped<ScopedTenant>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddVistaraJobQueue(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = connectionString;
            options.ConfiguredWorkerCount = 1;
        });
        services.AddSingleton(clock);
        services.AddSingleton<IUuid7Generator>(new Uuid7Generator(clock));
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.BlobIntegrity);
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.PurgeRecovery);
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.BlobIntegrity);
        services.AddVistaraReconciliationScheduler();
        return services.BuildServiceProvider();
    }

    private static async Task SeedTenantsAsync(
        string connectionString,
        Guid routedTenant,
        Guid unroutedTenant,
        DateTimeOffset now)
    {
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using var database = new VistaraDbContext(
            options,
            new FixedTenantScope(routedTenant));
        await database.Database.EnsureCreatedAsync(CancellationToken.None);
        foreach ((Guid tenantId, bool routed) in
                 new[] { (routedTenant, true), (unroutedTenant, false) })
        {
            await using var context = new VistaraDbContext(
                options,
                new FixedTenantScope(tenantId));
            context.Tenants.Add(new TenantRow
            {
                Id = tenantId,
                TenantId = tenantId,
                Slug = tenantId.ToString("N"),
                Name = tenantId.ToString("N"),
                Status = "Active",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            await context.SaveChangesAsync(CancellationToken.None);
            if (!routed)
            {
                continue;
            }

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO worker_tenant_catalog (
                     routed_tenant_id,
                     worker_enabled,
                     updated_at_utc,
                     version)
                 VALUES ({tenantId}, {true}, {now}, {1})
                 """,
                CancellationToken.None);
        }
    }

    private static async Task SeedBlobAsync(
        string connectionString,
        Guid tenantId,
        Guid blobId,
        string objectKey,
        DateTimeOffset now)
    {
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = new TenantKey(tenantId),
            Provider = "local",
            Container = "media",
            ObjectKey = objectKey,
            ProviderVersion = "v1",
            Sha256 = new string('a', 64),
            SizeBytes = 10,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = now,
        });
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task<JobRow[]> ReadJobsAsync(
        string connectionString,
        Guid tenantId)
    {
        DbContextOptions<JobDbContext> options =
            new DbContextOptionsBuilder<JobDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using var context = new JobDbContext(
            options,
            new FixedTenantScope(tenantId));
        return await context.Jobs
            .AsNoTracking()
            .OrderBy(job => job.CreatedAtUtc)
            .ToArrayAsync(CancellationToken.None);
    }

    private sealed class AdvancingClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        internal void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class ScopedTenant : IMutableTenantScope
    {
        private Guid? _tenantId;

        public Guid TenantId =>
            _tenantId ??
            throw new InvalidOperationException(
                "A tenant scope must be established.");

        public void Establish(Guid tenantId)
        {
            if (_tenantId.HasValue && _tenantId.Value != tenantId)
            {
                throw new InvalidOperationException(
                    "A reconciliation scope cannot switch tenants.");
            }

            _tenantId = tenantId;
        }
    }

    private sealed class FakePurgeState(
        Guid tenantId,
        StalledPurgeBatch[] batches) : IPurgeRecoveryStatePort
    {
        public ValueTask<IReadOnlyList<StalledPurgeBatch>>
            ListStalledBatchesAsync(
                Guid requestedTenantId,
                DateTimeOffset stalledBeforeUtc,
                int batchSize,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedTenantId != tenantId)
            {
                throw new InvalidOperationException(
                    "Purge recovery crossed a tenant boundary.");
            }

            IReadOnlyList<StalledPurgeBatch> stalled =
            [
                .. batches
                    .Where(batch => batch.StartedAtUtc <= stalledBeforeUtc)
                    .Take(batchSize),
            ];
            return ValueTask.FromResult(stalled);
        }
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        private readonly HashSet<string> _dedupeKeys = new(StringComparer.Ordinal);

        internal List<DurableJob> Created { get; } = [];

        public ValueTask<Result<JobEnqueueResult>> EnqueueAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool created = _dedupeKeys.Add(job.DedupeKey!.Value);
            if (created)
            {
                Created.Add(job);
            }

            return ValueTask.FromResult(
                Result.Success<JobEnqueueResult>(
                    new JobEnqueueResult(job.Id, created)));
        }

        public ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseAsync(
            JobLeaseRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<JobLease>> HeartbeatAsync(
            JobHeartbeatRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> CompleteAsync(
            JobCompletionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> FailAsync(
            JobFailureRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> RecoverExpiredAsync(
            JobExpiredLeaseRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
