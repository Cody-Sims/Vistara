using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vistara.Persistence.Auth;
using Xunit;

namespace Vistara.IntegrationTests.RateLimits;

/// <summary>
/// The rate-limit counter is only atomic if the database evaluates the
/// conditions, so what the PostgreSQL provider actually generates matters as
/// much as what SQLite does with it. Nothing here needs a server: the
/// connection is never opened and the generated command is captured instead of
/// executed.
/// </summary>
public sealed class RateLimitStoreNpgsqlTranslationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 14, 8, 0, 0, TimeSpan.Zero);

    private const string Key =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The ordinary path is one conditional UPDATE. A second round trip would
    /// mean the decision was made in this process instead of in the database.
    /// </summary>
    [Fact]
    public async Task Counting_a_request_is_one_conditional_update()
    {
        var capture = new CommandCapture(affectedRows: 1);
        await using RateLimitCatalogDbContext context = CreateContext(capture);
        var store = new RelationalRateLimitStore(context);

        PersistedRateLimitDecision decision = await store.TryAcquireAsync(
            Key,
            Now,
            TimeSpan.FromMinutes(1),
            6000,
            CancellationToken.None);

        Assert.True(decision.IsAllowed);
        string sql = Assert.Single(capture.Commands);
        Assert.StartsWith("UPDATE", sql, StringComparison.Ordinal);
        Assert.Contains("rate_limit_windows", sql, StringComparison.Ordinal);
        Assert.Contains("request_count", sql, StringComparison.Ordinal);
        Assert.Contains("version", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.Contains("key_hash", sql, StringComparison.Ordinal);
        Assert.Contains("window_started_at_utc", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// When no live window is under its limit, the provider is asked to
    /// restart an elapsed one - again as a single conditional statement, so
    /// two replicas arriving together cannot both restart it.
    /// </summary>
    [Fact]
    public async Task Restarting_an_elapsed_window_is_one_conditional_update()
    {
        var capture = new CommandCapture(affectedRows: 0, secondAffectedRows: 1);
        await using RateLimitCatalogDbContext context = CreateContext(capture);
        var store = new RelationalRateLimitStore(context);

        PersistedRateLimitDecision decision = await store.TryAcquireAsync(
            Key,
            Now,
            TimeSpan.FromMinutes(1),
            6000,
            CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Equal(2, capture.Commands.Count);
        string sql = capture.Commands[1];
        Assert.StartsWith("UPDATE", sql, StringComparison.Ordinal);
        Assert.Contains("rate_limit_windows", sql, StringComparison.Ordinal);
        Assert.Contains("window_started_at_utc", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", sql, StringComparison.Ordinal);
    }

    private static RateLimitCatalogDbContext CreateContext(CommandCapture capture) =>
        new(new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
            .UseNpgsql(
                "Host=postgres.invalid;Database=vistara;Username=vistara;Password=x")
            .AddInterceptors(capture)
            .Options);

    /// <summary>
    /// Captures the generated command and answers it without a server: the
    /// connection is never opened and the statement is never executed.
    /// </summary>
    private sealed class CommandCapture(
        int affectedRows,
        int? secondAffectedRows = null) :
        DbCommandInterceptor, IDbConnectionInterceptor
    {
        private readonly List<string> _commands = [];

        internal List<string> Commands => _commands;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            int index = _commands.Count;
            _commands.Add(command.CommandText);
            int affected = index == 0
                ? affectedRows
                : secondAffectedRows ?? affectedRows;
            return ValueTask.FromResult(
                InterceptionResult<int>.SuppressWithResult(affected));
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ArgumentNullException.ThrowIfNull(command);
            int index = _commands.Count;
            _commands.Add(command.CommandText);
            int affected = index == 0
                ? affectedRows
                : secondAffectedRows ?? affectedRows;
            return InterceptionResult<int>.SuppressWithResult(affected);
        }

        public InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) => InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public InterceptionResult ConnectionClosing(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) => InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }
}
