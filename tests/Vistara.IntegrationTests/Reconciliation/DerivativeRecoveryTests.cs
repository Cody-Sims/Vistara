using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Reconciliation.Derivatives;
using Vistara.Worker.Runtime.Jobs;
using Vistara.Worker.Runtime.Reconciliation;
using Xunit;

namespace Vistara.IntegrationTests.Reconciliation;

public sealed class DerivativeRecoveryTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dead_lettered_generation_is_requeued_with_a_granted_budget()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(dataSource, tenantId, requestId, jobId, maxAttempts: 5);
        await using ServiceProvider provider = BuildProvider(dataSource);

        DerivativeRecoveryReport report = await RunAsync(provider, tenantId);

        Assert.Equal(1, report.Detected);
        Assert.Equal(1, report.Requeued);
        Assert.Equal(0, report.Exhausted);
        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        Assert.Equal(nameof(JobState.RetryScheduled), job.State);
        Assert.Equal(8, job.MaxAttempts);
        Assert.Equal(5, job.Attempts);
        Assert.Equal(Now, job.AvailableAtUtc);
        Assert.Null(job.FailureCode);
        Assert.Null(job.LeaseOwner);
        Assert.Equal(2, job.Version);
        Assert.Equal(
            "Queued",
            (await ReadRequestAsync(dataSource, tenantId, requestId)).State);
    }

    [Fact]
    public async Task Recovered_generation_becomes_leasable_again()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(dataSource, tenantId, requestId, jobId, maxAttempts: 5);
        await using ServiceProvider provider = BuildProvider(dataSource);
        _ = await RunAsync(provider, tenantId);

        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        bool leasable =
            (job.State == nameof(JobState.Pending) ||
                job.State == nameof(JobState.RetryScheduled)) &&
            job.AvailableAtUtc <= Now &&
            job.Attempts < job.MaxAttempts;

        Assert.True(leasable);
    }

    [Fact]
    public async Task Recovery_is_idempotent_across_repeated_sweeps()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(dataSource, tenantId, requestId, jobId, maxAttempts: 5);
        await using ServiceProvider provider = BuildProvider(dataSource);

        DerivativeRecoveryReport first = await RunAsync(provider, tenantId);
        DerivativeRecoveryReport second = await RunAsync(provider, tenantId);

        Assert.Equal(1, first.Requeued);
        Assert.Equal(0, second.Detected);
        Assert.Equal(0, second.Requeued);
        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        Assert.Equal(8, job.MaxAttempts);
        Assert.Equal(2, job.Version);
    }

    [Fact]
    public async Task Exhausted_recovery_closes_the_request_as_failed()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(dataSource, tenantId, requestId, jobId, maxAttempts: 11);
        await using ServiceProvider provider = BuildProvider(dataSource);

        DerivativeRecoveryReport report = await RunAsync(provider, tenantId);

        Assert.Equal(1, report.Detected);
        Assert.Equal(0, report.Requeued);
        Assert.Equal(1, report.Exhausted);
        DerivativeRequestSnapshot request =
            await ReadRequestAsync(dataSource, tenantId, requestId);
        Assert.Equal("Failed", request.State);
        Assert.Equal("jobs.processing_failed", request.FailureCode);
        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        Assert.Equal(nameof(JobState.DeadLettered), job.State);
        Assert.Equal(11, job.MaxAttempts);
    }

    [Fact]
    public async Task Fresh_dead_letters_stay_untouched_until_the_threshold_passes()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(
            dataSource,
            tenantId,
            requestId,
            jobId,
            maxAttempts: 5,
            updatedAtUtc: Now.AddMinutes(-1));
        await using ServiceProvider provider = BuildProvider(dataSource);

        DerivativeRecoveryReport report = await RunAsync(provider, tenantId);

        Assert.Equal(0, report.Detected);
        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        Assert.Equal(nameof(JobState.DeadLettered), job.State);
    }

    [Fact]
    public async Task Ready_derivatives_are_never_recovered()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(
            dataSource,
            tenantId,
            requestId,
            jobId,
            maxAttempts: 5,
            requestState: "Ready");
        await using ServiceProvider provider = BuildProvider(dataSource);

        DerivativeRecoveryReport report = await RunAsync(provider, tenantId);

        Assert.Equal(0, report.Detected);
    }

    [Fact]
    public async Task Recovery_never_observes_another_tenant()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenant = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(
            dataSource,
            tenantId,
            requestId,
            jobId,
            maxAttempts: 5,
            additionalTenantId: otherTenant);
        await using ServiceProvider provider = BuildProvider(dataSource);

        DerivativeRecoveryReport report = await RunAsync(provider, otherTenant);

        Assert.Equal(0, report.Detected);
        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        Assert.Equal(nameof(JobState.DeadLettered), job.State);
    }

    [Fact]
    public async Task Dry_run_reports_candidates_without_reviving_them()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(dataSource, tenantId, requestId, jobId, maxAttempts: 5);
        await using ServiceProvider provider = BuildProvider(dataSource);

        DerivativeRecoveryReport report =
            await RunAsync(provider, tenantId, dryRun: true);

        Assert.Equal(1, report.Detected);
        Assert.Equal(0, report.Requeued);
        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        Assert.Equal(nameof(JobState.DeadLettered), job.State);
    }

    [Fact]
    public async Task Recovery_honours_cancellation()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedAsync(dataSource, tenantId, requestId, jobId, maxAttempts: 5);
        await using ServiceProvider provider = BuildProvider(dataSource);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await scope.ServiceProvider
                .GetRequiredService<DerivativeRecoveryService>()
                .RunAsync(tenantId, dryRun: false, cancelled.Token));

        JobRow job = await ReadJobAsync(dataSource, tenantId, jobId);
        Assert.Equal(nameof(JobState.DeadLettered), job.State);
    }

    [Fact]
    public async Task Recovery_job_handler_rejects_a_foreign_payload_version()
    {
        Guid tenantId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);
        await using ServiceProvider provider = BuildProvider(dataSource);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        DurableJob job = DurableJob.Create(
            new JobId(Guid.CreateVersion7()),
            new JobTenantId(tenantId),
            DerivativeRecoveryJobHandler.SupportedJobType,
            """{"cursor":null,"dryRun":false}""",
            payloadVersion: 2,
            new JobDedupeKey("derivative-reconcile:2:1"),
            priority: 0,
            maxAttempts: 5,
            availableAtUtc: Now,
            createdAtUtc: Now);

        JobHandlerResult result = await scope.ServiceProvider
            .GetRequiredService<DerivativeRecoveryJobHandler>()
            .HandleAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Worker_composition_schedules_derivative_recovery()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = "Data Source=:memory:",
                ["Worker:InstanceId"] = "derivative-recovery-test",
            })
            .Build();
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(configuration);

        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IJobHandler) &&
                descriptor.ImplementationType ==
                    typeof(DerivativeRecoveryJobHandler));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(ReconciliationSchedule) &&
                descriptor.ImplementationInstance is ReconciliationSchedule schedule &&
                schedule.JobType == "derivative.reconcile");
    }

    private static async Task<DerivativeRecoveryReport> RunAsync(
        ServiceProvider provider,
        Guid tenantId,
        bool dryRun = false)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<DerivativeRecoveryService>()
            .RunAsync(tenantId, dryRun, CancellationToken.None);
    }

    private static ServiceProvider BuildProvider(string dataSource)
    {
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
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddVistaraDerivativeRecoveryReconciliation();
        return services.BuildServiceProvider();
    }

    private static string NewDataSource() =>
        $"Data Source=DerivativeRecovery-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static async Task SeedTenantAsync(string dataSource, Guid tenantId)
    {
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(dataSource)
                .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        await context.Database.EnsureCreatedAsync(CancellationToken.None);
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = tenantId.ToString("N"),
            Name = tenantId.ToString("N"),
            Status = "Active",
            CreatedAtUtc = Created,
            UpdatedAtUtc = Created,
            Version = 1,
        });
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task SeedAsync(
        string dataSource,
        Guid tenantId,
        Guid requestId,
        Guid jobId,
        int maxAttempts,
        DateTimeOffset? updatedAtUtc = null,
        string requestState = "Queued",
        Guid? additionalTenantId = null)
    {
        await SeedTenantAsync(dataSource, tenantId);
        if (additionalTenantId is { } other)
        {
            await SeedTenantAsync(dataSource, other);
        }

        Guid userId = Guid.CreateVersion7();
        Guid blobId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid revisionId = Guid.CreateVersion7();
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(dataSource)
                .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        context.Users.Add(new UserRow
        {
            Id = userId,
            NormalizedEmail = $"{userId:N}@example.test",
            DisplayName = "owner",
            Status = "Active",
            CreatedAtUtc = Created,
            UpdatedAtUtc = Created,
            Version = 1,
        });
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = new TenantKey(tenantId),
            Provider = "local",
            Container = "media",
            ObjectKey = $"assets/{blobId:N}",
            ProviderVersion = "v1",
            Sha256 = new string('a', 64),
            SizeBytes = 1024,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = Created,
        });
        context.Assets.Add(new AssetRow
        {
            Id = assetId,
            TenantId = new TenantKey(tenantId),
            OwnerId = userId,
            Title = "asset",
            Status = "Ready",
            Visibility = "Private",
            CreatedAtUtc = Created,
            UpdatedAtUtc = Created,
            Version = 1,
        });
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = revisionId,
            TenantId = new TenantKey(tenantId),
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = blobId,
            DetectedFormat = "jpeg",
            DetectedContentType = "image/jpeg",
            Width = 640,
            Height = 480,
            FrameCount = 1,
            CreatedAtUtc = Created,
        });
        context.Jobs.Add(new JobRow
        {
            Id = jobId,
            TenantId = tenantId,
            Type = "asset.derivative.generate",
            Payload = """{"generation":{}}""",
            PayloadVersion = 2,
            DedupeKey = $"derivative:{jobId:N}",
            Priority = 0,
            MaxAttempts = maxAttempts,
            Attempts = maxAttempts,
            State = nameof(JobState.DeadLettered),
            AvailableAtUtc = Created,
            CreatedAtUtc = Created,
            FailureCode = "jobs.processing_failed",
            Version = 1,
        });
        await context.SaveChangesAsync(CancellationToken.None);

        bool ready = requestState == "Ready";
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO derivative_requests (
                 id, tenant_id, asset_id, revision_id, job_id,
                 idempotency_key, request_hash, preset_name, preset_revision,
                 width, height, fit, format, quality,
                 pipeline_id, pipeline_fingerprint, source_sha256,
                 recipe_sha256, generation_identity, cache_key, extension,
                 is_public, state,
                 representation_storage_key, representation_content_length,
                 representation_content_type, representation_sha256,
                 created_at_utc, updated_at_utc, version)
             VALUES (
                 {requestId}, {tenantId}, {assetId},
                 {revisionId}, {jobId},
                 {$"key-{requestId:N}"}, {$"hash-{requestId:N}"}, {"thumb"}, {1},
                 {320}, {240}, {"contain"}, {"webp"}, {80},
                 {"pipeline"}, {"fingerprint"}, {new string('a', 64)},
                 {new string('b', 64)}, {$"identity-{requestId:N}"},
                 {$"cache-{requestId:N}"}, {"webp"},
                 {false}, {requestState},
                 {(ready ? $"derivatives/{requestId:N}" : null)},
                 {(ready ? 1024L : (long?)null)},
                 {(ready ? "image/webp" : null)},
                 {(ready ? new string('c', 64) : null)},
                 {Created}, {updatedAtUtc ?? Created}, {1})
             """,
            CancellationToken.None);
    }

    private static async Task<JobRow> ReadJobAsync(
        string dataSource,
        Guid tenantId,
        Guid jobId)
    {
        DbContextOptions<JobDbContext> options =
            new DbContextOptionsBuilder<JobDbContext>()
                .UseSqlite(dataSource)
                .Options;
        await using var context = new JobDbContext(
            options,
            new FixedTenantScope(tenantId));
        return await context.Jobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId, CancellationToken.None);
    }

    private static async Task<DerivativeRequestSnapshot> ReadRequestAsync(
        string dataSource,
        Guid tenantId,
        Guid requestId)
    {
        _ = tenantId;
        await using var connection = new SqliteConnection(dataSource);
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "state", "failure_code", "version"
            FROM "derivative_requests"
            WHERE "id" = $id
            """;
        command.Parameters.AddWithValue("$id", requestId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return new DerivativeRequestSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2));
    }

    private sealed record DerivativeRequestSnapshot(
        string State,
        string? FailureCode,
        long Version);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
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
                    "A recovery scope cannot switch tenants.");
            }

            _tenantId = tenantId;
        }
    }
}
