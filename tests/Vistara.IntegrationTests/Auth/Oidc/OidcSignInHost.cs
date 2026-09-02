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
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.Features.Oidc;
using Vistara.Auth.Cookies;
using Vistara.Application.Common;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Oidc;

/// <summary>
/// Boots the production API composition over a shared in-memory SQLite
/// database, points hosted sign-in at a live HTTPS loopback identity provider,
/// and drives it through the real middleware pipeline.
///
/// The only substitution is the TLS trust decision for the loopback
/// certificate: the OIDC transport is still the production
/// <see cref="OidcHttpDefaults"/> handler with redirects disabled, and the
/// options binding, provider registry, credential resolution, login request
/// store, token exchange, token validation, allowlists, and cookie session
/// handoff are all the shipped code.
/// </summary>
internal sealed class OidcSignInHost : IAsyncDisposable
{
    internal const string ApplicationHost = "vistara.example.test";
    internal const string ClientSecret = "loopback-client-secret";
    internal const string TenantSlug = "acme";
    internal const string TenantName = "Acme";
    internal const string LocalPassword = "correct-horse-battery";
    internal const string BootstrapSlug = "vistara";
    internal const string BootstrapName = "Vistara";

    /// <summary>
    /// The harness clock starts at the real current instant rather than a
    /// fixed date. The transient handle is a Data Protection payload whose
    /// expiry is enforced against the framework's own system clock, so an
    /// application clock parked in the past would make every handle look
    /// expired before the code under test ever saw it. Advancing this clock
    /// still expires the login request, which is the behaviour the expiry test
    /// is about.
    /// </summary>
    internal static readonly DateTimeOffset StartOfTest =
        new(DateTimeOffset.UtcNow.UtcDateTime.Ticks -
            (DateTimeOffset.UtcNow.UtcDateTime.Ticks % TimeSpan.TicksPerSecond),
            TimeSpan.Zero);

    internal static readonly Guid DirectoryTenantId =
        Guid.Parse("2c1a5b6e-4f10-4d0b-9c2a-9a1f3b7e5d01");

    internal static readonly Guid ForeignDirectoryTenantId =
        Guid.Parse("7f9d0a11-2b33-4c55-8677-99aabbccdd01");

    internal const string ClientId = "b7d3f210-8e44-4b6a-9c31-0d5a7e2f4c88";

    private readonly SqliteConnection _anchor;
    private readonly WebApplication _app;
    private readonly RequestDelegate _pipeline;
    private readonly AdvanceableClock _clock;

    private readonly RecordedLogProvider _audit;

    private OidcSignInHost(
        SqliteConnection anchor,
        WebApplication app,
        RequestDelegate pipeline,
        AdvanceableClock clock,
        LoopbackIdentityProvider identityProvider,
        string connectionString,
        RecordedLogProvider audit)
    {
        _anchor = anchor;
        _app = app;
        _pipeline = pipeline;
        _clock = clock;
        IdentityProvider = identityProvider;
        ConnectionString = connectionString;
        _audit = audit;
    }

    /// <summary>
    /// The server-side sign-in outcomes, which are the only place a reason is
    /// recorded. The browser always sees one uniform code.
    /// </summary>
    internal IReadOnlyList<string> AuditRecords => _audit.Messages;

    internal LoopbackIdentityProvider IdentityProvider { get; }

    internal AdvanceableClock Clock => _clock;

    internal string ConnectionString { get; }

    internal IServiceProvider Services => _app.Services;

    /// <summary>The composed cookie policy, so tests read real lifetimes.</summary>
    internal CookieAuthOptions CookieOptions =>
        _app.Services.GetRequiredService<CookieAuthOptions>();

