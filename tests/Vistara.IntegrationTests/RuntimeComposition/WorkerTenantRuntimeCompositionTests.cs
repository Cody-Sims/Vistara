using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Worker.Composition.Platform;
using Xunit;

namespace Vistara.IntegrationTests.RuntimeComposition;

public sealed class WorkerTenantRuntimeCompositionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fresh_worker_scope_discovers_catalog_before_tenant_is_established()
    {
        string connectionString =
            $"Data Source=WorkerCatalog-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        Guid tenantId = Guid.CreateVersion7();
        await SeedTenantRouteAsync(connectionString, tenantId);
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(Configuration(connectionString));
        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ITenantScope tenantScope =
            scope.ServiceProvider.GetRequiredService<ITenantScope>();
        Assert.Throws<InvalidOperationException>(() => tenantScope.TenantId);

        IWorkerTenantCatalog catalog = scope.ServiceProvider
            .GetRequiredService<IWorkerTenantCatalog>();
        IReadOnlyList<Guid> tenantIds =
            await catalog.ListTenantIdsAsync(CancellationToken.None);

        Assert.Equal([tenantId], tenantIds);
        IMutableTenantScope mutable = scope.ServiceProvider
            .GetRequiredService<IMutableTenantScope>();
        mutable.Establish(tenantId);
        Assert.Equal(tenantId, tenantScope.TenantId);
        Assert.Throws<InvalidOperationException>(
            () => mutable.Establish(Guid.CreateVersion7()));
    }

    private static async Task SeedTenantRouteAsync(
        string connectionString,
        Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        await context.Database.EnsureCreatedAsync();
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = "worker-catalog",
            Name = "Worker catalog",
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO worker_tenant_catalog (
                 routed_tenant_id,
                 worker_enabled,
                 updated_at_utc,
                 version)
             VALUES (
                 {tenantId},
                 {true},
                 {Now},
                 {1})
             """);
    }

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = connectionString,
                ["Worker:InstanceId"] = "tenant-runtime-test",
            })
            .Build();
}
