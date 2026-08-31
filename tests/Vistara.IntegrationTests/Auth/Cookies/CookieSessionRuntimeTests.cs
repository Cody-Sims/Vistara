using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Application.Identity;
using Vistara.Auth.Cookies;
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

    [Fact]
    public async Task Cookie_authentication_rejects_an_unknown_session_token()
    {
        await using CookieRuntimeHost host = await CookieRuntimeHost.CreateAsync();
        await host.ProvisionAsync("repro-workspace", "repro.owner@vistara.invalid");
        string cookie = await host.LoginAsync("repro.owner@vistara.invalid");

        TestResponse forged = await host.SendAsync(
            "GET",
            "/api/v1/me",
            new string('a', 43));
        TestResponse live = await host.SendAsync("GET", "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal("cookie_auth.invalid_session", forged.ProblemCode());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
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

    private static string SetupBody(string slug, string email) =>
        JsonSerializer.Serialize(new
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
        private readonly SqliteConnection _anchor;
        private readonly WebApplication _app;
        private readonly RequestDelegate _pipeline;
        private readonly string _connectionString;

        private CookieRuntimeHost(
            SqliteConnection anchor,
            WebApplication app,
            RequestDelegate pipeline,
            string connectionString)
        {
            _anchor = anchor;
            _app = app;
            _pipeline = pipeline;
            _connectionString = connectionString;
        }

        internal static async Task<CookieRuntimeHost> CreateAsync()
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

            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "Sqlite",
                    ["Persistence:ConnectionString"] = connectionString,
                });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddVistaraApiRuntime(builder.Configuration);
            builder.Services.AddVistaraApiPlatform(builder.Configuration);
            builder.Services.AddVistaraApiPersistence(builder.Configuration);
            builder.Services.AddVistaraPlatformSurface();

            WebApplication app = builder.Build();
            app.UseVistaraPlatform();
            app.MapVistaraPlatformSurface();
            RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
            return new CookieRuntimeHost(anchor, app, pipeline, connectionString);
        }

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

        internal async Task SuspendTenantAsync(Guid tenantId)
        {
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

        internal Task<TestResponse> PostJsonAsync(
            string path,
            string body,
            string? sessionCookie = null) =>
            SendAsync("POST", path, sessionCookie, body);

        internal async Task<TestResponse> SendAsync(
            string method,
            string path,
            string? sessionCookie = null,
            string? body = null)
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
