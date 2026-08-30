using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common.Events;
using Vistara.Persistence.Outbox;
using Xunit;

namespace Vistara.IntegrationTests.OutboxEvents;

public sealed class OutboxPersistenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unset_outbox_tenant_fails_closed_before_sqlite_access()
    {
        var options = new DbContextOptionsBuilder<OutboxTestDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context =
            new OutboxTestDbContext(options, Guid.Empty);
        var repository = new OutboxRepository(context, context);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await repository.ReadPendingAsync(
                    new EventCursor(0),
                    10,
                    CancellationToken.None));

        Assert.Contains(
            "tenant scope",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Domain_state_and_outbox_commit_or_roll_back_together()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantId);

        await using (var transaction = await database.Context.Database.BeginTransactionAsync())
        {
            database.Context.DomainStates.Add(new DomainStateRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Value = "rolled-back",
            });
            EventSequence sequence =
                await database.Repository.ReserveSequenceAsync(CancellationToken.None);
            await database.Repository.AppendAsync(
                Message(tenantId, sequence.Value, "asset.ready", """{"assetId":"asset-1"}"""),
                CancellationToken.None);
            await database.Context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.DomainStates.ToListAsync());
        Assert.Empty(await database.Repository.ReadPendingAsync(
            new EventCursor(0),
            10,
            CancellationToken.None));

        await using (var transaction = await database.Context.Database.BeginTransactionAsync())
        {
            database.Context.DomainStates.Add(new DomainStateRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Value = "committed",
            });
            EventSequence sequence =
                await database.Repository.ReserveSequenceAsync(CancellationToken.None);
            await database.Repository.AppendAsync(
                Message(tenantId, sequence.Value, "asset.ready", """{"assetId":"asset-2"}"""),
                CancellationToken.None);
            await database.Context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        database.Context.ChangeTracker.Clear();
        Assert.Single(await database.Context.DomainStates.ToListAsync());
        Assert.Single(await database.Repository.ReadPendingAsync(
            new EventCursor(0),
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task Claims_are_exclusive_retriable_and_fenced()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid firstWorker = Guid.CreateVersion7();
        Guid secondWorker = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantId);
        await AppendAndSaveAsync(database, tenantId, 1);

        OutboxClaim first = Assert.Single(await database.Repository.ClaimPendingAsync(
            firstWorker,
            Now,
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None));

        await using OutboxTestDbContext secondContext = database.CreateContext(tenantId);
        var secondRepository = new OutboxRepository(secondContext, secondContext);
        Assert.Empty(await secondRepository.ClaimPendingAsync(
            secondWorker,
            Now.AddSeconds(30),
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None));

        OutboxClaim second = Assert.Single(await secondRepository.ClaimPendingAsync(
            secondWorker,
            Now.AddMinutes(2),
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None));
        Assert.True(second.Version.Value > first.Version.Value);

        OutboxPublishResult fenced = await database.Repository.PublishClaimAsync(
            first.Message.Id,
            first.ClaimId,
            first.Version,
            Now.AddMinutes(2),
            CancellationToken.None);
        Assert.Equal(OutboxPublishOutcome.Fenced, fenced.Outcome);

        OutboxPublishResult published = await secondRepository.PublishClaimAsync(
            second.Message.Id,
            second.ClaimId,
            second.Version,
            Now.AddMinutes(2),
            CancellationToken.None);
        Assert.Equal(OutboxPublishOutcome.Published, published.Outcome);

        OutboxPublishResult duplicate = await secondRepository.PublishClaimAsync(
            second.Message.Id,
            second.ClaimId,
            second.Version,
            Now.AddMinutes(3),
            CancellationToken.None);
        Assert.Equal(OutboxPublishOutcome.AlreadyPublished, duplicate.Outcome);
    }

    [Fact]
    public async Task Failed_claim_is_delayed_then_retried_with_a_new_fencing_version()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantId);
        await AppendAndSaveAsync(database, tenantId, 1);

        OutboxClaim first = Assert.Single(await database.Repository.ClaimPendingAsync(
            Guid.CreateVersion7(),
            Now,
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None));
        OutboxPublishResult released = await database.Repository.ReleaseClaimAsync(
            first.Message.Id,
            first.ClaimId,
            first.Version,
            Now.AddMinutes(5),
            "broker.unavailable",
            CancellationToken.None);
        Assert.Equal(OutboxPublishOutcome.Released, released.Outcome);

        Assert.Empty(await database.Repository.ClaimPendingAsync(
            Guid.CreateVersion7(),
            Now.AddMinutes(4),
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None));
        OutboxClaim retry = Assert.Single(await database.Repository.ClaimPendingAsync(
            Guid.CreateVersion7(),
            Now.AddMinutes(5),
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None));

        Assert.True(retry.Version.Value > first.Version.Value);
        Assert.Equal(
            OutboxPublishOutcome.Fenced,
            (await database.Repository.PublishClaimAsync(
                first.Message.Id,
                first.ClaimId,
                first.Version,
                Now.AddMinutes(6),
                CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Publication_cannot_advance_past_an_unpublished_sequence()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantId);
        await database.Repository.AppendAsync(
            Message(tenantId, 1, "job.completed", """{"jobId":"job-1"}"""),
            CancellationToken.None);
        await database.Repository.AppendAsync(
            Message(tenantId, 2, "job.completed", """{"jobId":"job-2"}"""),
            CancellationToken.None);
        await database.Context.SaveChangesAsync();

        IReadOnlyList<OutboxClaim> claims = await database.Repository.ClaimPendingAsync(
            Guid.CreateVersion7(),
            Now,
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None);
        Assert.Equal(2, claims.Count);

        Assert.Equal(
            OutboxPublishOutcome.OutOfOrder,
            (await database.Repository.PublishClaimAsync(
                claims[1].Message.Id,
                claims[1].ClaimId,
                claims[1].Version,
                Now.AddMinutes(1),
                CancellationToken.None)).Outcome);
        Assert.Equal(
            OutboxPublishOutcome.Published,
            (await database.Repository.PublishClaimAsync(
                claims[0].Message.Id,
                claims[0].ClaimId,
                claims[0].Version,
                Now.AddMinutes(1),
                CancellationToken.None)).Outcome);
        Assert.Equal(
            OutboxPublishOutcome.Published,
            (await database.Repository.PublishClaimAsync(
                claims[1].Message.Id,
                claims[1].ClaimId,
                claims[1].Version,
                Now.AddMinutes(1),
                CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Event_log_is_ordered_retained_and_tenant_scoped()
    {
        Guid tenantOne = Guid.CreateVersion7();
        Guid tenantTwo = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantOne);
        for (long sequence = 1; sequence <= 4; sequence++)
        {
            await AppendPublishAsync(database.Repository, database.Context, tenantOne, sequence);
        }

        await using (OutboxTestDbContext otherContext = database.CreateContext(tenantTwo))
        {
            var otherRepository = new OutboxRepository(otherContext, otherContext);
            await AppendPublishAsync(otherRepository, otherContext, tenantTwo, 1);
        }

        int removed = await database.Repository.PruneEventLogAsync(
            Now.AddDays(1),
            maximumRetainedEvents: 2,
            maximumAge: TimeSpan.FromDays(7),
            CancellationToken.None);
        Assert.Equal(2, removed);

        var page = await database.Repository.ReadAfterAsync(
            new EventTenantId(tenantOne),
            new EventCursor(0),
            10,
            CancellationToken.None);
        Assert.True(page.TryGetValue(out EventPage? retained), page.Error?.Message);
        Assert.Equal([3L, 4L], retained.Events.Select(item => item.Metadata.Sequence.Value));

        var stale = await database.Repository.ReadAfterAsync(
            new EventTenantId(tenantOne),
            new EventCursor(1),
            10,
            CancellationToken.None);
        Assert.Equal("events.resync_required", stale.Error?.Code);

        var future = await database.Repository.ReadAfterAsync(
            new EventTenantId(tenantOne),
            new EventCursor(5),
            10,
            CancellationToken.None);
        Assert.Equal("events.cursor_in_future", future.Error?.Code);
    }

    [Fact]
    public async Task Cursor_requires_resync_when_age_retention_removes_the_entire_log()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantId);
        await AppendPublishAsync(database.Repository, database.Context, tenantId, 1);
        await AppendPublishAsync(database.Repository, database.Context, tenantId, 2);

        Assert.Equal(
            2,
            await database.Repository.PruneEventLogAsync(
                Now.AddDays(1),
                maximumRetainedEvents: 10,
                maximumAge: TimeSpan.FromHours(1),
                CancellationToken.None));

        var stale = await database.Repository.ReadAfterAsync(
            new EventTenantId(tenantId),
            new EventCursor(1),
            10,
            CancellationToken.None);
        Assert.Equal("events.resync_required", stale.Error?.Code);

        var current = await database.Repository.ReadAfterAsync(
            new EventTenantId(tenantId),
            new EventCursor(2),
            10,
            CancellationToken.None);
        Assert.True(current.TryGetValue(out EventPage? page), current.Error?.Message);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task Unsafe_payload_fields_and_private_media_are_redacted_before_persistence()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantId);
        const string unsafePayload =
            """{"assetId":"asset-1","accessToken":"secret","privateMetadata":{"gps":"1,2"},"thumbnail":"data:image/png;base64,AAAA"}""";

        await AppendPublishAsync(
            database.Repository,
            database.Context,
            tenantId,
            sequence: 1,
            unsafePayload);

        var page = await database.Repository.ReadAfterAsync(
            new EventTenantId(tenantId),
            new EventCursor(0),
            10,
            CancellationToken.None);
        Assert.True(page.TryGetValue(out EventPage? events), page.Error?.Message);
        string payload = Assert.Single(events.Events).ClientPayload;

        Assert.Contains("\"assetId\":\"asset-1\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gps", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:image", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_payload_must_be_a_json_metadata_object()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using OutboxDatabase database = await OutboxDatabase.CreateAsync(tenantId);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await database.Repository.AppendAsync(
                Message(tenantId, 1, "asset.ready", "\"raw private media\""),
                CancellationToken.None));
    }

    private static async Task AppendAndSaveAsync(
        OutboxDatabase database,
        Guid tenantId,
        long sequence)
    {
        await database.Repository.AppendAsync(
            Message(tenantId, sequence, "job.completed", """{"jobId":"job-1"}"""),
            CancellationToken.None);
        await database.Context.SaveChangesAsync();
    }

    private static async Task AppendPublishAsync(
        OutboxRepository repository,
        DbContext context,
        Guid tenantId,
        long sequence,
        string payload = """{"jobId":"job-1"}""")
    {
        OutboxMessage message = Message(tenantId, sequence, "job.completed", payload);
        await repository.AppendAsync(message, CancellationToken.None);
        await context.SaveChangesAsync();
        OutboxClaim claim = Assert.Single(await repository.ClaimPendingAsync(
            Guid.CreateVersion7(),
            Now,
            TimeSpan.FromMinutes(1),
            10,
            CancellationToken.None));
        OutboxPublishResult result = await repository.PublishClaimAsync(
            message.Id,
            claim.ClaimId,
            claim.Version,
            Now.AddMinutes(1),
            CancellationToken.None);
        Assert.Equal(OutboxPublishOutcome.Published, result.Outcome);
    }

    private static OutboxMessage Message(
        Guid tenantId,
        long sequence,
        string eventType,
        string payload) =>
        OutboxMessage.Create(
            new OutboxMessageId(Guid.CreateVersion7()),
            new EventEnvelope(
                new EventMetadata(
                    new EventId(Guid.CreateVersion7()),
                    new EventTenantId(tenantId),
                    new EventSequence(sequence),
                    eventType,
                    eventVersion: 1,
                    Now,
                    correlationId: Guid.CreateVersion7()),
                payload),
            Now);
}

