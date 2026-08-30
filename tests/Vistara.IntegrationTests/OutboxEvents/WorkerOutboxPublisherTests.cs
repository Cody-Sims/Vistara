using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Common.Events;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Persistence.Outbox;
using Vistara.Worker.Composition.Platform;
using Xunit;

namespace Vistara.IntegrationTests.OutboxEvents;

public sealed class WorkerOutboxPublisherTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Worker_outbox_publishes_each_catalog_tenant_in_a_fresh_scope()
    {
        string connectionString =
            $"Data Source=WorkerOutbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        Guid firstTenant = Guid.CreateVersion7();
        Guid secondTenant = Guid.CreateVersion7();
        await EnsureSchemaAsync(connectionString, firstTenant);
        await SeedTenantMessageAsync(
            connectionString,
            firstTenant,
            "first",
            """{"jobId":"first"}""");
        await SeedTenantMessageAsync(
            connectionString,
            secondTenant,
            "second",
            """{"jobId":"second"}""");
        ServiceCollection services = [];
        services.AddSingleton<IClock>(new FixedClock(Now.AddMinutes(1)));
        services.AddVistaraWorkerPlatform(Configuration(connectionString));
        await using ServiceProvider provider = services.BuildServiceProvider();
        OutboxPublisher publisher =
            provider.GetRequiredService<OutboxPublisher>();

        Assert.True(
            await publisher.PublishAvailableAsync(CancellationToken.None));

        EventPage first = await ReadEventsAsync(provider, firstTenant);
        EventPage second = await ReadEventsAsync(provider, secondTenant);
        Assert.Equal(
            """{"jobId":"first"}""",
            Assert.Single(first.Events).ClientPayload);
        Assert.Equal(
            """{"jobId":"second"}""",
            Assert.Single(second.Events).ClientPayload);
    }

    private static async Task EnsureSchemaAsync(
        string connectionString,
        Guid tenantId)
    {
        await using VistaraDbContext context =
            CreateContext(connectionString, tenantId);
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task SeedTenantMessageAsync(
        string connectionString,
        Guid tenantId,
        string slug,
        string payload)
    {
        await using VistaraDbContext context =
            CreateContext(connectionString, tenantId);
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
        var repository = new OutboxRepository(context, context);
        EventSequence sequence =
            await repository.ReserveSequenceAsync(CancellationToken.None);
        await repository.AppendAsync(
            OutboxMessage.Create(
                new OutboxMessageId(Guid.CreateVersion7()),
                new EventEnvelope(
                    new EventMetadata(
                        new EventId(Guid.CreateVersion7()),
                        new EventTenantId(tenantId),
                        sequence,
                        "job.completed",
                        eventVersion: 1,
                        Now,
                        correlationId: Guid.CreateVersion7()),
                    payload),
                Now),
            CancellationToken.None);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO authentication_routes (
                 lookup_digest,
                 kind,
                 routed_tenant_id,
                 principal_id,
                 credential_id,
                 created_at_utc)
             VALUES (
                 {$"worker-outbox-{slug}"},
                 {"ApiKey"},
                 {tenantId},
                 {Guid.CreateVersion7()},
                 {Guid.CreateVersion7()},
                 {Now})
             """);
    }

    private static async Task<EventPage> ReadEventsAsync(
        ServiceProvider provider,
        Guid tenantId)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider
            .GetRequiredService<IMutableTenantScope>()
            .Establish(tenantId);
        OutboxRepository repository =
            scope.ServiceProvider.GetRequiredService<OutboxRepository>();
        var result = await repository.ReadAfterAsync(
            new EventTenantId(tenantId),
            new EventCursor(0),
            10,
            CancellationToken.None);
        Assert.True(result.TryGetValue(out EventPage? page), result.Error?.Code);
        return page;
    }

    private static VistaraDbContext CreateContext(
        string connectionString,
        Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
    }

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = connectionString,
                ["Worker:InstanceId"] = "outbox-runtime-test",
            })
            .Build();

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
