using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Worker.Features.Reconciliation.Lifecycle;
using Xunit;

namespace Vistara.IntegrationTests.Reconciliation;

public sealed class PurgeRecoveryPersistenceTests
{
    private static readonly DateTimeOffset Requested =
        new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Only_batches_that_actually_started_and_stalled_are_returned()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid stalled = Guid.CreateVersion7();
        Guid freshlyStarted = Guid.CreateVersion7();
        Guid neverStarted = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);
        await SeedBatchAsync(
            dataSource,
            tenantId,
            stalled,
            startedAtUtc: Now.AddHours(-2));
        await SeedBatchAsync(
            dataSource,
            tenantId,
            freshlyStarted,
            startedAtUtc: Now.AddMinutes(-1));
        await SeedBatchAsync(
            dataSource,
            tenantId,
            neverStarted,
            startedAtUtc: null);

        await using ServiceProvider provider = BuildProvider(dataSource);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        IReadOnlyList<StalledPurgeBatch> batches = await scope.ServiceProvider
            .GetRequiredService<IPurgeRecoveryStatePort>()
            .ListStalledBatchesAsync(
                tenantId,
                Now - TimeSpan.FromMinutes(30),
                batchSize: 50,
                CancellationToken.None);

        StalledPurgeBatch batch = Assert.Single(batches);
        Assert.Equal(stalled, batch.BatchId);
        Assert.Equal(Now.AddHours(-2), batch.StartedAtUtc);
    }

    [Fact]
    public async Task A_long_requested_but_recently_started_batch_is_not_recovered()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid recentlyStarted = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);
        await SeedBatchAsync(
            dataSource,
            tenantId,
            recentlyStarted,
            startedAtUtc: Now.AddMinutes(-2));

        await using ServiceProvider provider = BuildProvider(dataSource);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        PurgeRecoveryReport report = await scope.ServiceProvider
            .GetRequiredService<PurgeRecoveryService>()
            .RunAsync(tenantId, dryRun: false, CancellationToken.None);

        Assert.Equal(0, report.Detected);
        Assert.Equal(0, report.Requeued);
    }

    [Fact]
    public async Task Another_tenants_stalled_batch_is_never_returned()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenant = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);
        await SeedTenantAsync(dataSource, otherTenant);
        await SeedBatchAsync(
            dataSource,
            otherTenant,
            Guid.CreateVersion7(),
            startedAtUtc: Now.AddHours(-3));

        await using ServiceProvider provider = BuildProvider(dataSource);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        IReadOnlyList<StalledPurgeBatch> batches = await scope.ServiceProvider
            .GetRequiredService<IPurgeRecoveryStatePort>()
            .ListStalledBatchesAsync(
                tenantId,
                Now - TimeSpan.FromMinutes(30),
                batchSize: 50,
                CancellationToken.None);

        Assert.Empty(batches);
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
        services.AddVistaraJobQueue(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = dataSource;
            options.ConfiguredWorkerCount = 1;
        });
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IUuid7Generator>(
            new Uuid7Generator(new FixedClock(Now)));
        services.AddVistaraPurgeRecoveryReconciliation();
        return services.BuildServiceProvider();
    }

    private static string NewDataSource() =>
        $"Data Source=PurgeRecovery-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

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
            CreatedAtUtc = Requested,
            UpdatedAtUtc = Requested,
            Version = 1,
        });
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task SeedBatchAsync(
        string dataSource,
        Guid tenantId,
        Guid batchId,
        DateTimeOffset? startedAtUtc)
    {
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(dataSource)
                .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        context.PurgeBatches.Add(new PurgeBatchRow
        {
            Id = batchId,
            TenantId = new TenantKey(tenantId),
            RequestedByUserId = Guid.CreateVersion7(),
            RequestedAtUtc = Requested,
            CandidateCount = 1,
            EligibleCount = 1,
            StartedAtUtc = startedAtUtc,
            State = "Executing",
            Version = 1,
        });
        await context.SaveChangesAsync(CancellationToken.None);
    }

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