internal sealed class DomainStateRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Value { get; set; } = string.Empty;
}

internal sealed class OutboxTestDbContext(
    DbContextOptions<OutboxTestDbContext> options,
    Guid tenantId) : DbContext(options), IOutboxTenantContext
{
    public Guid TenantId { get; } = tenantId;
    public DbSet<DomainStateRow> DomainStates => Set<DomainStateRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DomainStateRow>(entity =>
        {
            entity.ToTable("domain_state");
            entity.HasKey(row => row.Id);
        });
        OutboxPersistenceContributor.Configure(modelBuilder, this);
    }
}

internal sealed class OutboxDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private OutboxDatabase(
        SqliteConnection connection,
        OutboxTestDbContext context)
    {
        _connection = connection;
        Context = context;
        Repository = new OutboxRepository(context, context);
    }

    internal OutboxTestDbContext Context { get; }
    internal OutboxRepository Repository { get; }

    internal static async ValueTask<OutboxDatabase> CreateAsync(Guid tenantId)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        OutboxTestDbContext context = CreateContext(connection, tenantId);
        await context.Database.EnsureCreatedAsync();
        return new OutboxDatabase(connection, context);
    }

    internal OutboxTestDbContext CreateContext(Guid tenantId) =>
        CreateContext(_connection, tenantId);

    private static OutboxTestDbContext CreateContext(
        SqliteConnection connection,
        Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<OutboxTestDbContext>()
            .UseSqlite(connection)
            .Options;
        return new OutboxTestDbContext(options, tenantId);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
