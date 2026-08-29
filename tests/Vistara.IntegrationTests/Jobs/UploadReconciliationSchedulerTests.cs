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
        var tenantScope = new SchedulerTenantScope(firstTenant);
        var clock = new SchedulerClock(
            new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var metadata = new UploadReconciliationScheduleMetadata
        {
            InitialDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMinutes(15),
        };
        ServiceCollection services = [];
        services.AddSingleton<ITenantScope>(tenantScope);
        services.AddDbContext<VistaraDbContext>(
            options => options.UseSqlite(connectionString));
        services.AddDbContext<JobDbContext>(
            options => options.UseSqlite(connectionString));
        services.AddScoped<IJobQueue>(provider =>
            new RelationalJobQueue(
                provider.GetRequiredService<JobDbContext>(),
                new JobQueueOptions { ConfiguredWorkerCount = 1 }));
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IUuid7Generator, Uuid7Generator>();
        services.AddSingleton(metadata);
        services.AddSingleton<UploadReconciliationScheduler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await SeedTenantsAsync(
            provider,
            tenantScope,
            firstTenant,
            secondTenant,
            clock.UtcNow);
        UploadReconciliationScheduler scheduler =
            provider.GetRequiredService<UploadReconciliationScheduler>();

        Assert.Equal(2, await scheduler.EnqueueCurrentWindowAsync(CancellationToken.None));
        Assert.Equal(0, await scheduler.EnqueueCurrentWindowAsync(CancellationToken.None));
        clock.Advance(metadata.Interval);
        Assert.Equal(2, await scheduler.EnqueueCurrentWindowAsync(CancellationToken.None));

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        JobRow[] jobs = await scope.ServiceProvider
            .GetRequiredService<JobDbContext>()
            .Jobs
            .AsNoTracking()
            .OrderBy(job => job.TenantId)
            .ThenBy(job => job.CreatedAtUtc)
            .ToArrayAsync();
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
        ServiceProvider provider,
        SchedulerTenantScope tenantScope,
        Guid firstTenant,
        Guid secondTenant,
        DateTimeOffset now)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        VistaraDbContext database =
            scope.ServiceProvider.GetRequiredService<VistaraDbContext>();
        await database.Database.EnsureCreatedAsync();
        await AddTenantAsync(database, tenantScope, firstTenant, "first", now);
        await AddTenantAsync(database, tenantScope, secondTenant, "second", now);
    }

    private static async Task AddTenantAsync(
        VistaraDbContext database,
        SchedulerTenantScope tenantScope,
        Guid tenantId,
        string slug,
        DateTimeOffset now)
    {
        tenantScope.Establish(tenantId);
        database.ChangeTracker.Clear();
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
    }

    private sealed class SchedulerClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        internal void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class SchedulerTenantScope(Guid tenantId) : ITenantScope
    {
        public Guid TenantId { get; private set; } = tenantId;

        internal void Establish(Guid tenantId) => TenantId = tenantId;
    }
}