    internal static async Task<OidcSignInHost> CreateAsync(
        bool bootstrapEnabled = false,
        IReadOnlyList<Guid>? allowedObjectIds = null,
        bool useManagedIdentity = false,
        Guid? bootstrapDirectoryTenantId = null)
    {
        var clock = new AdvanceableClock(StartOfTest);
        LoopbackIdentityProvider identityProvider =
            await LoopbackIdentityProvider.StartAsync(
                DirectoryTenantId,
                ClientId,
                () => clock.UtcNow);

        string name = $"OidcSignIn-{Guid.NewGuid():N}";
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
            await schema.Database.EnsureCreatedAsync(CancellationToken.None);
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            Settings(
                connectionString,
                identityProvider,
                bootstrapEnabled,
                allowedObjectIds ?? [],
                useManagedIdentity,
                bootstrapDirectoryTenantId ?? DirectoryTenantId));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var audit = new RecordedLogProvider();
        builder.Logging.AddProvider(audit);
        builder.Logging.AddFilter(
            "Vistara.Api.Composition.Platform.PlatformOidcAuditSink",
            LogLevel.Information);
        builder.Services.AddSingleton<IClock>(clock);
        if (useManagedIdentity)
        {
            // The federated assertion is minted by Azure instance metadata,
            // which does not exist in a test. Only that one call is stubbed;
            // the credential resolver, the assertion parameter names, and the
            // token request are the production ones.
            builder.Services.AddSingleton<IOidcManagedIdentityTokenSource>(
                new StubManagedIdentityTokenSource());
        }

        builder.Services.AddVistaraApiRuntime(builder.Configuration);
        builder.Services.AddVistaraApiPlatform(builder.Configuration);
        builder.Services.AddVistaraApiPersistence(builder.Configuration);
        builder.Services.AddVistaraPlatformSurface();

