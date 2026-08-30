using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

public sealed class UploadReconciliationSchedulerTests
{
    [Fact]
    public async Task Scheduler_enqueues_one_deduplicated_job_per_tenant_and_interval()
    {
        string connectionString =
            $"Data Source=UploadScheduler-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        Guid firstTenant = Guid.CreateVersion7();
        Guid secondTenant = Guid.CreateVersion7();
        Guid uncatalogedTenant = Guid.CreateVersion7();
        var clock = new SchedulerClock(
            new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var metadata = new UploadReconciliationScheduleMetadata
        {
            InitialDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMinutes(15),
        };
        ServiceCollection services = [];
        services.AddScoped<SchedulerTenantScope>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<SchedulerTenantScope>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<SchedulerTenantScope>());
        services.AddVistaraJobQueue(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = connectionString;
            options.ConfiguredWorkerCount = 1;
        });
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IUuid7Generator, Uuid7Generator>();
        services.AddSingleton(metadata);
        services.AddSingleton<UploadReconciliationScheduler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await SeedTenantsAsync(
            connectionString,
            firstTenant,
            secondTenant,
            uncatalogedTenant,
            clock.UtcNow);
        UploadReconciliationScheduler scheduler =
            provider.GetRequiredService<UploadReconciliationScheduler>();

        Assert.Equal(2, await scheduler.EnqueueCurrentWindowAsync(CancellationToken.None));
        Assert.Equal(0, await scheduler.EnqueueCurrentWindowAsync(CancellationToken.None));
        clock.Advance(metadata.Interval);
        Assert.Equal(2, await scheduler.EnqueueCurrentWindowAsync(CancellationToken.None));

        JobRow[] jobs =
        [
            .. await ReadJobsAsync(connectionString, firstTenant),
            .. await ReadJobsAsync(connectionString, secondTenant),
        ];
        Assert.Empty(
            await ReadJobsAsync(connectionString, uncatalogedTenant));
        Assert.Equal(4, jobs.Length);
        Assert.Equal(2, jobs.Select(job => job.TenantId).Distinct().Count());
        Assert.All(
            jobs,
            job =>
            {
                Assert.Equal("upload.reconcile", job.Type);
                Assert.Equal(1, job.PayloadVersion);
                Assert.Contains("\"dryRun\":false", job.Payload, StringComparison.Ordinal);
                Assert.Contains("\"cursor\":null", job.Payload, StringComparison.Ordinal);
            });
        Assert.Equal(2, jobs.Select(job => job.DedupeKey).Distinct().Count());
    }

    private static async Task SeedTenantsAsync(
        string connectionString,
        Guid firstTenant,
        Guid secondTenant,
        Guid uncatalogedTenant,
        DateTimeOffset now)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var database = new VistaraDbContext(
            options,
            new FixedTenantScope(firstTenant));
        await database.Database.EnsureCreatedAsync();
        await AddTenantAsync(
            connectionString,
            firstTenant,
            "first",
            now,
            addCatalogRoute: true);
        await AddTenantAsync(
            connectionString,
            secondTenant,
            "second",
            now,
            addCatalogRoute: true);
        await AddTenantAsync(
            connectionString,
            uncatalogedTenant,
            "uncataloged",
            now,
            addCatalogRoute: false);
    }

    private static async Task AddTenantAsync(
        string connectionString,
        Guid tenantId,
        string slug,
        DateTimeOffset now,
        bool addCatalogRoute)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var database = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        database.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = slug,
            Name = slug,
            Status = "Active",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        await database.SaveChangesAsync();
        if (!addCatalogRoute)
        {
            return;
        }

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO worker_tenant_catalog (
                 routed_tenant_id,
                 worker_enabled,
                 updated_at_utc,
                 version)
             VALUES (
                 {tenantId},
                 {true},
                 {now},
                 {1})
             """);
    }

    private static async Task<JobRow[]> ReadJobsAsync(
        string connectionString,
        Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var context = new JobDbContext(
            options,
            new FixedTenantScope(tenantId));
        return await context.Jobs
            .AsNoTracking()
            .OrderBy(job => job.CreatedAtUtc)
            .ToArrayAsync();
    }

    private sealed class SchedulerClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        internal void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class SchedulerTenantScope : IMutableTenantScope
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
                    "A scheduler scope cannot switch tenants.");
            }

            _tenantId = tenantId;
        }
    }
}
