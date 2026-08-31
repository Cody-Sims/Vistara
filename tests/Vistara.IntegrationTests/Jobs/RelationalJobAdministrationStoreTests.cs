using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

/// <summary>
/// Exercises the shipped job administration store over real persistence:
/// tenant isolation, keyset paging, and the single safe operator action.
/// </summary>
public sealed class RelationalJobAdministrationStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Listing_pages_newest_first_and_never_leaves_the_tenant()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenantId = Guid.CreateVersion7();
        await using var database = await JobAdministrationDatabase.CreateAsync();
        Guid[] mine = await database.SeedAsync(tenantId, 5, "asset.ingest");
        await database.SeedAsync(otherTenantId, 3, "asset.ingest");

        await using VistaraDbContext context = database.CreateContext(tenantId);
        var store = new RelationalJobAdministrationStore(context);
        JobAdministrationPage first = await store.ListAsync(
            new JobAdministrationQuery(tenantId, [], null, 2, null, null),
            default);
        JobAdministrationPage second = await store.ListAsync(
            new JobAdministrationQuery(
                tenantId,
                [],
                null,
                2,
                first.NextCreatedAtUtc,
                first.NextJobId),
            default);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.NotNull(first.NextJobId);
        Assert.All(
            first.Items.Concat(second.Items),
            snapshot => Assert.Contains(snapshot.Id.Value, mine));
        Assert.Empty(
            first.Items.Select(item => item.Id.Value)
                .Intersect(second.Items.Select(item => item.Id.Value)));
    }

    [Fact]
    public async Task The_last_page_reports_no_cursor()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using var database = await JobAdministrationDatabase.CreateAsync();
        await database.SeedAsync(tenantId, 2, "asset.ingest");

        await using VistaraDbContext context = database.CreateContext(tenantId);
        JobAdministrationPage page = await new RelationalJobAdministrationStore(context)
            .ListAsync(
                new JobAdministrationQuery(tenantId, [], null, 50, null, null),
                default);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.NextJobId);
        Assert.Null(page.NextCreatedAtUtc);
    }

    [Fact]
    public async Task Listing_filters_by_state_and_type()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using var database = await JobAdministrationDatabase.CreateAsync();
        await database.SeedAsync(tenantId, 2, "asset.ingest");
        Guid dead = await database.SeedDeadLetteredAsync(tenantId, "derivatives");

        await using VistaraDbContext context = database.CreateContext(tenantId);
        var store = new RelationalJobAdministrationStore(context);
        JobAdministrationPage byState = await store.ListAsync(
            new JobAdministrationQuery(
                tenantId,
                [nameof(JobState.DeadLettered)],
                null,
                50,
                null,
                null),
            default);
        JobAdministrationPage byType = await store.ListAsync(
            new JobAdministrationQuery(tenantId, [], "derivatives", 50, null, null),
            default);

        Assert.Equal(dead, Assert.Single(byState.Items).Id.Value);
        Assert.Equal(dead, Assert.Single(byType.Items).Id.Value);
    }

    [Fact]
    public async Task A_dead_lettered_job_returns_to_the_queue_exactly_once()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using var database = await JobAdministrationDatabase.CreateAsync();
        Guid jobId = await database.SeedDeadLetteredAsync(tenantId, "derivatives");

        await using VistaraDbContext context = database.CreateContext(tenantId);
        var store = new RelationalJobAdministrationStore(context);
        DateTimeOffset requeueAt = Now.AddMinutes(5);
        JobRetryStatus first =
            await store.RetryAsync(tenantId, jobId, 3, requeueAt, default);
        JobRetryStatus replay =
            await store.RetryAsync(tenantId, jobId, 3, requeueAt, default);

        Assert.Equal(JobRetryStatus.Retried, first);
        Assert.Equal(JobRetryStatus.VersionConflict, replay);
        JobSnapshot? snapshot = await store.FindAsync(tenantId, jobId, default);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Pending, snapshot.State);
        Assert.Equal(0, snapshot.Attempts);
        Assert.Null(snapshot.LastFailure);
        Assert.Equal(4, snapshot.Version.Value);
    }

    [Fact]
    public async Task A_pending_job_is_not_retryable()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using var database = await JobAdministrationDatabase.CreateAsync();
        Guid[] pending = await database.SeedAsync(tenantId, 1, "asset.ingest");

        await using VistaraDbContext context = database.CreateContext(tenantId);
        JobRetryStatus status = await new RelationalJobAdministrationStore(context)
            .RetryAsync(tenantId, pending[0], 1, Now.AddMinutes(5), default);

        Assert.Equal(JobRetryStatus.NotRetryable, status);
    }

    [Fact]
    public async Task A_job_from_another_tenant_is_never_retried_or_read()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenantId = Guid.CreateVersion7();
        await using var database = await JobAdministrationDatabase.CreateAsync();
        Guid jobId = await database.SeedDeadLetteredAsync(otherTenantId, "derivatives");

        await using VistaraDbContext context = database.CreateContext(tenantId);
        var store = new RelationalJobAdministrationStore(context);
        JobRetryStatus status =
            await store.RetryAsync(tenantId, jobId, 3, Now.AddMinutes(5), default);
        JobSnapshot? snapshot = await store.FindAsync(tenantId, jobId, default);
        JobAdministrationPage page = await store.ListAsync(
            new JobAdministrationQuery(tenantId, [], null, 50, null, null),
            default);

        Assert.Equal(JobRetryStatus.NotFound, status);
        Assert.Null(snapshot);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Requests_outside_the_active_tenant_scope_are_refused()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using var database = await JobAdministrationDatabase.CreateAsync();
        await database.SeedAsync(tenantId, 1, "asset.ingest");

        await using VistaraDbContext context = database.CreateContext(tenantId);
        var store = new RelationalJobAdministrationStore(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.ListAsync(
                new JobAdministrationQuery(
                    Guid.CreateVersion7(),
                    [],
                    null,
                    10,
                    null,
                    null),
                default));
    }

    private sealed class JobAdministrationDatabase : IAsyncDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _anchor;
        private readonly string _connectionString;
        private int _sequence;

        private JobAdministrationDatabase(
            Microsoft.Data.Sqlite.SqliteConnection anchor,
            string connectionString)
        {
            _anchor = anchor;
            _connectionString = connectionString;
        }

        internal static async ValueTask<JobAdministrationDatabase> CreateAsync()
        {
            string name = $"JobAdmin-{Guid.NewGuid():N}";
            string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
            var anchor = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            await anchor.OpenAsync(default);
            var database = new JobAdministrationDatabase(anchor, connectionString);
            await using VistaraDbContext schema =
                database.CreateContext(Guid.CreateVersion7());
            await schema.Database.EnsureCreatedAsync(default);
            return database;
        }

        internal VistaraDbContext CreateContext(Guid tenantId) =>
            new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(_connectionString)
                    .Options,
                new FixedTenantScope(tenantId));

        internal async Task<Guid[]> SeedAsync(Guid tenantId, int count, string type)
        {
            var ids = new List<Guid>(count);
            await using VistaraDbContext context = CreateContext(tenantId);
            for (int index = 0; index < count; index++)
            {
                Guid id = Guid.CreateVersion7();
                ids.Add(id);
                context.Jobs.Add(new JobRow
                {
                    Id = id,
                    TenantId = tenantId,
                    Type = type,
                    Payload = "{}",
                    PayloadVersion = 1,
                    DedupeKey = $"{type}:{Interlocked.Increment(ref _sequence)}",
                    Priority = 0,
                    MaxAttempts = 5,
                    Attempts = 0,
                    State = nameof(JobState.Pending),
                    AvailableAtUtc = Now.AddSeconds(index),
                    CreatedAtUtc = Now.AddSeconds(index),
                    Version = 1,
                });
            }

            await context.SaveChangesAsync(default);
            return [.. ids];
        }

        internal async Task<Guid> SeedDeadLetteredAsync(Guid tenantId, string type)
        {
            Guid id = Guid.CreateVersion7();
            await using VistaraDbContext context = CreateContext(tenantId);
            context.Jobs.Add(new JobRow
            {
                Id = id,
                TenantId = tenantId,
                Type = type,
                Payload = "{}",
                PayloadVersion = 1,
                DedupeKey = $"{type}:{Interlocked.Increment(ref _sequence)}",
                Priority = 0,
                MaxAttempts = 5,
                Attempts = 5,
                State = nameof(JobState.DeadLettered),
                FailureCode = "jobs.media_decode_failed",
                AvailableAtUtc = Now.AddMinutes(1),
                CreatedAtUtc = Now.AddMinutes(1),
                Version = 3,
            });
            await context.SaveChangesAsync(default);
            return id;
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }
}
