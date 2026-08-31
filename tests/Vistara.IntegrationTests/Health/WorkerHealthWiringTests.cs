using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Vistara.Observability.Health;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Worker.Composition.Media;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Composition.Runtime;
using Xunit;

namespace Vistara.IntegrationTests.Health;

public sealed class WorkerHealthWiringTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Composed_worker_reports_healthy_startup_and_readiness()
    {
        string root = CreateScratchRoot();
        string connectionString =
            $"Data Source=WorkerHealth-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(CancellationToken.None);
        try
        {
            await CreateSchemaAsync(connectionString);
            await using ServiceProvider provider =
                BuildProvider(connectionString, root);

            WorkerHealthSnapshot snapshot = await provider
                .GetRequiredService<WorkerHealthMonitor>()
                .EvaluateAsync(CancellationToken.None);

            Assert.True(
                snapshot.Readiness.State == HealthState.Healthy,
                HealthReportJson.Serialize(snapshot.Readiness));
            Assert.Contains(
                snapshot.Readiness.Checks,
                check =>
                    check.Dependency == HealthDependency.Storage &&
                    check.State == HealthState.Healthy);
            Assert.All(
                snapshot.Startup.Checks.Where(check =>
                    check.Dependency is HealthDependency.Configuration
                        or HealthDependency.Migrations),
                check => Assert.Equal(HealthState.Healthy, check.State));
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public async Task Worker_readiness_degrades_without_the_database_schema()
    {
        string root = CreateScratchRoot();
        string connectionString =
            $"Data Source=WorkerHealth-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(CancellationToken.None);
        try
        {
            await using ServiceProvider provider =
                BuildProvider(connectionString, root);

            WorkerHealthSnapshot snapshot = await provider
                .GetRequiredService<WorkerHealthMonitor>()
                .EvaluateAsync(CancellationToken.None);

            Assert.Equal(HealthState.Unhealthy, snapshot.State);
            Assert.Contains(
                snapshot.Readiness.Checks,
                check =>
                    check.Dependency == HealthDependency.Schema &&
                    check.ReasonCode == HealthReasonCodes.SchemaIncompatible);
            Assert.DoesNotContain(
                connectionString,
                HealthReportJson.Serialize(snapshot.Readiness),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public async Task Worker_health_monitoring_publishes_state_and_stops_on_cancellation()
    {
        string root = CreateScratchRoot();
        string connectionString =
            $"Data Source=WorkerHealth-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(CancellationToken.None);
        try
        {
            await CreateSchemaAsync(connectionString);
            await using ServiceProvider provider =
                BuildProvider(connectionString, root);
            WorkerHealthMonitor monitor =
                provider.GetRequiredService<WorkerHealthMonitor>();
            using var stopping = new CancellationTokenSource();

            Task loop = monitor.RunAsync(stopping.Token);
            await WaitForSnapshotAsync(provider.GetRequiredService<IWorkerHealthState>());
            await stopping.CancelAsync();
            await loop;

            IWorkerHealthState state =
                provider.GetRequiredService<IWorkerHealthState>();
            Assert.NotNull(state.Current);
            Assert.Equal(
                HealthState.Healthy,
                state.Current!.Readiness.State);
            Assert.True(loop.IsCompletedSuccessfully);
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public void Worker_runtime_composition_registers_health_and_telemetry()
    {
        string root = CreateScratchRoot();
        try
        {
            ServiceCollection services = [];
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    Settings("Data Source=:memory:", root))
                .Build();
            services.AddVistaraMedia(configuration);
            services.AddVistaraWorkerRuntime(configuration);
            using ServiceProvider provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetService<TracerProvider>());
            Assert.Contains(
                services,
                descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) &&
                    descriptor.ImplementationType?.Name ==
                        "WorkerHealthMonitorHostedService");
            Assert.NotNull(provider.GetService<IWorkerHealthState>());
            using IServiceScope scope = provider.CreateScope();
            Assert.NotEmpty(
                scope.ServiceProvider.GetServices<IHealthDependencyProbe>());
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public async Task Worker_readiness_fails_when_the_database_cannot_answer_a_query()
    {
        string root = CreateScratchRoot();
        try
        {
            // The file opens as a connection but rejects every statement, which
            // is what an unreachable or refusing database looks like to a probe
            // that only asks whether a connection can be established.
            string database = Path.Combine(root, "corrupt.db");
            await File.WriteAllBytesAsync(
                database,
                "this is deliberately not a database"u8.ToArray(),
                CancellationToken.None);
            await using ServiceProvider provider =
                BuildProvider($"Data Source={database}", root);

            WorkerHealthSnapshot snapshot = await provider
                .GetRequiredService<WorkerHealthMonitor>()
                .EvaluateAsync(CancellationToken.None);

            HealthCheckResult check = Assert.Single(
                snapshot.Readiness.Checks,
                candidate => candidate.Dependency == HealthDependency.Database);
            Assert.Equal(HealthState.Unhealthy, check.State);
            Assert.Equal(
                HealthReasonCodes.DependencyUnavailable,
                check.ReasonCode);
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    private static async Task WaitForSnapshotAsync(IWorkerHealthState state)
    {
        for (int attempt = 0; attempt < 100 && state.Current is null; attempt++)
        {
            await Task.Delay(20, CancellationToken.None);
        }
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
        string root)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings(connectionString, root))
            .Build();
        ServiceCollection services = [];
        services.AddVistaraMedia(configuration);
        services.AddVistaraWorkerPlatform(configuration);
        services.AddVistaraWorkerRuntime(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Settings(
        string connectionString,
        string root) =>
        new()
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = connectionString,
            ["Worker:InstanceId"] = "worker-health-test",
            ["Media:Storage:Provider"] = "Local",
            ["Media:Storage:Local:RootPath"] = root,
            ["Media:Imaging:Provider"] = "NetVips",
            ["Telemetry:ServiceName"] = "vistara-worker-test",
        };

    private static async Task CreateSchemaAsync(string connectionString)
    {
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(Guid.CreateVersion7()));
        await context.Database.EnsureCreatedAsync(CancellationToken.None);
        _ = Now;
        _ = typeof(TenantRow);
    }

    private static string CreateScratchRoot()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "eng",
            "tests",
            "worker-health",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteScratchRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
