using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Application.Common;
using Vistara.Application.Identity;
using Vistara.Auth.Cookies;
using Vistara.Auth.Jwt;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Cookies;

/// <summary>
/// Exercises the browser session cookie against the real API composition over
/// SQLite: the same registrations, middleware order, and authentication
/// handlers the shipped host uses. The flow mirrors a first run in a browser,
/// where the cookie issued by sign-in must authenticate the very next request.
/// </summary>
public sealed class CookieSessionRuntimeTests
{
    private const string Password = "correct-horse-battery-staple";

    /// <summary>
    /// The canonical base64url encoding of thirty-two zero bytes. It satisfies
    /// the session token format exactly, so nothing but a routing miss can
    /// reject it, and no issued session can ever carry it.
    /// </summary>
    private const string UnroutedSessionToken =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>
    /// Forty-three base64url characters that are not the canonical encoding of
    /// any thirty-two byte value: the final character carries bits that a
    /// canonical encoding leaves clear.
    /// </summary>
    private const string NoncanonicalSessionToken =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string BearerIssuer = "https://issuer.vistara.invalid";

    private const string BearerAudience = "vistara-api";

    private const string BearerSubject = "external-subject-1";

    private static readonly DateTimeOffset StartOfTest =
        new(2036, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [Fact]
    public async Task Cookie_issued_by_login_authenticates_the_next_request()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();

        TestResponse available = await host.SendAsync("GET", "/api/v1/setup");
        TestResponse provisioned = await host.PostJsonAsync(
            "/api/v1/setup",
            SetupBody("repro-workspace", "repro.owner@vistara.invalid"));
        TestResponse login = await host.PostJsonAsync(
            "/api/v1/auth/login",
            LoginBody("repro.owner@vistara.invalid"));
        string cookie = login.SessionCookieValue();
        TestResponse me = await host.SendAsync("GET", "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        Assert.True(available.Json().GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, provisioned.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(cookie));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(
            provisioned.Json().GetProperty("userId").GetGuid(),
            me.Json().GetProperty("userId").GetGuid());
        Assert.Equal(
            provisioned.Json().GetProperty("tenantId").GetGuid(),
            me.Json().GetProperty("tenantId").GetGuid());
        Assert.Equal("TenantOwner", me.Json().GetProperty("role").GetString());
    }

    /// <summary>
    /// A live session must survive being read. The defect this covers revoked
    /// the row during authentication, so a second read failed even after the
    /// first was repaired.
    /// </summary>
    [Fact]
    public async Task Cookie_authentication_keeps_the_session_live_across_requests()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string cookie = await host.LoginAsync("repro.owner@vistara.invalid");

