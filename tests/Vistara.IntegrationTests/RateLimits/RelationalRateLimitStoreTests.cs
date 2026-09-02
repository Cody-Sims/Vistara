using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Xunit;

namespace Vistara.IntegrationTests.RateLimits;

/// <summary>
/// The persisted rate-limit counter under real contention.
///
/// This row is the hottest one in the deployment: behind a shared ingress
/// every replica counts every request into the same key. The store therefore
/// has to admit exactly the configured limit, never more, and never turn
/// ordinary contention into a failed request.
/// </summary>
public sealed class RelationalRateLimitStoreTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 3, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string Key =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task The_first_request_opens_a_window()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();

        PersistedRateLimitDecision decision = await store.TryAcquireAsync(
            Key,
            Start,
            Window,
            3,
            CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.RetryAfter);
        Assert.Equal(1, await database.CountAsync(Key));
    }

    [Fact]
    public async Task A_full_window_is_rejected_for_exactly_what_remains_of_it()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();
        for (int request = 0; request < 3; request++)
        {
            Assert.True(
                (await store.TryAcquireAsync(
                    Key,
                    Start,
                    Window,
                    3,
                    CancellationToken.None)).IsAllowed);
        }

        PersistedRateLimitDecision rejected = await store.TryAcquireAsync(
            Key,
            Start.AddSeconds(20),
            Window,
            3,
            CancellationToken.None);

        Assert.False(rejected.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(40), rejected.RetryAfter);
        Assert.Equal(3, await database.CountAsync(Key));
    }

    [Fact]
    public async Task An_elapsed_window_starts_again_at_one()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();
        Assert.True(
            (await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                1,
                CancellationToken.None)).IsAllowed);
        Assert.False(
            (await store.TryAcquireAsync(
                Key,
                Start.AddSeconds(59),
                Window,
                1,
                CancellationToken.None)).IsAllowed);

        PersistedRateLimitDecision allowed = await store.TryAcquireAsync(
            Key,
            Start.Add(Window),
            Window,
            1,
            CancellationToken.None);

        Assert.True(allowed.IsAllowed);
        Assert.Equal(1, await database.CountAsync(Key));
        Assert.Equal(Start.Add(Window), await database.WindowStartAsync(Key));
    }

    /// <summary>
    /// The whole point of the persisted counter: replicas that each keep their
    /// own count would multiply the ceiling. Every acquisition here races
    /// against the others over separate contexts, and the deployment-wide
    /// ceiling is admitted exactly once - no overshoot, no failed request.
    /// </summary>
    [Fact]
    public async Task Replicas_racing_on_one_key_admit_exactly_the_limit()
    {
        const int Limit = 6000;
        const int Replicas = 8;
        const int AttemptsPerReplica = 800;
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        var failures = new ConcurrentBag<Exception>();
        var allowed = new int[Replicas];

        await Task.WhenAll(Enumerable.Range(0, Replicas).Select(replica =>
            Task.Run(async () =>
            {
                RelationalRateLimitStore store = database.CreateStore();
                for (int attempt = 0; attempt < AttemptsPerReplica; attempt++)
                {
                    try
                    {
                        PersistedRateLimitDecision decision =
                            await store.TryAcquireAsync(
                                Key,
                                Start,
                                Window,
                                Limit,
                                CancellationToken.None);
                        if (decision.IsAllowed)
                        {
                            allowed[replica]++;
                        }
                        else
                        {
                            Assert.NotNull(decision.RetryAfter);
                        }
                    }
                    catch (Exception failure)
                    {
                        failures.Add(failure);
                    }
                }
            })));

        Assert.Empty(failures);
        Assert.Equal(Limit, allowed.Sum());
        Assert.Equal(Limit, await database.CountAsync(Key));
    }

    /// <summary>
    /// Replicas that arrive together on a window that has just elapsed must
    /// reset it once between them, not once each.
    /// </summary>
    [Fact]
    public async Task Replicas_racing_on_an_elapsed_window_reset_it_once()
    {
        const int Limit = 200;
        const int Replicas = 8;
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore seed = database.CreateStore();
        for (int request = 0; request < Limit; request++)
        {
            Assert.True(
                (await seed.TryAcquireAsync(
                    Key,
                    Start,
                    Window,
                    Limit,
                    CancellationToken.None)).IsAllowed);
        }

        DateTimeOffset next = Start.Add(Window);
        var failures = new ConcurrentBag<Exception>();
        var allowed = new int[Replicas];
        await Task.WhenAll(Enumerable.Range(0, Replicas).Select(replica =>
            Task.Run(async () =>
            {
                RelationalRateLimitStore store = database.CreateStore();
                for (int attempt = 0; attempt < Limit / 2; attempt++)
                {
                    try
                    {
                        if ((await store.TryAcquireAsync(
                                Key,
                                next,
                                Window,
                                Limit,
                                CancellationToken.None)).IsAllowed)
                        {
                            allowed[replica]++;
                        }
                    }
                    catch (Exception failure)
                    {
                        failures.Add(failure);
                    }
                }
            })));

        Assert.Empty(failures);
        Assert.Equal(Limit, allowed.Sum());
        Assert.Equal(Limit, await database.CountAsync(Key));
        Assert.Equal(next, await database.WindowStartAsync(Key));
    }

    [Fact]
    public async Task Separate_keys_do_not_contend()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();
        string other = new('a', 64);

        Assert.True(
            (await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                1,
                CancellationToken.None)).IsAllowed);
        Assert.True(
            (await store.TryAcquireAsync(
                other,
                Start,
                Window,
                1,
                CancellationToken.None)).IsAllowed);
        Assert.False(
            (await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                1,
                CancellationToken.None)).IsAllowed);
    }

    /// <summary>
    /// A caller that went away is not a rate-limit decision, so cancellation
    /// is surfaced rather than counted or swallowed.
    /// </summary>
    [Fact]
    public async Task Cancellation_is_surfaced_to_the_caller()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                10,
                cancellation.Token));
    }

    /// <summary>
    /// A database that is not there is a fault, not a throttling decision. It
    /// must not be mistaken for contention and retried into silence.
    /// </summary>
    [Fact]
    public async Task A_provider_failure_is_not_mistaken_for_contention()
    {
        await using RateLimitCatalogDbContext context = new(
            new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
                .UseSqlite("Data Source=missing;Mode=Memory")
                .Options);
        var store = new RelationalRateLimitStore(context);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                10,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-digest")]
    public async Task A_key_that_is_not_a_digest_is_refused(string key)
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await store.TryAcquireAsync(
                key,
                Start,
                Window,
                10,
                CancellationToken.None));
    }

    [Fact]
    public async Task A_local_timestamp_is_refused()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.TryAcquireAsync(
                Key,
                new DateTimeOffset(2026, 3, 14, 8, 0, 0, TimeSpan.FromHours(2)),
                Window,
                10,
                CancellationToken.None));
    }

    [Fact]
    public async Task A_window_or_limit_that_is_not_positive_is_refused()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        RelationalRateLimitStore store = database.CreateStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.TryAcquireAsync(
                Key,
                Start,
                TimeSpan.Zero,
                10,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                0,
                CancellationToken.None));
    }

    /// <summary>
    /// Contention that never clears is still not a broken deployment. The
    /// last classified attempt refuses the request for the length of the
    /// window instead of letting the fault escape as a failed request: a
    /// caller being throttled is told to retry, not told the server broke.
    /// </summary>
    [Fact]
    public async Task Unrelenting_contention_refuses_rather_than_fails()
    {
        var contention = new FaultInterceptor(
            () => new SqliteException("database is locked", 5));
        await using RateLimitCatalogDbContext context = new(
            new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
                .UseSqlite("Data Source=:memory:")
                .AddInterceptors(contention)
                .Options);
        var store = new RelationalRateLimitStore(context);

        PersistedRateLimitDecision decision = await store.TryAcquireAsync(
            Key,
            Start,
            Window,
            10,
            CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(Window, decision.RetryAfter);
        Assert.True(
            contention.Faults > 1,
            "Contention should be retried before the request is refused.");
    }

    /// <summary>
    /// A constraint violation on every attempt is the same story: the row is
    /// contended, not the deployment broken.
    /// </summary>
    [Fact]
    public async Task An_unrelenting_constraint_violation_refuses_rather_than_fails()
    {
        var contention = new FaultInterceptor(
            () => new SqliteException("UNIQUE constraint failed", 19));
        await using RateLimitCatalogDbContext context = new(
            new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
                .UseSqlite("Data Source=:memory:")
                .AddInterceptors(contention)
                .Options);
        var store = new RelationalRateLimitStore(context);

        PersistedRateLimitDecision decision = await store.TryAcquireAsync(
            Key,
            Start,
            TimeSpan.FromSeconds(30),
            10,
            CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.RetryAfter);
    }

    /// <summary>
    /// A fault that is not contention is not retried into a throttling
    /// decision: it is the deployment's problem and it surfaces.
    /// </summary>
    [Fact]
    public async Task A_fault_that_is_not_contention_still_surfaces()
    {
        var broken = new FaultInterceptor(
            () => new SqliteException("no such table", 1));
        await using RateLimitCatalogDbContext context = new(
            new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
                .UseSqlite("Data Source=:memory:")
                .AddInterceptors(broken)
                .Options);
        var store = new RelationalRateLimitStore(context);

        await Assert.ThrowsAnyAsync<SqliteException>(
            async () => await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                10,
                CancellationToken.None));
        Assert.Equal(1, broken.Faults);
    }

    /// <summary>
    /// Cancellation during contention is cancellation, not a throttling
    /// decision.
    /// </summary>
    [Fact]
    public async Task Cancellation_during_contention_is_surfaced()
    {
        using var cancellation = new CancellationTokenSource();
        var contention = new FaultInterceptor(
            () => new SqliteException("database is locked", 5),
            onFault: cancellation.Cancel);
        await using RateLimitCatalogDbContext context = new(
            new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
                .UseSqlite("Data Source=:memory:")
                .AddInterceptors(contention)
                .Options);
        var store = new RelationalRateLimitStore(context);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.TryAcquireAsync(
                Key,
                Start,
                Window,
                10,
                cancellation.Token));
    }

    /// <summary>
    /// Fails every statement the store issues with one chosen fault, so the
    /// classification is exercised rather than waited for.
    /// </summary>
    private sealed class FaultInterceptor(
        Func<DbException> fault,
        Action? onFault = null) : DbCommandInterceptor
    {
        private int _faults;

        internal int Faults => Volatile.Read(ref _faults);

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result) => throw Fail();

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw Fail();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result) => throw Fail();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) => throw Fail();

        private DbException Fail()
        {
            Interlocked.Increment(ref _faults);
            onFault?.Invoke();
            return fault();
        }
    }

    /// <summary>
    /// A file-backed SQLite database in write-ahead logging mode, so the
    /// contention the tests create is the database's own and not an artefact
    /// of a shared in-memory cache.
    /// </summary>
    private sealed class RateLimitDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _directory;
        private readonly List<RateLimitCatalogDbContext> _contexts = [];

        private RateLimitDatabase(string connectionString, string directory)
        {
            _connectionString = connectionString;
            _directory = directory;
        }

        internal static async ValueTask<RateLimitDatabase> CreateAsync()
        {
            string directory = Path.Combine(
                AppContext.BaseDirectory,
                $"rate-limit-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string connectionString =
                $"Data Source={Path.Combine(directory, "rate-limits.db")}";
            await using VistaraDbContext schema = new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(connectionString)
                    .Options,
                new FixedTenantScope(Guid.CreateVersion7()));
            await schema.Database.EnsureCreatedAsync(CancellationToken.None);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            return new RateLimitDatabase(connectionString, directory);
        }

        internal RelationalRateLimitStore CreateStore()
        {
            RateLimitCatalogDbContext context = CreateContext();
            lock (_contexts)
            {
                _contexts.Add(context);
            }

            return new RelationalRateLimitStore(context);
        }

        internal async Task<int> CountAsync(string keyHash)
        {
            await using RateLimitCatalogDbContext context = CreateContext();
            return await context.Windows
                .AsNoTracking()
                .Where(row => row.KeyHash == keyHash)
                .Select(row => row.RequestCount)
                .SingleAsync(CancellationToken.None);
        }

        internal async Task<DateTimeOffset> WindowStartAsync(string keyHash)
        {
            await using RateLimitCatalogDbContext context = CreateContext();
            return await context.Windows
                .AsNoTracking()
                .Where(row => row.KeyHash == keyHash)
                .Select(row => row.WindowStartedAtUtc)
                .SingleAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (RateLimitCatalogDbContext context in _contexts)
            {
                await context.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private RateLimitCatalogDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
                .UseSqlite(_connectionString)
                .Options);
    }
}
