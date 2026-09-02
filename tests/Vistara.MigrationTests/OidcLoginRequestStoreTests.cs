using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.MigrationTests;

/// <summary>
/// Exercises <see cref="RelationalOidcLoginRequestStore"/> against a real
/// migrated SQLite database. The table carries the single-use browser login
/// state for the hosted OIDC entry path, so replay, expiry, and concurrency are
/// behavior gates rather than review notes.
/// </summary>
public sealed class OidcLoginRequestStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Created_request_is_consumed_exactly_once()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();
        OidcLoginRequest request = NewRequest();

        Assert.True(await fixture.Store.CreateAsync(request, CancellationToken.None));

        ConsumedOidcLoginRequest? first = await fixture.Store.ConsumeAsync(
            request.StateDigest,
            Now.AddSeconds(30),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("entra", first.ProviderId);
        Assert.Equal(request.CodeVerifier, first.CodeVerifier);
        Assert.Equal(request.RedirectUri, first.RedirectUri);
        Assert.Equal("/library?view=grid", first.ReturnTo);
        Assert.Equal(request.NonceDigest, first.NonceDigest);
        Assert.Equal(request.HandleDigest, first.HandleDigest);
        Assert.Equal(Now, first.CreatedAtUtc);
        Assert.Equal(Now.AddMinutes(10), first.ExpiresAtUtc);
        Assert.Equal(Now.AddSeconds(30), first.ConsumedAtUtc);
        Assert.True(RelationalOidcLoginRequestStore.HandleMatches(
            first,
            request.HandleDigest));
        Assert.False(RelationalOidcLoginRequestStore.HandleMatches(
            first,
            Digest("another-browser")));

        Assert.Null(await fixture.Store.ConsumeAsync(
            request.StateDigest,
            Now.AddSeconds(40),
            CancellationToken.None));
    }

    [Fact]
    public async Task Replayed_state_never_reveals_the_verifier_again()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();
        OidcLoginRequest request = NewRequest();
        await fixture.Store.CreateAsync(request, CancellationToken.None);
        _ = await fixture.Store.ConsumeAsync(
            request.StateDigest,
            Now.AddSeconds(1),
            CancellationToken.None);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await fixture.Store.ConsumeAsync(
                request.StateDigest,
                Now.AddSeconds(2 + attempt),
                CancellationToken.None));
        }

        Assert.Equal(
            Now.AddSeconds(1),
            await fixture.ReadConsumedAtAsync(request.StateDigest));
    }

    [Fact]
    public async Task Expired_request_is_never_consumed()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();
        OidcLoginRequest request = NewRequest();
        await fixture.Store.CreateAsync(request, CancellationToken.None);

        Assert.Null(await fixture.Store.ConsumeAsync(
            request.StateDigest,
            Now.AddMinutes(10),
            CancellationToken.None));
        Assert.Null(await fixture.Store.ConsumeAsync(
            request.StateDigest,
            Now.AddMinutes(11),
            CancellationToken.None));

        Assert.Null(await fixture.ReadConsumedAtAsync(request.StateDigest));
    }

    [Fact]
    public async Task Unknown_state_digest_is_indistinguishable_from_replay()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();

        Assert.Null(await fixture.Store.ConsumeAsync(
            Digest("never-issued"),
            Now,
            CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_state_digest_is_rejected_without_overwriting()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();
        OidcLoginRequest first = NewRequest();
        OidcLoginRequest second = first with
        {
            CodeVerifier = new string('b', 64),
            ReturnTo = "/albums",
        };

        Assert.True(await fixture.Store.CreateAsync(first, CancellationToken.None));
        Assert.False(await fixture.Store.CreateAsync(second, CancellationToken.None));

        ConsumedOidcLoginRequest? consumed = await fixture.Store.ConsumeAsync(
            first.StateDigest,
            Now.AddSeconds(5),
            CancellationToken.None);
        Assert.NotNull(consumed);
        Assert.Equal(first.CodeVerifier, consumed.CodeVerifier);
        Assert.Equal(first.ReturnTo, consumed.ReturnTo);
        Assert.Equal(1, await fixture.CountRowsAsync());
    }

    [Fact]
    public async Task Concurrent_consumers_produce_exactly_one_winner()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync(shared: true);
        OidcLoginRequest request = NewRequest();
        await fixture.Store.CreateAsync(request, CancellationToken.None);

        const int attempts = 8;
        RelationalOidcLoginRequestStore[] stores = Enumerable.Range(0, attempts)
            .Select(_ => fixture.CreateIsolatedStore())
            .ToArray();
        using var gate = new SemaphoreSlim(0, attempts);
        Task<ConsumedOidcLoginRequest?>[] races = stores
            .Select(store => Task.Run(async () =>
            {
                await gate.WaitAsync(CancellationToken.None);
                return await store.ConsumeAsync(
                    request.StateDigest,
                    Now.AddSeconds(5),
                    CancellationToken.None);
            }))
            .ToArray();
        gate.Release(attempts);

        ConsumedOidcLoginRequest?[] results = await Task.WhenAll(races);

        ConsumedOidcLoginRequest winner = Assert.Single(
            results,
            result => result is not null)!;
        Assert.Equal(request.CodeVerifier, winner.CodeVerifier);
        Assert.Equal(Now.AddSeconds(5), winner.ConsumedAtUtc);
    }

    [Fact]
    public async Task Sweep_removes_only_expired_rows_and_stays_bounded()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();
        for (int index = 0; index < 5; index++)
        {
            await fixture.Store.CreateAsync(
                NewRequest() with
                {
                    StateDigest = Digest($"expired-{index}"),
                    CreatedAtUtc = Now.AddMinutes(-100),
                    ExpiresAtUtc = Now.AddMinutes(-90),
                },
                CancellationToken.None);
        }

        OidcLoginRequest live = NewRequest() with { StateDigest = Digest("live") };
        await fixture.Store.CreateAsync(live, CancellationToken.None);

        Assert.Equal(
            2,
            await fixture.Store.DeleteExpiredAsync(
                Now,
                maximumRows: 2,
                CancellationToken.None));
        Assert.Equal(
            3,
            await fixture.Store.DeleteExpiredAsync(
                Now,
                maximumRows: 100,
                CancellationToken.None));
        Assert.Equal(
            0,
            await fixture.Store.DeleteExpiredAsync(
                Now,
                maximumRows: 100,
                CancellationToken.None));
        Assert.Equal(1, await fixture.CountRowsAsync());
        Assert.NotNull(await fixture.Store.ConsumeAsync(
            live.StateDigest,
            Now.AddSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_only_removes_rows_older_than_the_supplied_threshold()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();
        OidcLoginRequest recentlyExpired = NewRequest() with
        {
            CreatedAtUtc = Now.AddMinutes(-11),
            ExpiresAtUtc = Now.AddMinutes(-1),
        };
        await fixture.Store.CreateAsync(
            recentlyExpired,
            CancellationToken.None);

        Assert.Equal(
            0,
            await fixture.Store.DeleteExpiredAsync(
                Now.AddHours(-1),
                maximumRows: 100,
                CancellationToken.None));
        Assert.Equal(
            1,
            await fixture.Store.DeleteExpiredAsync(
                Now,
                maximumRows: 100,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Sweep_rejects_an_unbounded_batch(int maximumRows)
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await fixture.Store.DeleteExpiredAsync(
                Now,
                maximumRows,
                CancellationToken.None));
    }

    [Fact]
    public async Task Consume_rejects_a_digest_that_is_not_sha256_sized()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Store.ConsumeAsync(
                [1, 2, 3],
                Now,
                CancellationToken.None));
    }

    [Fact]
    public async Task Rejected_request_shapes_never_reach_the_database()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();

        foreach ((string reason, OidcLoginRequest request) in RejectedRequests())
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(
                async () => await fixture.Store.CreateAsync(
                    request,
                    CancellationToken.None));
            Assert.Equal(0, await fixture.CountRowsAsync());
            Assert.NotEmpty(reason);
        }
    }

    [Fact]
    public async Task Stored_row_never_holds_raw_state_nonce_or_handle_material()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();
        const string state = "state-secret-value";
        const string nonce = "nonce-secret-value";
        const string handle = "handle-secret-value";
        OidcLoginRequest request = NewRequest() with
        {
            StateDigest = Digest(state),
            NonceDigest = Digest(nonce),
            HandleDigest = Digest(handle),
        };
        await fixture.Store.CreateAsync(request, CancellationToken.None);

        string dump = await fixture.DumpRowsAsync();

        Assert.DoesNotContain(state, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(nonce, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(handle, dump, StringComparison.Ordinal);
        Assert.Contains(
            Convert.ToHexString(Digest(state)),
            dump,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Table_matches_the_specified_ten_column_shape()
    {
        await using OidcLoginRequestFixture fixture =
            await OidcLoginRequestFixture.CreateAsync();

        (string Name, string Type, bool NotNull, bool PrimaryKey)[] columns =
            await fixture.ReadColumnsAsync();

        Assert.Equal(
            [
                ("code_verifier", "TEXT", true, false),
                ("consumed_at_utc", "TEXT", false, false),
                ("created_at_utc", "TEXT", true, false),
                ("expires_at_utc", "TEXT", true, false),
                ("handle_digest", "BLOB", true, false),
                ("nonce_digest", "BLOB", true, false),
                ("provider_id", "TEXT", true, false),
                ("redirect_uri", "TEXT", true, false),
                ("return_to", "TEXT", true, false),
                ("state_digest", "BLOB", true, true),
            ],
            columns);
        Assert.DoesNotContain(
            columns,
            column => column.Name.Contains("tenant", StringComparison.Ordinal));
    }

    private static IEnumerable<(string Reason, OidcLoginRequest Request)>
        RejectedRequests()
    {
        yield return ("short state digest", NewRequest() with { StateDigest = [1, 2, 3] });
        yield return ("empty nonce digest", NewRequest() with { NonceDigest = [] });
        yield return ("long handle digest", NewRequest() with { HandleDigest = new byte[64] });
        yield return ("empty provider", NewRequest() with { ProviderId = "" });
        yield return ("long provider", NewRequest() with { ProviderId = new string('p', 33) });
        yield return ("unsafe provider", NewRequest() with { ProviderId = "entra id" });
        yield return ("short verifier", NewRequest() with { CodeVerifier = new string('a', 42) });
        yield return ("long verifier", NewRequest() with { CodeVerifier = new string('a', 129) });
        yield return (
            "verifier charset",
            NewRequest() with { CodeVerifier = new string('a', 50) + "%" });
        yield return (
            "relative redirect",
            NewRequest() with { RedirectUri = "/relative/callback" });
        yield return (
            "long redirect",
            NewRequest() with { RedirectUri = "https://host/" + new string('p', 2048) });
        yield return ("protocol relative returnTo", NewRequest() with { ReturnTo = "//evil.example" });
        yield return ("absolute returnTo", NewRequest() with { ReturnTo = "https://evil.example/" });
        yield return (
            "header injection returnTo",
            NewRequest() with { ReturnTo = "/library\r\nSet-Cookie: x=1" });
        yield return (
            "backslash returnTo",
            NewRequest() with { ReturnTo = "/library\\..\\admin" });
        yield return ("rootless returnTo", NewRequest() with { ReturnTo = "library" });
        yield return ("empty returnTo", NewRequest() with { ReturnTo = "" });
        yield return (
            "long returnTo",
            NewRequest() with { ReturnTo = "/" + new string('r', 2048) });
        yield return ("instant expiry", NewRequest() with { ExpiresAtUtc = Now });
        yield return ("past expiry", NewRequest() with { ExpiresAtUtc = Now.AddMinutes(-1) });
    }

    private static OidcLoginRequest NewRequest() =>
        new(
            StateDigest: Digest("state"),
            ProviderId: "entra",
            NonceDigest: Digest("nonce"),
            HandleDigest: Digest("handle"),
            CodeVerifier: new string('a', 64),
            RedirectUri: "https://vistara.example/api/v1/auth/oidc/entra/callback",
            ReturnTo: "/library?view=grid",
            CreatedAtUtc: Now,
            ExpiresAtUtc: Now.AddMinutes(10));

    private static byte[] Digest(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private sealed class OidcLoginRequestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly string? _databaseFile;
        private readonly List<AuthenticationCatalogDbContext> _contexts = [];

        private OidcLoginRequestFixture(
            SqliteConnection connection,
            string connectionString,
            string? databaseFile)
        {
            _connection = connection;
            _connectionString = connectionString;
            _databaseFile = databaseFile;
            Store = new RelationalOidcLoginRequestStore(CreateCatalog());
        }

        internal RelationalOidcLoginRequestStore Store { get; }

        internal static async Task<OidcLoginRequestFixture> CreateAsync(
            bool shared = false)
        {
            string? databaseFile = shared
                ? Path.Combine(
                    AppContext.BaseDirectory,
                    $"oidc-login-requests-{Guid.NewGuid():N}.db")
                : null;
            string connectionString = databaseFile is null
                ? "Data Source=:memory:"
                : $"Data Source={databaseFile}";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using VistaraDbContext context =
                MigrationTestSupport.CreateSqliteContext(connection);
            await context.Database.MigrateAsync();
            return new OidcLoginRequestFixture(
                connection,
                connectionString,
                databaseFile);
        }

        internal RelationalOidcLoginRequestStore CreateIsolatedStore()
        {
            if (_databaseFile is null)
            {
                throw new InvalidOperationException(
                    "Isolated stores need a file-backed database.");
            }

            return new RelationalOidcLoginRequestStore(CreateCatalog(isolated: true));
        }

        internal Task<int> CountRowsAsync() =>
            _contexts[0].Set<OidcLoginRequestRow>().AsNoTracking().CountAsync();

        internal Task<DateTimeOffset?> ReadConsumedAtAsync(byte[] stateDigest) =>
            _contexts[0].Set<OidcLoginRequestRow>()
                .AsNoTracking()
                .Where(row => row.StateDigest == stateDigest)
                .Select(row => row.ConsumedAtUtc)
                .SingleAsync();

        internal async Task<(string Name, string Type, bool NotNull, bool PrimaryKey)[]>
            ReadColumnsAsync()
        {
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT name, type, "notnull", pk
                FROM pragma_table_info('oidc_login_requests')
                ORDER BY name;
                """;
            var columns = new List<(string, string, bool, bool)>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2) == 1,
                    reader.GetInt32(3) == 1));
            }

            return columns.ToArray();
        }

        internal async Task<string> DumpRowsAsync()
        {
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT hex(state_digest) || '|' || provider_id || '|' ||
                       hex(nonce_digest) || '|' || hex(handle_digest) || '|' ||
                       code_verifier || '|' || redirect_uri || '|' || return_to ||
                       '|' || created_at_utc || '|' || expires_at_utc
                FROM oidc_login_requests;
                """;
            var builder = new StringBuilder();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builder.AppendLine(reader.GetString(0));
            }

            return builder.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (AuthenticationCatalogDbContext context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
            if (_databaseFile is not null)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(_databaseFile);
            }
        }

        private AuthenticationCatalogDbContext CreateCatalog(bool isolated = false)
        {
            var options = new DbContextOptionsBuilder<AuthenticationCatalogDbContext>();
            if (isolated)
            {
                options.UseSqlite(_connectionString);
            }
            else
            {
                options.UseSqlite(_connection);
            }

            var context = new AuthenticationCatalogDbContext(options.Options);
            _contexts.Add(context);
            return context;
        }
    }
}