        TestResponse first = await host.SendAsync("GET", "/api/v1/me", cookie);
        string current = first.RefreshedSessionCookieValue() ?? cookie;
        TestResponse second = await host.SendAsync("GET", "/api/v1/me", current);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await host.CountLiveSessionsAsync());
        Assert.Equal(0, await host.CountRevokedSessionsAsync());
    }

    /// <summary>
    /// A well-formed token that owns no routing row must be rejected by the
    /// routing lookup itself, not by the format gate. The executed SQL is
    /// captured to prove the tenant routing table really was consulted and
    /// that the miss stopped the request before any session row was read.
    /// </summary>
    [Fact]
    public async Task Cookie_authentication_rejects_a_well_formed_token_that_owns_no_route()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string cookie = await host.LoginAsync("repro.owner@vistara.invalid");
        Assert.Equal(1, await host.CountCookieSessionRoutesAsync());

        host.ClearExecutedSql();
        TestResponse unrouted = await host.SendAsync(
            "GET",
            "/api/v1/me",
            UnroutedSessionToken);
        IReadOnlyList<string> executed = host.ExecutedSql();
        TestResponse live = await host.SendAsync("GET", "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, unrouted.StatusCode);
        Assert.Equal("cookie_auth.invalid_session", unrouted.ProblemCode());
        Assert.Contains(
            executed,
            statement =>
                statement.Contains("authentication_routes", StringComparison.Ordinal) &&
                statement.Contains("lookup_digest", StringComparison.Ordinal));
        Assert.DoesNotContain(
            executed,
            statement => statement.Contains("cookie_sessions", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(1, await host.CountLiveSessionsAsync());
        Assert.Equal(0, await host.CountRevokedSessionsAsync());
        Assert.Equal(1, await host.CountCookieSessionRoutesAsync());
    }

    /// <summary>
    /// A token that is not a canonical session token must be refused by the
    /// format gate, so a caller cannot probe the tenant routing table with
    /// arbitrary cookie values.
    /// </summary>
    [Theory]
    [InlineData(NoncanonicalSessionToken)]
    [InlineData("short-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.")]
    public async Task Cookie_authentication_rejects_a_malformed_token_without_routing(
        string malformedToken)
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string cookie = await host.LoginAsync("repro.owner@vistara.invalid");

        host.ClearExecutedSql();
        TestResponse malformed = await host.SendAsync(
            "GET",
            "/api/v1/me",
            malformedToken);
        IReadOnlyList<string> executed = host.ExecutedSql();
        TestResponse live = await host.SendAsync("GET", "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        Assert.Equal("cookie_auth.invalid_session", malformed.ProblemCode());
        Assert.NotEmpty(executed);
        Assert.DoesNotContain(
            executed,
            statement =>
                statement.Contains("authentication_routes", StringComparison.Ordinal));
        Assert.DoesNotContain(
            executed,
            statement => statement.Contains("cookie_sessions", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(1, await host.CountLiveSessionsAsync());
        Assert.Equal(0, await host.CountRevokedSessionsAsync());
    }

    [Fact]
    public async Task Cookie_authentication_rejects_a_revoked_session()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string cookie = await host.LoginAsync("repro.owner@vistara.invalid");

        TestResponse loggedOut = await host.PostJsonAsync(
            "/api/v1/auth/logout",
            "{}",
            cookie);
        TestResponse afterLogout = await host.SendAsync("GET", "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        Assert.Equal("cookie_auth.invalid_session", afterLogout.ProblemCode());
        Assert.Equal(0, await host.CountLiveSessionsAsync());
    }

    /// <summary>
    /// Two tenants own separate sessions. Each cookie must authenticate into
    /// its own tenant only, and neither may be revoked or re-scoped by the
    /// other's traffic.
    /// </summary>
    [Fact]
    public async Task Cookie_authentication_binds_each_session_to_its_own_tenant()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        Guid firstTenant = await host.ProvisionAsync(
            "first-workspace",
            "first.owner@vistara.invalid");
        Guid secondTenant = await host.CreateAdditionalTenantAsync(
            "second-workspace",
            "second.owner@vistara.invalid");

        string firstCookie = await host.LoginAsync("first.owner@vistara.invalid");
        string secondCookie = await host.LoginAsync("second.owner@vistara.invalid");
        TestResponse first = await host.SendAsync("GET", "/api/v1/me", firstCookie);
        TestResponse second = await host.SendAsync("GET", "/api/v1/me", secondCookie);

        Assert.NotEqual(firstTenant, secondTenant);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstTenant, first.Json().GetProperty("tenantId").GetGuid());
        Assert.Equal(secondTenant, second.Json().GetProperty("tenantId").GetGuid());
        Assert.Equal(
            "first.owner@vistara.invalid",
            first.Json().GetProperty("email").GetString());
        Assert.Equal(
            "second.owner@vistara.invalid",
            second.Json().GetProperty("email").GetString());
        Assert.Equal(2, await host.CountLiveSessionsAsync());
    }

    [Fact]
    public async Task Cookie_authentication_rejects_a_session_whose_tenant_is_suspended()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        Guid tenantId = await host.ProvisionAsync(
            "repro-workspace",
            "repro.owner@vistara.invalid");
        string cookie = await host.LoginAsync("repro.owner@vistara.invalid");
        await host.SuspendTenantAsync(tenantId);

        TestResponse response = await host.SendAsync("GET", "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await host.CountLiveSessionsAsync());
    }

    /// <summary>
    /// A browser that returns after the sliding refresh interval gets a new
    /// session cookie. The response must still describe a cookie session, and
    /// the antiforgery token it carries must belong to the refreshed session,
    /// not to the cookie the request arrived with, which rotation revoked.
    /// </summary>
    [Fact]
    public async Task Cookie_session_rotated_by_the_sliding_interval_stays_usable()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string original = await host.LoginAsync("repro.owner@vistara.invalid");
        host.Clock.Advance(TimeSpan.FromMinutes(11));

        TestResponse me = await host.SendAsync("GET", "/api/v1/me", original);
        string refreshed = me.SessionCookieValue();
        string csrfToken = me.Json().GetProperty("csrfToken").GetString()!;
        TestResponse unsafeRequest = await host.SendAsync(
            "POST",
            "/api/v1/api-keys",
            refreshed,
            """{"scopes":["assets.read"]}""",
            new Dictionary<string, string> { ["X-Vistara-CSRF"] = csrfToken });
        TestResponse withOldCookie = await host.SendAsync(
            "GET",
            "/api/v1/me",
            original);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("cookie", me.Json().GetProperty("authenticationKind").GetString());
        Assert.Equal(
            "X-Vistara-CSRF",
            me.Json().GetProperty("csrfHeaderName").GetString());
        Assert.NotEqual(original, refreshed);
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));
        Assert.Equal(HttpStatusCode.Created, unsafeRequest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withOldCookie.StatusCode);
    }

    /// <summary>
    /// The antiforgery token a rotation produces must be the one the very next
    /// unsafe request needs, with no second read of <c>/api/v1/me</c> in
    /// between.
    /// </summary>
    [Fact]
    public async Task Rotated_cookie_session_accepts_the_immediate_unsafe_request()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string original = await host.LoginAsync("repro.owner@vistara.invalid");
        string staleCsrf = (await host.SendAsync("GET", "/api/v1/me", original))
            .Json()
            .GetProperty("csrfToken")
            .GetString()!;
        host.Clock.Advance(TimeSpan.FromMinutes(11));

        TestResponse me = await host.SendAsync("GET", "/api/v1/me", original);
        string refreshed = me.SessionCookieValue();
        string csrfToken = me.Json().GetProperty("csrfToken").GetString()!;
        TestResponse accepted = await host.SendAsync(
            "POST",
            "/api/v1/api-keys",
            refreshed,
            """{"scopes":["assets.read"]}""",
            new Dictionary<string, string> { ["X-Vistara-CSRF"] = csrfToken });
        TestResponse refused = await host.SendAsync(
            "POST",
            "/api/v1/api-keys",
            refreshed,
            """{"scopes":["assets.read"]}""",
            new Dictionary<string, string> { ["X-Vistara-CSRF"] = staleCsrf });

        Assert.NotEqual(staleCsrf, csrfToken);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("cookie_auth.antiforgery_required", refused.ProblemCode());
    }

    /// <summary>
    /// Two tabs read the same live session. Neither may rotate it, and both
    /// must receive the same usable antiforgery token.
    /// </summary>
    [Fact]
    public async Task Cookie_session_antiforgery_token_is_stable_across_tabs()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string cookie = await host.LoginAsync("repro.owner@vistara.invalid");

        TestResponse firstTab = await host.SendAsync("GET", "/api/v1/me", cookie);
        TestResponse secondTab = await host.SendAsync("GET", "/api/v1/me", cookie);
        string csrfToken = secondTab.Json().GetProperty("csrfToken").GetString()!;
        TestResponse unsafeRequest = await host.SendAsync(
            "POST",
            "/api/v1/api-keys",
            cookie,
            """{"scopes":["assets.read"]}""",
            new Dictionary<string, string> { ["X-Vistara-CSRF"] = csrfToken });

        Assert.Equal(HttpStatusCode.OK, firstTab.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondTab.StatusCode);
        Assert.Null(firstTab.RefreshedSessionCookieValue());
        Assert.Null(secondTab.RefreshedSessionCookieValue());
        Assert.Equal(
            firstTab.Json().GetProperty("csrfToken").GetString(),
            csrfToken);
        Assert.Equal(HttpStatusCode.Created, unsafeRequest.StatusCode);
        Assert.Equal(1, await host.CountLiveSessionsAsync());
    }

    /// <summary>
    /// Every credential publishes its own kind, and only a cookie session ever
    /// carries an antiforgery token.
    /// </summary>
    [Fact]
    public async Task Every_credential_kind_is_published_and_only_cookies_carry_csrf()
    {
        using RSA rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "issuer-1" };
        await using CookieRuntimeHost host =
            await CookieRuntimeHost.CreateAsync(signingKey);
        Guid tenantId = await host.ProvisionAsync(
            "repro-workspace",
            "repro.owner@vistara.invalid");
        TestResponse login = await host.PostJsonAsync(
            "/api/v1/auth/login",
            LoginBody("repro.owner@vistara.invalid"));
        string cookie = login.SessionCookieValue();
        string csrfToken = login.Json().GetProperty("csrfToken").GetString()!;
        Guid userId = login.Json()
            .GetProperty("user")
            .GetProperty("userId")
            .GetGuid();
        await host.LinkExternalIdentityAsync(
            tenantId,
            userId,
            BearerIssuer,
            BearerSubject);

        TestResponse created = await host.SendAsync(
            "POST",
            "/api/v1/api-keys",
            cookie,
            """{"scopes":["assets.read"]}""",
            new Dictionary<string, string> { ["X-Vistara-CSRF"] = csrfToken });
        string apiKey = created.Json().GetProperty("secret").GetString()!;
        TestResponse byCookie = await host.SendAsync("GET", "/api/v1/me", cookie);
        TestResponse byApiKey = await host.SendAsync(
            "GET",
            "/api/v1/me",
            headers: new Dictionary<string, string> { ["X-API-Key"] = apiKey });
        TestResponse byBearer = await host.SendAsync(
            "GET",
            "/api/v1/me",
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {CreateBearerToken(signingKey, tenantId, host)}",
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(
            "cookie",
            login.Json()
                .GetProperty("user")
                .GetProperty("authenticationKind")
                .GetString());
        Assert.Equal(HttpStatusCode.OK, byCookie.StatusCode);
        Assert.Equal(
            "cookie",
            byCookie.Json().GetProperty("authenticationKind").GetString());
        Assert.True(byCookie.Json().TryGetProperty("csrfToken", out _));
        Assert.Equal(HttpStatusCode.OK, byApiKey.StatusCode);
        Assert.Equal(
            "apiKey",
            byApiKey.Json().GetProperty("authenticationKind").GetString());
        Assert.False(byApiKey.Json().TryGetProperty("csrfToken", out _));
        Assert.Equal(HttpStatusCode.OK, byBearer.StatusCode);
        Assert.Equal(
            "bearer",
            byBearer.Json().GetProperty("authenticationKind").GetString());
        Assert.False(byBearer.Json().TryGetProperty("csrfToken", out _));
    }

    private static string CreateBearerToken(
        SecurityKey signingKey,
        Guid tenantId,
        CookieRuntimeHost host)
    {
        DateTimeOffset now = host.Clock.UtcNow;
        string payload = $$"""
            {"iss":"{{BearerIssuer}}","aud":"{{BearerAudience}}",
             "sub":"{{BearerSubject}}","jti":"{{Guid.CreateVersion7():D}}",
             "tenant_id":"{{tenantId:D}}",
             "nbf":{{now.AddMinutes(-1).ToUnixTimeSeconds()}},
             "exp":{{now.AddMinutes(5).ToUnixTimeSeconds()}}}
            """;
        return new JsonWebTokenHandler().CreateToken(
            payload,
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            new Dictionary<string, object> { ["typ"] = "at+jwt" });
    }

    private static string SetupBody(string slug, string email) =>        JsonSerializer.Serialize(new
        {
            tenantSlug = slug,
            tenantName = slug,
            displayName = "Repro Owner",
            email,
            password = Password,
        });

    private static string LoginBody(string email) =>
        JsonSerializer.Serialize(new { login = email, password = Password });

    /// <summary>
    /// Boots the production API composition over a shared in-memory SQLite
    /// database and drives it through the real middleware pipeline.
    /// </summary>
    private sealed class CookieRuntimeHost : IAsyncDisposable
    {
        private const string CommandCategory =
            "Microsoft.EntityFrameworkCore.Database.Command";

        private readonly SqliteConnection _anchor;
        private readonly WebApplication _app;
        private readonly RequestDelegate _pipeline;
        private readonly string _connectionString;
        private readonly ExecutedSqlRecorder _sql;
        private readonly AdvanceableClock _clock;

        private CookieRuntimeHost(
            SqliteConnection anchor,
            WebApplication app,
            RequestDelegate pipeline,
            string connectionString,
            ExecutedSqlRecorder sql,
            AdvanceableClock clock)
        {
            _anchor = anchor;
            _app = app;
            _pipeline = pipeline;
            _connectionString = connectionString;
            _sql = sql;
            _clock = clock;
        }

        internal static async Task<CookieRuntimeHost> CreateAsync(
            SecurityKey? bearerSigningKey = null)
        {
            string name = $"CookieRuntime-{Guid.NewGuid():N}";
            string connectionString =
                $"Data Source={name};Mode=Memory;Cache=Shared;Default Timeout=30";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync(CancellationToken.None);
            await using (var schema = new VistaraDbContext(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(connectionString)
                    .Options,
                new FixedTenantScope(Guid.CreateVersion7())))
            {
                await schema.Database.EnsureCreatedAsync(
                    CancellationToken.None);
            }

            var sql = new ExecutedSqlRecorder();
            var clock = new AdvanceableClock(StartOfTest);
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "Sqlite",
                    ["Persistence:ConnectionString"] = connectionString,
                    ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
                    ["Platform:Authentication:ApiKeys:Peppers:v1"] =
                        Convert.ToBase64String(new byte[32]),
                    ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] = "test",
                    ["Platform:Authentication:Jwt:Issuers:0:Issuer"] = BearerIssuer,
                    ["Platform:Authentication:Jwt:Issuers:0:Audience"] = BearerAudience,
                    ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
                        $"{BearerIssuer}/.well-known/openid-configuration",
                    ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] =
                        SecurityAlgorithms.RsaSha256,
                });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddProvider(sql);
            builder.Logging.AddFilter(CommandCategory, LogLevel.Information);
            builder.Services.AddSingleton<IClock>(clock);
            if (bearerSigningKey is not null)
            {
                // Only the federated issuer's published key set is substituted:
                // it lives outside the process. Token validation, membership
                // resolution, and claim mapping stay on the production path.
                builder.Services.AddSingleton<IJwtMetadataSigningKeyResolver>(
                    new StaticMetadataSigningKeyResolver(bearerSigningKey));
            }

            builder.Services.AddVistaraApiRuntime(builder.Configuration);
            builder.Services.AddVistaraApiPlatform(builder.Configuration);
            builder.Services.AddVistaraApiPersistence(builder.Configuration);
            builder.Services.AddVistaraPlatformSurface();

            WebApplication app = builder.Build();
            app.UseVistaraPlatform();
            app.MapVistaraPlatformSurface();
            RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
            return new CookieRuntimeHost(
                anchor,
                app,
                pipeline,
                connectionString,
                sql,
                clock);
        }

        /// <summary>The clock the whole host reads, so lifetimes are exact.</summary>
        internal AdvanceableClock Clock => _clock;

        /// <summary>Every statement the host has run since the last clear.</summary>
        internal IReadOnlyList<string> ExecutedSql() => _sql.Statements;

        internal void ClearExecutedSql() => _sql.Clear();

        internal async Task<Guid> ProvisionAsync(string slug, string email)
        {
            TestResponse response = await PostJsonAsync(
                "/api/v1/setup",
                SetupBody(slug, email));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return response.Json().GetProperty("tenantId").GetGuid();
        }

        /// <summary>
        /// Adds a second tenant with its own owner. First-owner provisioning is
        /// a one-time route, so the extra tenant is written directly through the
        /// production persistence model.
        /// </summary>
        internal async Task<Guid> CreateAdditionalTenantAsync(
            string slug,
            string email)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var factory = scope.ServiceProvider
                .GetRequiredService<TenantDbContextFactory>();
            Guid tenantId = Guid.CreateVersion7();
            Guid userId = Guid.CreateVersion7();
            Guid identityId = Guid.CreateVersion7();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string hash = scope.ServiceProvider
                .GetRequiredService<ILocalPasswordHasher>()
                .Hash(Password);
            await using VistaraDbContext context = factory.Create(tenantId);
            context.Tenants.Add(new TenantRow
            {
                Id = tenantId,
                TenantId = tenantId,
                Slug = slug,
                Name = slug,
                Status = "Active",
                SettingsJson = "{}",
                QuotasJson = "{}",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            context.Users.Add(new UserRow
            {
                Id = userId,
                NormalizedEmail = email,
                DisplayName = "Second Owner",
                Status = "Active",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            context.LocalIdentities.Add(new LocalIdentityRow
            {
                Id = identityId,
                UserId = userId,
                NormalizedLogin = email,
                LinkedAtUtc = now,
            });
            context.LocalCredentials.Add(new LocalCredentialRow
            {
                LocalIdentityId = identityId,
                UserId = userId,
                PasswordHash = hash,
                UpdatedAtUtc = now,
                Version = 1,
            });
            context.TenantMemberships.Add(new TenantMembershipRow
            {
                TenantId = tenantId,
                UserId = userId,
                Role = "TenantOwner",
                Status = "Active",
                InvitedAtUtc = now,
                JoinedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            await context.SaveChangesAsync(CancellationToken.None);
            return tenantId;
        }

        internal async Task<string> LoginAsync(string email)
        {
            TestResponse response = await PostJsonAsync(
                "/api/v1/auth/login",
                LoginBody(email));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response.SessionCookieValue();
        }

        /// <summary>
        /// Links a federated subject to an existing user, the way an external
        /// identity provider sign-in does, so a bearer token can authenticate.
        /// </summary>
        internal async Task LinkExternalIdentityAsync(
            Guid tenantId,
            Guid userId,
            string issuer,
            string subject)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var factory = scope.ServiceProvider
                .GetRequiredService<TenantDbContextFactory>();
            await using VistaraDbContext context = factory.Create(tenantId);
            context.ExternalIdentities.Add(new ExternalIdentityRow
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                Issuer = issuer,
                Subject = subject,
                LinkedAtUtc = _clock.UtcNow,
            });
            await context.SaveChangesAsync(CancellationToken.None);
        }

        internal async Task SuspendTenantAsync(Guid tenantId)        {
            await ExecuteAsync(
                "UPDATE tenants SET status = 'Suspended' WHERE lower(id) = $tenant",
                ("$tenant", tenantId.ToString("D").ToLowerInvariant()));
        }

        internal Task<int> CountLiveSessionsAsync() =>
            CountAsync(
                "SELECT COUNT(*) FROM cookie_sessions WHERE revoked_at_utc IS NULL");

        internal Task<int> CountRevokedSessionsAsync() =>
            CountAsync(
                "SELECT COUNT(*) FROM cookie_sessions WHERE revoked_at_utc IS NOT NULL");

        /// <summary>Counts the tenant routing rows that browser sessions own.</summary>
        internal Task<int> CountCookieSessionRoutesAsync() =>
            CountAsync(
                "SELECT COUNT(*) FROM authentication_routes " +
                "WHERE kind = 'CookieSession'");

        internal Task<TestResponse> PostJsonAsync(
            string path,
            string body,
            string? sessionCookie = null) =>
            SendAsync("POST", path, sessionCookie, body);

        internal async Task<TestResponse> SendAsync(
            string method,
            string path,
            string? sessionCookie = null,
            string? body = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
            };
            context.Request.Method = method;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("vistara.example.test");
            context.Request.Path = path;
            if (headers is not null)
            {
                foreach ((string name, string value) in headers)
                {
                    context.Request.Headers[name] = value;
                }
            }
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.10");
            if (sessionCookie is not null)
            {
                context.Request.Headers.Cookie =
                    $"{CookieAuthOptions.ProductionCookieName}={sessionCookie}";
            }

            if (body is not null)
            {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = payload.Length;
                context.Request.Body = new MemoryStream(payload);
            }

            var responseBody = new MemoryStream();
            context.Response.Body = responseBody;
            await _pipeline(context);
            responseBody.Position = 0;
            string text = await new StreamReader(responseBody).ReadToEndAsync(
                CancellationToken.None);
            return new TestResponse(
                (HttpStatusCode)context.Response.StatusCode,
                text,
                context.Response.Headers[HeaderNames.SetCookie]
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .ToArray());
        }

        private async Task<int> CountAsync(string sql)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            object? value = await command.ExecuteScalarAsync(
                CancellationToken.None);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private async Task ExecuteAsync(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _anchor.DisposeAsync();
        }
    }

    /// <summary>
    /// A clock the test advances by hand so session lifetimes are exact rather
    /// than wall-clock dependent.
    /// </summary>
    private sealed class AdvanceableClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _utcNow = start;

        public DateTimeOffset UtcNow => _utcNow;

        internal void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }

    /// <summary>
    /// Publishes a federated issuer's signing key without reaching the network.
    /// </summary>
    private sealed class StaticMetadataSigningKeyResolver(SecurityKey key)
        : IJwtMetadataSigningKeyResolver
    {
        public ValueTask<IReadOnlyCollection<SecurityKey>> ResolveAsync(
            Uri metadataAddress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyCollection<SecurityKey>>([key]);
    }

    /// <summary>
    /// Records the SQL the host actually executes, so a test can prove which
    /// tables a request read rather than inferring it from the status code.
    /// </summary>
    private sealed class ExecutedSqlRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _statements = new();

        internal IReadOnlyList<string> Statements => [.. _statements];

        internal void Clear() => _statements.Clear();

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(_statements);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> statements)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                statements.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string Body,
        IReadOnlyList<string> SetCookieHeaders)
    {
        internal JsonElement Json() =>
            JsonDocument.Parse(Body).RootElement.Clone();

        internal string? ProblemCode() =>
            Json().TryGetProperty("code", out JsonElement code)
                ? code.GetString()
                : null;

        internal string SessionCookieValue() =>
            RefreshedSessionCookieValue()
            ?? throw new InvalidOperationException(
                "The response did not issue a browser session cookie.");

        internal string? RefreshedSessionCookieValue()
        {
            string prefix = $"{CookieAuthOptions.ProductionCookieName}=";
            string? header = SetCookieHeaders.LastOrDefault(value =>
                value.StartsWith(prefix, StringComparison.Ordinal));
            if (header is null)
            {
                return null;
            }

            string value = header[prefix.Length..].Split(';')[0];
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
