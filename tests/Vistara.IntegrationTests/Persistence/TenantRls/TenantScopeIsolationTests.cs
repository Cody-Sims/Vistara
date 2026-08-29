using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Persistence.TenantRls;

public sealed class TenantScopeIsolationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PostgreSql_tenant_setting_is_transaction_local()
    {
        Assert.Equal(
            "SELECT set_config('vistara.tenant_id', @tenant_id, true);",
            TenantRlsCommandInterceptor.SetTenantSql);
    }

    [Fact]
    public async Task Unset_tenant_fails_closed_with_a_scope_error()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        Guid tenantId = Guid.CreateVersion7();
        await using (var schema = CreateContext(
                         connection,
                         new FixedTenantScope(tenantId)))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await using VistaraDbContext context =
            CreateContext(connection, new MutableTenantScope());
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.Assets.CountAsync());

        Assert.Contains("tenant scope", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reused_context_applies_the_current_explicit_sqlite_tenant_filter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        Guid tenantOne = Guid.CreateVersion7();
        Guid tenantTwo = Guid.CreateVersion7();
        await using (var schema = CreateContext(
                         connection,
                         new FixedTenantScope(tenantOne)))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await AddTenantAsync(connection, tenantOne, "one");
        await AddTenantAsync(connection, tenantTwo, "two");

        var tenantScope = new MutableTenantScope();
        await using VistaraDbContext context = CreateContext(connection, tenantScope);
        tenantScope.Establish(tenantOne);
        string[] first = await context.Tenants
            .AsNoTracking()
            .Select(row => row.Slug)
            .ToArrayAsync();

        tenantScope.Establish(tenantTwo);
        string[] second = await context.Tenants
            .AsNoTracking()
            .Select(row => row.Slug)
            .ToArrayAsync();

        Assert.Equal(["one"], first);
        Assert.Equal(["two"], second);
    }

    private static async Task AddTenantAsync(
        SqliteConnection connection,
        Guid tenantId,
        string slug)
    {
        await using VistaraDbContext context =
            CreateContext(connection, new FixedTenantScope(tenantId));
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = slug,
            Name = slug,
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
    }

    private static VistaraDbContext CreateContext(
        SqliteConnection connection,
        ITenantScope tenantScope)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connection)
            .Options;
        return new VistaraDbContext(options, tenantScope);
    }

    private sealed class MutableTenantScope : IMutableTenantScope
    {
        public Guid TenantId { get; private set; }

        public void Establish(Guid tenantId) => TenantId = tenantId;
    }
}
