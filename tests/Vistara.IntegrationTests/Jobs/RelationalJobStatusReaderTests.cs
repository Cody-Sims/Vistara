using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Jobs;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

public sealed class RelationalJobStatusReaderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reader_returns_the_persisted_state_for_the_owning_tenant()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(default);
        await SeedAsync(connection, tenantId, jobId);

        await using VistaraDbContext context =
            CreateContext(connection, new FixedTenantScope(tenantId));
        var reader = new RelationalJobStatusReader(context);

        JobSnapshot? snapshot = await reader.FindAsync(
            tenantId,
            new JobId(jobId),
            default);

        Assert.NotNull(snapshot);
        Assert.Equal(jobId, snapshot.Id.Value);
        Assert.Equal(tenantId, snapshot.TenantId.Value);
        Assert.Equal(JobState.Pending, snapshot.State);
        Assert.Equal("asset.ingest", snapshot.Type.Value);
        Assert.Equal(3, snapshot.Version.Value);
    }

    [Fact]
    public async Task Reader_conceals_jobs_owned_by_another_tenant()
    {
        Guid ownerTenantId = Guid.CreateVersion7();
        Guid otherTenantId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(default);
        await SeedAsync(connection, ownerTenantId, jobId);

        await using VistaraDbContext context =
            CreateContext(connection, new FixedTenantScope(otherTenantId));
        var reader = new RelationalJobStatusReader(context);

        JobSnapshot? snapshot = await reader.FindAsync(
            otherTenantId,
            new JobId(jobId),
            default);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Reader_rejects_lookups_outside_the_active_tenant_scope()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid jobId = Guid.CreateVersion7();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(default);
        await SeedAsync(connection, tenantId, jobId);

        await using VistaraDbContext context =
            CreateContext(connection, new FixedTenantScope(tenantId));
        var reader = new RelationalJobStatusReader(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.FindAsync(
                Guid.CreateVersion7(),
                new JobId(jobId),
                default));
    }

    private static async Task SeedAsync(
        SqliteConnection connection,
        Guid tenantId,
        Guid jobId)
    {
        await using VistaraDbContext context =
            CreateContext(connection, new FixedTenantScope(tenantId));
        await context.Database.EnsureCreatedAsync(default);
        context.Jobs.Add(new JobRow
        {
            Id = jobId,
            TenantId = tenantId,
            Type = "asset.ingest",
            Payload = """{"assetId":"01990a2a-bc00-7000-8000-000000000b01"}""",
            PayloadVersion = 1,
            DedupeKey = $"asset.ingest:{jobId:D}",
            Priority = 0,
            MaxAttempts = 5,
            Attempts = 0,
            State = "Pending",
            AvailableAtUtc = Now,
            CreatedAtUtc = Now,
            TraceParent = null,
            Version = 3,
        });
        await context.SaveChangesAsync(default);
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
}
