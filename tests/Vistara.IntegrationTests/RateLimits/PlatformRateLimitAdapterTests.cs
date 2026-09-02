using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vistara.Api.Composition.Platform;
using Vistara.Application.Common;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Xunit;

namespace Vistara.IntegrationTests.RateLimits;

/// <summary>
/// Behaviour of the persisted rate-limit ceiling against a real store.
///
/// The configured limit and window have to reach the counter, the counter has
/// to be shared by every replica, and the bucket key has to stay the transport
/// peer: a caller that could partition the limit with a header it controls
/// would have no limit at all.
/// </summary>
public sealed class PlatformRateLimitAdapterTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 3, 14, 8, 0, 0, TimeSpan.Zero);

    private const string Client = "203.0.113.10";

    /// <summary>
    /// The unconfigured events bucket still admits exactly the thirty requests
    /// a minute it admitted before the limits became configuration.
    /// </summary>
    [Fact]
    public async Task The_shipped_events_limit_is_unchanged_by_default()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        var clock = new MutableClock(Start);
        PlatformRateLimitPersistenceAdapter adapter = database.CreateAdapter(
            clock,
            new PlatformRateLimitOptions());

        for (int request = 0; request < 30; request++)
        {
            Assert.True(
                (await Check(adapter, "/api/v1/events")).IsAllowed,
                $"Request {request + 1} of the shipped allowance was rejected.");
        }

        PlatformRateLimitDecision rejected = await Check(adapter, "/api/v1/events");
        Assert.False(rejected.IsAllowed);
        Assert.NotNull(rejected.RetryAfter);
        Assert.InRange(
            rejected.RetryAfter!.Value,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The persisted key is part of the deployment's state, so the default
    /// configuration has to produce the same key the previous constant-limited
    /// adapter produced. A changed key silently resets every live window.
    /// </summary>
    [Fact]
    public async Task The_persisted_key_is_unchanged_by_default()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        PlatformRateLimitPersistenceAdapter adapter = database.CreateAdapter(
            new MutableClock(Start),
            new PlatformRateLimitOptions());

        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);
        Assert.True((await Check(adapter, "/delivery/asset")).IsAllowed);
        Assert.True((await Check(adapter, "/media/asset")).IsAllowed);
        Assert.True((await Check(adapter, "/api/v1/assets")).IsAllowed);

        string[] stored = await database.KeyHashesAsync();

        Assert.Equal(
            new[]
            {
                LegacyKeyHash("api", Client),
                LegacyKeyHash("delivery", Client),
                LegacyKeyHash("events", Client),
                LegacyKeyHash("media", Client),
            }.Order(StringComparer.Ordinal),
            stored.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_request_outside_every_bucket_is_never_counted()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        PlatformRateLimitPersistenceAdapter adapter = database.CreateAdapter(
            new MutableClock(Start),
            new PlatformRateLimitOptions());

        Assert.True((await Check(adapter, "/health/live")).IsAllowed);

        Assert.Empty(await database.KeyHashesAsync());
    }

    /// <summary>
    /// The configured limit is the one the store enforces, and the configured
    /// window is the one it enforces it over.
    /// </summary>
    [Fact]
    public async Task The_configured_limit_and_window_are_the_ones_enforced()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        var clock = new MutableClock(Start);
        PlatformRateLimitPersistenceAdapter adapter = database.CreateAdapter(
            clock,
            new PlatformRateLimitOptions
            {
                Window = TimeSpan.FromSeconds(30),
                Events = 3,
            });

        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);
        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);
        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);

        PlatformRateLimitDecision rejected = await Check(adapter, "/api/v1/events");
        Assert.False(rejected.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(30), rejected.RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False((await Check(adapter, "/api/v1/events")).IsAllowed);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);
    }

    /// <summary>
    /// A raised bucket must not raise the others: an elevated events ceiling
    /// is not permission to serve an unlimited number of deliveries.
    /// </summary>
    [Fact]
    public async Task Each_bucket_is_counted_against_its_own_configured_limit()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        PlatformRateLimitPersistenceAdapter adapter = database.CreateAdapter(
            new MutableClock(Start),
            new PlatformRateLimitOptions
            {
                Events = 5,
                Delivery = 1,
                Media = 1,
                Api = 1,
            });

        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);
        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);
        Assert.True((await Check(adapter, "/delivery/asset")).IsAllowed);
        Assert.False((await Check(adapter, "/delivery/asset")).IsAllowed);
        Assert.True((await Check(adapter, "/media/asset")).IsAllowed);
        Assert.False((await Check(adapter, "/media/asset")).IsAllowed);
        Assert.True((await Check(adapter, "/api/v1/assets")).IsAllowed);
        Assert.False((await Check(adapter, "/api/v1/assets")).IsAllowed);
        Assert.True((await Check(adapter, "/api/v1/events")).IsAllowed);
    }

    /// <summary>
    /// The counter is the reason this adapter exists: replicas that each keep
    /// their own count would multiply the ceiling by the replica count.
    /// </summary>
    [Fact]
    public async Task Replicas_share_one_counter()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        var clock = new MutableClock(Start);
        var limits = new PlatformRateLimitOptions { Api = 4 };
        PlatformRateLimitPersistenceAdapter first =
            database.CreateAdapter(clock, limits);
        PlatformRateLimitPersistenceAdapter second =
            database.CreateAdapter(clock, limits);

        Assert.True((await Check(first, "/api/v1/assets")).IsAllowed);
        Assert.True((await Check(second, "/api/v1/assets")).IsAllowed);
        Assert.True((await Check(first, "/api/v1/assets")).IsAllowed);
        Assert.True((await Check(second, "/api/v1/assets")).IsAllowed);

        Assert.False((await Check(first, "/api/v1/assets")).IsAllowed);
        Assert.False((await Check(second, "/api/v1/assets")).IsAllowed);
    }

    /// <summary>
    /// A forwarded header is client-controlled. If it partitioned the bucket,
    /// any caller could mint a fresh allowance per request, so the header is
    /// not read at all and the peer keeps the count.
    /// </summary>
    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Real-IP")]
    [InlineData("Forwarded")]
    public async Task A_forwarded_header_cannot_mint_a_new_bucket(string header)
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        PlatformRateLimitPersistenceAdapter adapter = database.CreateAdapter(
            new MutableClock(Start),
            new PlatformRateLimitOptions { Api = 2 });

        Assert.True((await Check(
            adapter,
            "/api/v1/assets",
            header: (header, "198.51.100.1"))).IsAllowed);
        Assert.True((await Check(
            adapter,
            "/api/v1/assets",
            header: (header, "198.51.100.2"))).IsAllowed);
        Assert.False((await Check(
            adapter,
            "/api/v1/assets",
            header: (header, "198.51.100.3"))).IsAllowed);

        Assert.Single(await database.KeyHashesAsync());
    }

    /// <summary>
    /// The peer really is the partition, which is exactly why a deployment
    /// behind a shared ingress has to be configured as one shared ceiling.
    /// </summary>
    [Fact]
    public async Task A_different_peer_is_a_different_bucket()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        PlatformRateLimitPersistenceAdapter adapter = database.CreateAdapter(
            new MutableClock(Start),
            new PlatformRateLimitOptions { Api = 1 });

        Assert.True((await Check(adapter, "/api/v1/assets")).IsAllowed);
        Assert.False((await Check(adapter, "/api/v1/assets")).IsAllowed);
        Assert.True((await Check(
            adapter,
            "/api/v1/assets",
            client: "198.51.100.7")).IsAllowed);
    }

    /// <summary>
    /// The hosted profile has to serve the traffic a hosted deployment sees,
    /// which the shipped defaults do not when every request shares one peer.
    /// </summary>
    [Fact]
    public async Task The_hosted_profile_admits_traffic_the_defaults_reject()
    {
        await using RateLimitDatabase database = await RateLimitDatabase.CreateAsync();
        var clock = new MutableClock(Start);
        PlatformRateLimitPersistenceAdapter hosted = database.CreateAdapter(
            clock,
            new PlatformRateLimitOptions
            {
                Window = TimeSpan.FromMinutes(1),
                Api = PlatformRateLimitHostedProfile.Api,
                Events = PlatformRateLimitHostedProfile.Events,
                Delivery = PlatformRateLimitHostedProfile.Delivery,
                Media = PlatformRateLimitHostedProfile.Media,
            });

        for (int request = 0; request < 301; request++)
        {
            Assert.True((await Check(hosted, "/api/v1/assets")).IsAllowed);
        }
    }

    private static Task<PlatformRateLimitDecision> Check(
        PlatformRateLimitPersistenceAdapter adapter,
        string path,
        string client = Client,
        (string Name, string Value)? header = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(client);
        if (header is { } forwarded)
        {
            context.Request.Headers[forwarded.Name] = forwarded.Value;
        }

        return adapter.CheckAsync(context, CancellationToken.None).AsTask();
    }

    private static string LegacyKeyHash(string bucket, string client) =>
        Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes($"vistara:rate:{bucket}:{client}")));

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
    }

    /// <summary>
    /// One database shared by every context the test creates, which is how a
    /// replica is modelled here: separate contexts, one persisted counter.
    /// </summary>
    private sealed class RateLimitDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;
        private readonly List<RateLimitCatalogDbContext> _contexts = [];

        private RateLimitDatabase(SqliteConnection anchor, string connectionString)
        {
            _anchor = anchor;
            _connectionString = connectionString;
        }

        internal static async ValueTask<RateLimitDatabase> CreateAsync()
        {
            string name = $"RateLimits-{Guid.NewGuid():N}";
            string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync(CancellationToken.None);
            await using VistaraDbContext schema = new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(connectionString)
                    .Options,
                new FixedTenantScope(Guid.CreateVersion7()));
            await schema.Database.EnsureCreatedAsync(CancellationToken.None);
            return new RateLimitDatabase(anchor, connectionString);
        }

        internal PlatformRateLimitPersistenceAdapter CreateAdapter(
            IClock clock,
            PlatformRateLimitOptions limits)
        {
            RateLimitCatalogDbContext context = CreateContext();
            _contexts.Add(context);
            return new PlatformRateLimitPersistenceAdapter(
                new RelationalRateLimitStore(context),
                clock,
                Options.Create(limits));
        }

        internal async Task<string[]> KeyHashesAsync()
        {
            await using RateLimitCatalogDbContext context = CreateContext();
            return await context.Windows
                .AsNoTracking()
                .Select(row => row.KeyHash)
                .ToArrayAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (RateLimitCatalogDbContext context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _anchor.DisposeAsync();
        }

        private RateLimitCatalogDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<RateLimitCatalogDbContext>()
                .UseSqlite(_connectionString)
                .Options);
    }
}
