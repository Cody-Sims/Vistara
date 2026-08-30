using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Domain.Tenancy;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Repositories;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

public sealed class WorkerTenantCatalogTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Tenant_creation_atomically_provisions_minimal_worker_route()
    {
        await using CatalogDatabase database = await CatalogDatabase.CreateAsync();
        Guid tenantId =
            Guid.Parse("01991a54-6c00-7000-8000-000000000201");
        Tenant tenant = Required(Tenant.Create(
            new TenantId(tenantId),
            "catalog-create",
            "Catalog create",
            Now));

        await database.AddAsync(tenant);

        Assert.Equal(
            [tenantId],
            await database.ListTenantIdsAsync());
        Assert.Equal(
            [
                "routed_tenant_id",
                "updated_at_utc",
                "version",
                "worker_enabled",
            ],
            await database.ReadCatalogColumnsAsync());
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                "SELECT COUNT(*) FROM authentication_routes;"));
        Assert.Equal(
            new CatalogState(true, 1, Now),
            await database.ReadCatalogStateAsync(tenantId));
    }

    [Fact]
    public async Task Failed_tenant_creation_rolls_back_worker_route()
    {
        await using CatalogDatabase database = await CatalogDatabase.CreateAsync();
        Tenant first = Required(Tenant.Create(
            new TenantId(
                Guid.Parse("01991a54-6c00-7000-8000-000000000211")),
            "duplicate-slug",
            "First",
            Now));
        Tenant conflicting = Required(Tenant.Create(
            new TenantId(
                Guid.Parse("01991a54-6c00-7000-8000-000000000212")),
            "duplicate-slug",
            "Conflicting",
            Now));
        await database.AddAsync(first);

        await Assert.ThrowsAsync<DbUpdateException>(
            async () => await database.AddAsync(conflicting));

        Assert.Equal(
            [first.Id.Value],
            await database.ListTenantIdsAsync());
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                "SELECT COUNT(*) FROM worker_tenant_catalog;"));
    }

    [Fact]
    public async Task Status_changes_suspend_reactivate_and_tombstone_worker_route()
    {
        await using CatalogDatabase database = await CatalogDatabase.CreateAsync();
        Guid tenantId =
            Guid.Parse("01991a54-6c00-7000-8000-000000000221");
        Tenant tenant = Required(Tenant.Create(
            new TenantId(tenantId),
            "catalog-status",
            "Catalog status",
            Now));
        await database.AddAsync(tenant);

        Assert.True(tenant.Suspend(Now.AddMinutes(1)).IsSuccess);
        await database.UpdateAsync(tenant, expectedVersion: 1);
        Assert.Empty(await database.ListTenantIdsAsync());
        Assert.Equal(
            new CatalogState(false, 2, Now.AddMinutes(1)),
            await database.ReadCatalogStateAsync(tenantId));

        Assert.True(tenant.Activate(Now.AddMinutes(2)).IsSuccess);
        await database.UpdateAsync(tenant, expectedVersion: 2);
        Assert.Equal([tenantId], await database.ListTenantIdsAsync());
        Assert.Equal(
            new CatalogState(true, 3, Now.AddMinutes(2)),
            await database.ReadCatalogStateAsync(tenantId));

        Assert.True(tenant.Deactivate(Now.AddMinutes(3)).IsSuccess);
        await database.UpdateAsync(tenant, expectedVersion: 3);
        Assert.Empty(await database.ListTenantIdsAsync());
        Assert.Equal(
            new CatalogState(false, 4, Now.AddMinutes(3)),
            await database.ReadCatalogStateAsync(tenantId));
    }

    [Fact]
    public async Task Physical_tenant_deletion_cascades_worker_route()
    {
        await using CatalogDatabase database = await CatalogDatabase.CreateAsync();
        Guid tenantId =
            Guid.Parse("01991a54-6c00-7000-8000-000000000229");
        await database.AddAsync(Required(Tenant.Create(
            new TenantId(tenantId),
            "catalog-delete",
            "Catalog delete",
            Now)));

        await database.ExecuteAsync("DELETE FROM tenants;");

        Assert.Empty(await database.ListTenantIdsAsync());
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                "SELECT COUNT(*) FROM worker_tenant_catalog;"));
    }

    [Fact]
    public async Task Concurrent_tenant_update_cannot_overwrite_newer_catalog_version()
    {
        await using CatalogDatabase database = await CatalogDatabase.CreateAsync();
        Guid tenantId =
            Guid.Parse("01991a54-6c00-7000-8000-000000000231");
        await database.AddAsync(Required(Tenant.Create(
            new TenantId(tenantId),
            "catalog-concurrency",
            "Catalog concurrency",
            Now)));
        Tenant first = await database.LoadAsync(tenantId);
        Tenant stale = await database.LoadAsync(tenantId);
        Assert.True(first.Suspend(Now.AddMinutes(1)).IsSuccess);
        Assert.True(stale.Deactivate(Now.AddMinutes(2)).IsSuccess);

        await database.UpdateAsync(first, expectedVersion: 1);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await database.UpdateAsync(stale, expectedVersion: 1));

        Assert.Equal(
            new CatalogState(false, 2, Now.AddMinutes(1)),
            await database.ReadCatalogStateAsync(tenantId));
    }

    [Fact]
    public async Task Catalog_version_mismatch_blocks_tenant_update()
    {
        await using CatalogDatabase database = await CatalogDatabase.CreateAsync();
        Guid tenantId =
            Guid.Parse("01991a54-6c00-7000-8000-000000000241");
        await database.AddAsync(Required(Tenant.Create(
            new TenantId(tenantId),
            "catalog-version",
            "Catalog version",
            Now)));
        await database.ExecuteAsync(
            """
             UPDATE worker_tenant_catalog
             SET
                 version = 2,
                 updated_at_utc = '2026-08-29T12:01:00Z';
             """);
        Tenant tenant = await database.LoadAsync(tenantId);
        Assert.True(tenant.Rename("Blocked rename", Now.AddMinutes(2)).IsSuccess);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await database.UpdateAsync(tenant, expectedVersion: 1));

        Tenant persisted = await database.LoadAsync(tenantId);
        Assert.Equal("Catalog version", persisted.Name);
        Assert.Equal(1, persisted.Version);
        Assert.Equal(
            new CatalogState(true, 2, Now.AddMinutes(1)),
            await database.ReadCatalogStateAsync(tenantId));
    }

    [Fact]
    public async Task Active_tenants_are_read_in_bounded_keyset_pages()
    {
        await using CatalogDatabase database = await CatalogDatabase.CreateAsync();
        Guid[] tenantIds =
        [
            Guid.Parse("01991a54-6c00-7000-8000-000000000301"),
            Guid.Parse("01991a54-6c00-7000-8000-000000000302"),
            Guid.Parse("01991a54-6c00-7000-8000-000000000303"),
            Guid.Parse("01991a54-6c00-7000-8000-000000000304"),
            Guid.Parse("01991a54-6c00-7000-8000-000000000305"),
            Guid.Parse("01991a54-6c00-7000-8000-000000000306"),
        ];
        for (int index = 0; index < tenantIds.Length; index++)
        {
            Tenant tenant = Required(Tenant.Create(
                new TenantId(tenantIds[index]),
                $"catalog-page-{index}",
                $"Catalog page {index}",
                Now));
            if (index == 2)
            {
                Assert.True(tenant.Suspend(Now.AddMinutes(1)).IsSuccess);
            }

            await database.AddAsync(tenant);
        }

        IReadOnlyList<Guid> first =
            await database.ListTenantIdsAsync(afterTenantId: null, maximumCount: 2);
        IReadOnlyList<Guid> second =
            await database.ListTenantIdsAsync(first[^1], maximumCount: 2);
        IReadOnlyList<Guid> third =
            await database.ListTenantIdsAsync(second[^1], maximumCount: 2);

        Assert.Equal(tenantIds[..2], first);
        Assert.Equal(tenantIds[3..5], second);
        Assert.Equal([tenantIds[5]], third);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await database.ListTenantIdsAsync(
                afterTenantId: null,
                maximumCount: 257));
    }

    private static Tenant Required(
        Vistara.Domain.Common.Result<Tenant> result)
    {
        Assert.True(result.TryGetValue(out Tenant? value), result.Error?.Message);
        return value!;
    }

    private sealed record CatalogState(
        bool WorkerEnabled,
        long Version,
        DateTimeOffset UpdatedAtUtc);

    private sealed class CatalogDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;

        private CatalogDatabase(
            SqliteConnection anchor,
            string connectionString)
        {
            _anchor = anchor;
            ConnectionString = connectionString;
        }

        private string ConnectionString { get; }

        internal static async ValueTask<CatalogDatabase> CreateAsync()
        {
            string connectionString =
                $"Data Source=WorkerCatalog-{Guid.NewGuid():N};" +
                "Mode=Memory;Cache=Shared;Foreign Keys=True";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var database = new CatalogDatabase(anchor, connectionString);
            await using VistaraDbContext context = database.CreateContext(
                Guid.Parse("01991a54-6c00-7000-8000-000000000200"));
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        internal async ValueTask AddAsync(Tenant tenant)
        {
            await using VistaraDbContext context = CreateContext(tenant.Id.Value);
            var repository = new TenantRepository(context);
            await repository.AddAsync(tenant, CancellationToken.None);
        }

        internal async ValueTask<Tenant> LoadAsync(Guid tenantId)
        {
            await using VistaraDbContext context = CreateContext(tenantId);
            var repository = new TenantRepository(context);
            return Assert.IsType<Tenant>(
                await repository.FindByIdAsync(
                    new TenantId(tenantId),
                    CancellationToken.None));
        }

        internal async ValueTask UpdateAsync(
            Tenant tenant,
            long expectedVersion)
        {
            await using VistaraDbContext context = CreateContext(tenant.Id.Value);
            var repository = new TenantRepository(context);
            await repository.UpdateAsync(
                tenant,
                expectedVersion,
                CancellationToken.None);
        }

        internal async ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync()
            => await ListTenantIdsAsync(
                catalog => catalog.ListTenantIdsAsync(CancellationToken.None));

        internal async ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
            Guid? afterTenantId,
            int maximumCount)
            => await ListTenantIdsAsync(
                catalog => catalog.ListTenantIdsAsync(
                    afterTenantId,
                    maximumCount,
                    CancellationToken.None));

        private async ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
            Func<IWorkerTenantCatalog, ValueTask<IReadOnlyList<Guid>>> read)
        {
            var services = new ServiceCollection();
            services.AddVistaraJobQueue(options =>
            {
                options.Provider = VistaraDatabaseProvider.Sqlite;
                options.ConnectionString = ConnectionString;
            });
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await read(
                scope.ServiceProvider.GetRequiredService<IWorkerTenantCatalog>());
        }

        internal async ValueTask<string[]> ReadCatalogColumnsAsync()
        {
            await using SqliteCommand command = _anchor.CreateCommand();
            command.CommandText = "PRAGMA table_info('worker_tenant_catalog');";
            var columns = new List<string>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            return columns.Order(StringComparer.Ordinal).ToArray();
        }

        internal async ValueTask<CatalogState> ReadCatalogStateAsync(Guid tenantId)
        {
            await using SqliteCommand command = _anchor.CreateCommand();
            command.CommandText =
                """
                SELECT worker_enabled, version, updated_at_utc
                FROM worker_tenant_catalog
                WHERE routed_tenant_id = $tenant_id;
                """;
            command.Parameters.AddWithValue("$tenant_id", tenantId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            DateTime updatedAt = reader.GetDateTime(2);
            return new CatalogState(
                reader.GetBoolean(0),
                reader.GetInt64(1),
                new DateTimeOffset(
                    DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc)));
        }

        internal async ValueTask<T> ScalarAsync<T>(string sql)
        {
            await using SqliteCommand command = _anchor.CreateCommand();
            command.CommandText = sql;
            object? value = await command.ExecuteScalarAsync();
            return (T)Convert.ChangeType(
                value,
                typeof(T),
                CultureInfo.InvariantCulture)!;
        }

        internal async ValueTask ExecuteAsync(string sql)
        {
            await using SqliteCommand command = _anchor.CreateCommand();
            command.CommandText = sql;
            _ = await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _anchor.DisposeAsync();
        }

        private VistaraDbContext CreateContext(Guid tenantId)
        {
            var options = new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(ConnectionString)
                .Options;
            return new VistaraDbContext(
                options,
                new FixedTenantScope(tenantId));
        }
    }
}