        // The loopback provider presents a self-signed certificate. Trusting
        // exactly that certificate is the only production behaviour replaced
        // here; redirects stay disabled and every other handler setting is the
        // shipped one.
        builder.Services.AddHttpClient(OidcHttpDefaults.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                SocketsHttpHandler handler = OidcHttpDefaults.CreateHandler();
                handler.SslOptions.RemoteCertificateValidationCallback =
                    (_, certificate, _, _) =>
                        certificate is not null &&
                        certificate.GetCertHashString() ==
                            identityProvider.Certificate.GetCertHashString();
                return handler;
            });

        WebApplication app = builder.Build();

        // The hosted sign-in graph is validated the way the host does at
        // startup. The full platform validation additionally requires the
        // media graph, which this harness deliberately does not compose.
        app.Services.ValidateVistaraApiOidcComposition();
        app.UseVistaraPlatform();
        app.MapVistaraPlatformSurface();
        RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
        return new OidcSignInHost(
            anchor,
            app,
            pipeline,
            clock,
            identityProvider,
            connectionString,
            audit);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _anchor.DisposeAsync();
        await IdentityProvider.DisposeAsync();
    }

    /// <summary>
    /// Starts a sign-in the way a browser navigation does and returns the
    /// authorization request plus the transient handle cookie.
    /// </summary>
    internal async Task<StartedSignIn> StartAsync(
        string providerId = "entra",
        string? returnTo = null,
        string? sessionCookie = null)
    {
        TestResponse response = await SendAsync(
            HttpMethods.Get,
            $"/api/v1/auth/oidc/{providerId}/start",
            returnTo is null ? null : $"?returnTo={Uri.EscapeDataString(returnTo)}",
            sessionCookie);
        return new StartedSignIn(response, response.CookieValue("__Host-vistara-oidc"));
    }

    internal Task<TestResponse> CallbackAsync(
        string? code,
        string? state,
        string? handleCookie,
        string? sessionCookie = null,
        string? error = null)
    {
        var query = new StringBuilder("?");
        if (code is not null)
        {
            query.Append("code=").Append(Uri.EscapeDataString(code)).Append('&');
        }

        if (state is not null)
        {
            query.Append("state=").Append(Uri.EscapeDataString(state)).Append('&');
        }

        if (error is not null)
        {
            query.Append("error=").Append(Uri.EscapeDataString(error)).Append('&');
        }

        return SendAsync(
            HttpMethods.Get,
            "/api/v1/auth/oidc/entra/callback",
            query.ToString().TrimEnd('&', '?'),
            sessionCookie,
            handleCookie);
    }

    /// <summary>
    /// Runs one whole sign-in: start, consent at the provider, callback.
    /// </summary>
    internal async Task<TestResponse> SignInAsync(
        Guid objectId,
        string? returnTo = null,
        string? sessionCookie = null,
        Guid? directoryTenantId = null,
        string email = "owner@example.test")
    {
        StartedSignIn started = await StartAsync(
            returnTo: returnTo,
            sessionCookie: sessionCookie);
        string code = IdentityProvider.Authorize(
            started.AuthorizationUri,
            objectId,
            directoryTenantId,
            email);
        return await CallbackAsync(
            code,
            started.State,
            started.HandleCookie,
            sessionCookie);
    }

    internal async Task<Guid> ProvisionLocalOwnerAsync(
        string slug = TenantSlug,
        string email = "local-owner@example.test")
    {
        TestResponse response = await SendAsync(
            HttpMethods.Post,
            "/api/v1/setup",
            body: JsonSerializer.Serialize(new
            {
                tenantSlug = slug,
                tenantName = slug,
                displayName = "Local Owner",
                email,
                password = LocalPassword,
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response.Json().GetProperty("tenantId").GetGuid();
    }

    internal async Task<string> LoginLocallyAsync(string email)
    {
        TestResponse response = await SendAsync(
            HttpMethods.Post,
            "/api/v1/auth/login",
            body: JsonSerializer.Serialize(new { login = email, password = LocalPassword }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.CookieValue(CookieAuthOptions.ProductionCookieName)!;
    }

    /// <summary>
    /// Links a directory identity to an existing user the way a prior hosted
    /// sign-in would have, using the canonical issuer the catalog stores.
    /// </summary>
    internal async Task LinkDirectoryIdentityAsync(
        Guid tenantId,
        Guid userId,
        Guid objectId,
        Guid? directoryTenantId = null)
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
        await using VistaraDbContext context = factory.Create(tenantId);
        context.ExternalIdentities.Add(new ExternalIdentityRow
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Issuer = ExternalFirstOwnerCredential.CanonicalIssuer(
                "entra",
                directoryTenantId ?? DirectoryTenantId),
            Subject = ExternalFirstOwnerCredential.SubjectFor(objectId),
            LinkedAtUtc = _clock.UtcNow,
        });
        await context.SaveChangesAsync(CancellationToken.None);
    }

    internal async Task<Guid> ReadOwnerUserIdAsync(Guid tenantId)
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
        await using VistaraDbContext context = factory.Create(tenantId);
        return await context.TenantMemberships
            .AsNoTracking()
            .Where(row => row.TenantId.Equals(tenantId))
            .Select(row => row.UserId)
            .FirstAsync(CancellationToken.None);
    }

    internal async Task DisableUserAsync(Guid tenantId, Guid userId)
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
        await using VistaraDbContext context = factory.Create(tenantId);
        UserRow user = await context.Users.SingleAsync(
            row => row.Id == userId,
            CancellationToken.None);
        user.Status = "Disabled";
        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Adds a second active tenant and membership for one user.</summary>
    internal async Task<Guid> AddMembershipAsync(Guid userId, string slug)
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
        Guid tenantId = Guid.CreateVersion7();
        DateTimeOffset now = _clock.UtcNow;
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
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = tenantId,
            UserId = userId,
            Role = "Member",
            Status = "Active",
            InvitedAtUtc = now,
            JoinedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        await context.SaveChangesAsync(CancellationToken.None);
        return tenantId;
    }

    /// <summary>Counts browser sessions that have not been revoked.</summary>
    internal async Task<int> CountLiveSessionsAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM cookie_sessions WHERE revoked_at_utc IS NULL";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    internal async Task<int> CountLoginRequestsAsync()
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
        await using VistaraDbContext context = factory.Create(Guid.CreateVersion7());
        return await context.OidcLoginRequests.AsNoTracking().CountAsync(
            CancellationToken.None);
    }

    /// <summary>
    /// Counts tenants outside any tenant scope. A scoped context would filter
    /// the very rows a bootstrap test needs to see.
    /// </summary>
    internal async Task<int> CountTenantsAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tenants";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    internal Task<TestResponse> DescribeSetupAsync() =>
        SendAsync(HttpMethods.Get, "/api/v1/setup");

    /// <summary>
    /// The provider iframe request. It is deliberately sent without a session
    /// cookie because a SameSite=Lax cookie is never attached to a cross-site
    /// iframe GET, which is the whole reason the endpoint cannot revoke.
    /// </summary>
    internal Task<TestResponse> FrontChannelLogoutAsync(string? query = null) =>
        SendAsync(
            HttpMethods.Get,
            "/api/v1/auth/oidc/entra/frontchannel-logout",
            query);

    /// <summary>
    /// Relying-party initiated sign-out, sent the way the application sends
    /// it: same-site POST, session cookie, antiforgery token.
    /// </summary>
    internal async Task<TestResponse> SignOutAsync(
        string sessionCookie,
        string? csrfToken = null,
        string providerId = "entra")
    {
        csrfToken ??= (await GetCurrentUserAsync(sessionCookie))
            .Json()
            .GetProperty("csrfToken")
            .GetString();
        return await SendAsync(
            HttpMethods.Post,
            OidcRoutes.SignOutPath(providerId),
            sessionCookie: sessionCookie,
            headers: csrfToken is null
                ? null
                : new Dictionary<string, string>
                {
                    [CookieAuthOptions.DefaultAntiforgeryHeaderName] = csrfToken,
                });
    }

    internal Task<TestResponse> GetCurrentUserAsync(string sessionCookie) =>
        SendAsync(HttpMethods.Get, "/api/v1/me", sessionCookie: sessionCookie);

    internal async Task<TestResponse> SendAsync(
        string method,
        string path,
        string? query = null,
        string? sessionCookie = null,
        string? handleCookie = null,
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(ApplicationHost);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query ?? string.Empty);
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.10");
        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                context.Request.Headers[name] = value;
            }
        }

        var cookies = new List<string>();
        if (sessionCookie is not null)
        {
            cookies.Add($"{CookieAuthOptions.ProductionCookieName}={sessionCookie}");
        }

        if (handleCookie is not null)
        {
            cookies.Add($"__Host-vistara-oidc={handleCookie}");
        }

        if (cookies.Count > 0)
        {
            context.Request.Headers.Cookie = string.Join("; ", cookies);
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
            context.Response.Headers.Location.ToString(),
            context.Response.Headers.CacheControl.ToString(),
            text,
            [.. context.Response.Headers[HeaderNames.SetCookie]
                .Where(value => value is not null)
                .Select(value => value!)]);
    }

    private static Dictionary<string, string?> Settings(
        string connectionString,
        LoopbackIdentityProvider identityProvider,
        bool bootstrapEnabled,
        IReadOnlyList<Guid> allowedObjectIds,
        bool useManagedIdentity,
        Guid bootstrapDirectoryTenantId)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = connectionString,
            ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
            ["Platform:Authentication:ApiKeys:Peppers:v1"] =
                Convert.ToBase64String(new byte[32]),
            ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] = "machine",
            ["Platform:Authentication:Jwt:Issuers:0:Issuer"] =
                "https://issuer.example.test",
            ["Platform:Authentication:Jwt:Issuers:0:Audience"] = "vistara-api",
            ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
                "https://issuer.example.test/.well-known/openid-configuration",
            ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] =
                SecurityAlgorithms.RsaSha256,
            ["Platform:Authentication:Oidc:Enabled"] = "true",
            ["Platform:Authentication:Oidc:Providers:0:ProviderId"] = "entra",
            ["Platform:Authentication:Oidc:Providers:0:DisplayName"] =
                "Microsoft Entra ID",
            ["Platform:Authentication:Oidc:Providers:0:TenantId"] =
                identityProvider.DirectoryTenantId.ToString("D"),
            ["Platform:Authentication:Oidc:Providers:0:ClientId"] =
                identityProvider.ClientId,
            ["Platform:Authentication:Oidc:Providers:0:Authority"] =
                identityProvider.Authority.AbsoluteUri.TrimEnd('/'),
            ["Platform:Authentication:Oidc:Providers:0:RedirectUri"] =
                $"https://{ApplicationHost}{OidcRoutes.CallbackPath}",
            ["Platform:Authentication:Oidc:Providers:0:PostLogoutRedirectUri"] =
                $"https://{ApplicationHost}{OidcRoutes.SignedOutPath}",
            ["Platform:Authentication:Oidc:Providers:0:LoginRequestLifetimeSeconds"] =
                "600",
        };

        if (useManagedIdentity)
        {
            settings["Platform:Authentication:Oidc:Providers:0:ManagedIdentityClientId"] =
                "3f7e2a91-55cc-4d18-9b7a-6e2f11c40a02";
        }
        else
        {
            settings["Platform:Authentication:Oidc:Providers:0:ClientSecret"] = ClientSecret;
        }

        if (!bootstrapEnabled)
        {
            return settings;
        }

        settings["Platform:Bootstrap:FirstOwner:Enabled"] = "true";
        settings["Platform:Bootstrap:FirstOwner:ProviderId"] = "entra";
        settings["Platform:Bootstrap:FirstOwner:DirectoryTenantId"] =
            bootstrapDirectoryTenantId.ToString("D");
        settings["Platform:Bootstrap:FirstOwner:TenantSlug"] = BootstrapSlug;
        settings["Platform:Bootstrap:FirstOwner:TenantName"] = BootstrapName;
        for (int index = 0; index < allowedObjectIds.Count; index++)
        {
            settings[$"Platform:Bootstrap:FirstOwner:AllowedObjectIds:{index}"] =
                allowedObjectIds[index].ToString("D");
        }

        return settings;
    }

    private sealed class StubManagedIdentityTokenSource : IOidcManagedIdentityTokenSource
    {
        public ValueTask<string?> GetClientAssertionAsync(
            string managedIdentityClientId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("federated.managed.identity.assertion");
    }
}

/// <summary>The observable result of starting a hosted sign-in.</summary>
internal sealed record StartedSignIn(TestResponse Response, string? HandleCookie)
{
    internal Uri AuthorizationUri => new(Response.Location);

    internal string State =>
        LoopbackIdentityProvider.ReadAuthorizationRequest(AuthorizationUri)["state"];
}

internal sealed record TestResponse(
    HttpStatusCode StatusCode,
    string Location,
    string CacheControl,
    string Body,
    IReadOnlyList<string> SetCookies)
{
    internal JsonElement Json() => JsonDocument.Parse(Body).RootElement.Clone();

    /// <summary>
    /// The value a response set for one cookie, or null when the response
    /// cleared it or never set it.
    /// </summary>
    internal string? CookieValue(string name)
    {
        string prefix = name + "=";
        string? header = SetCookies.LastOrDefault(
            value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (header is null)
        {
            return null;
        }

        string value = header[prefix.Length..].Split(';')[0];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal bool ClearsCookie(string name) =>
        SetCookies.Any(value =>
            value.StartsWith(name + "=;", StringComparison.Ordinal) ||
            (value.StartsWith(name + "=", StringComparison.Ordinal) &&
                value.Contains("Max-Age=0", StringComparison.Ordinal)));
}

/// <summary>Captures what the application logs, with the category it came from.</summary>
internal sealed class RecordedLogProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Category, string Message)>
        _entries = new();

    internal IReadOnlyList<string> Messages =>
        [.. _entries.Select(entry => entry.Message)];

    /// <summary>
    /// Only the records Vistara itself wrote. The hosting and routing
    /// diagnostics the framework emits for every request are not
    /// operator-facing Vistara records and are not this repository's to
    /// change.
    /// </summary>
    internal IReadOnlyList<string> VistaraMessages =>
    [
        .. _entries
            .Where(entry => entry.Category.StartsWith("Vistara.", StringComparison.Ordinal))
            .Select(entry => entry.Message),
    ];

    public ILogger CreateLogger(string categoryName) =>
        new QueueLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class QueueLogger(
        string category,
        System.Collections.Concurrent.ConcurrentQueue<(string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, formatter(state, exception)));
    }
}

internal sealed class AdvanceableClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    internal void Advance(TimeSpan amount) => _now = _now.Add(amount);
}
