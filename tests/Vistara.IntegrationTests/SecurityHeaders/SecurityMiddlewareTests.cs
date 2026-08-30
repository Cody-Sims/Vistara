using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Security;
using Vistara.Auth.Cookies;
using Xunit;

namespace Vistara.IntegrationTests.SecurityHeaders;

public sealed class SecurityMiddlewareTests
{
    private const string AllowedOrigin = "https://uploads.example.test";
    private const string AntiforgeryToken = "browser-antiforgery-token";

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public async Task Security_headers_vary_by_environment_without_blocking_spa_or_media(
        string environment,
        bool expectsHsts)
    {
        await using SecurityTestHost host = SecurityTestHost.Create(environment);

        TestResponse spa = await host.SendAsync("GET", "/app.js", isHttps: true);
        TestResponse media = await host.SendAsync("GET", "/media/example.webp", isHttps: true);

        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Equal("spa-script", spa.Body);
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        Assert.Equal("media-body", media.Body);
        Assert.Equal("nosniff", spa.Header("X-Content-Type-Options"));
        Assert.Equal("no-referrer", spa.Header("Referrer-Policy"));
        Assert.Contains("camera=()", spa.Header("Permissions-Policy"));
        Assert.Contains("img-src 'self' data: blob:", spa.Header("Content-Security-Policy"));
        Assert.Contains("media-src 'self' blob:", media.Header("Content-Security-Policy"));
        Assert.Equal(expectsHsts, spa.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.Equal(
            environment == Environments.Development,
            spa.Header("Content-Security-Policy").Contains(
                "'unsafe-eval'",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cors_allows_only_the_exact_configured_origin_and_contract()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production);

        TestResponse allowed = await host.SendAsync(
            "OPTIONS",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["Origin"] = AllowedOrigin,
                ["Access-Control-Request-Method"] = "POST",
                ["Access-Control-Request-Headers"] = "content-type,x-vistara-csrf",
            });
        TestResponse denied = await host.SendAsync(
            "OPTIONS",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["Origin"] = "https://attacker.example",
                ["Access-Control-Request-Method"] = "POST",
            });

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(AllowedOrigin, allowed.Header("Access-Control-Allow-Origin"));
        Assert.Equal("true", allowed.Header("Access-Control-Allow-Credentials"));
        Assert.Contains("POST", allowed.Header("Access-Control-Allow-Methods"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.False(denied.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.Equal("cors.origin_denied", denied.ProblemCode());
    }

    [Fact]
    public async Task Cors_does_not_reject_the_integrated_spa_same_origin()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production);

        TestResponse response = await host.SendAsync(
            "POST",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["Origin"] = "https://vistara.example.test",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.True(response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Cookie_authenticated_mutations_still_require_antiforgery()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production);
        var cookieHeaders = new Dictionary<string, string>
        {
            ["Cookie"] = $"{CookieAuthOptions.ProductionCookieName}=session",
        };

        TestResponse missing = await host.SendAsync(
            "POST",
            "/api/v1/protected",
            cookieHeaders);
        cookieHeaders[CookieAuthOptions.DefaultAntiforgeryHeaderName] =
            AntiforgeryToken;
        TestResponse valid = await host.SendAsync(
            "POST",
            "/api/v1/protected",
            cookieHeaders);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(
            "cookie_auth.antiforgery_required",
            missing.ProblemCode());
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task Malformed_requests_and_unhandled_errors_return_safe_problem_details()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production);

        TestResponse malformed = await host.SendAsync(
            "POST",
            "/api/v1/malformed",
            contentType: "application/json",
            body: """{"value":""");
        TestResponse failed = await host.SendAsync("GET", "/api/v1/fail");

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("request.malformed", malformed.ProblemCode());
        Assert.DoesNotContain("JsonException", malformed.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("platform.unhandled_error", failed.ProblemCode());
        Assert.DoesNotContain("sensitive failure detail", failed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_body_and_rate_limits_fail_with_safe_problem_details()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Limits:MaxRequestBodyBytes"] = "8",
                ["Security:Limits:RequestsPerWindow"] = "1",
            });

        TestResponse oversized = await host.SendAsync(
            "POST",
            "/api/v1/probe",
            body: "123456789");
        TestResponse first = await host.SendAsync("GET", "/api/v1/probe");
        TestResponse limited = await host.SendAsync("GET", "/api/v1/probe");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal("request.body_too_large", oversized.ProblemCode());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limit.exceeded", limited.ProblemCode());
        Assert.True(limited.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Request_target_and_headers_are_bounded()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Limits:MaxRequestTargetBytes"] = "256",
                ["Security:Limits:MaxRequestHeaderBytes"] = "1024",
            });

        TestResponse target = await host.SendAsync(
            "GET",
            $"/api/v1/probe?value={new string('a', 256)}");
        TestResponse headers = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Large"] = new string('b', 1100),
            });

        Assert.Equal(HttpStatusCode.RequestUriTooLong, target.StatusCode);
        Assert.Equal("request.target_too_long", target.ProblemCode());
        Assert.Equal(
            HttpStatusCode.RequestHeaderFieldsTooLarge,
            headers.StatusCode);
        Assert.Equal("request.headers_too_large", headers.ProblemCode());
    }

    [Fact]
    public async Task Startup_rejects_a_configured_required_secret_that_is_missing()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = Environments.Production,
            });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:RequiredSecretKeys:0"] = "Secrets:SigningKey",
            });
        builder.Services.AddVistaraApiSecurity(
            builder.Configuration,
            builder.Environment);
        using IHost host = builder.Build();

        OptionsValidationException error =
            await Assert.ThrowsAsync<OptionsValidationException>(
                () => host.StartAsync());

        Assert.Contains("Secrets:SigningKey", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SigningKey=", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://uploads.example.test")]
    public async Task Production_startup_rejects_unsafe_cors_origins(string origin)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = Environments.Production,
            });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:Cors:AllowedOrigins:0"] = origin,
            });
        builder.Services.AddVistaraApiSecurity(
            builder.Configuration,
            builder.Environment);
        using IHost host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());
    }

    [Fact]
    public async Task Request_logging_never_emits_sensitive_headers_urls_or_metadata()
    {
        var logs = new RecordingLoggerProvider();
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            loggerProvider: logs);

        await host.SendAsync(
            "GET",
            "/api/v1/probe?sig=signed-url-secret&metadata=private-metadata",
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer test-authorization-marker",
                ["X-API-Key"] = "test-api-key-marker",
                ["X-Private-Metadata"] = "private-header-value",
            });

        string output = string.Join('\n', logs.Messages);
        Assert.Contains("sensitive request data redacted", output, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-url-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-metadata", output, StringComparison.Ordinal);
        Assert.DoesNotContain("test-authorization-marker", output, StringComparison.Ordinal);
        Assert.DoesNotContain("test-api-key-marker", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-header-value", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_assembly_registers_security_through_the_hosting_composition_hook()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ApplicationName = typeof(Program).Assembly.FullName,
                EnvironmentName = Environments.Production,
            });

        Assert.Contains(
            builder.Services,
            service => service.ServiceType ==
                typeof(IVistaraSecurityRegistration));
    }

    [Fact]
    public async Task Hosting_composition_hook_applies_security_to_the_real_pipeline()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ApplicationName = typeof(Program).Assembly.FullName,
                EnvironmentName = Environments.Production,
            });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        await using WebApplication app = builder.Build();
        app.MapGet("/probe", () => Results.Text("ok"));
        app.Urls.Add("http://127.0.0.1:0");

        await app.StartAsync();
        string address = Assert.Single(
            app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(address),
        };
        using HttpResponseMessage response = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "nosniff",
            Assert.Single(response.Headers.GetValues(
                "X-Content-Type-Options")));
        await app.StopAsync();
    }

    [Fact]
    public void Security_composition_suppresses_framework_request_url_logging()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = Environments.Production,
            });
        builder.Services.AddVistaraApiSecurity(
            builder.Configuration,
            builder.Environment);
        using IHost host = builder.Build();

        LoggerFilterOptions filters = host.Services
            .GetRequiredService<IOptions<LoggerFilterOptions>>()
            .Value;

        Assert.Contains(
            filters.Rules,
            rule =>
                rule.CategoryName ==
                    "Microsoft.AspNetCore.Hosting.Diagnostics" &&
                rule.LogLevel == LogLevel.Warning);
    }

    private sealed record ProbeBody(string Value);

    private sealed class SecurityTestHost : IAsyncDisposable
    {
        private static readonly Guid UserId =
            Guid.Parse("01990a2a-bc00-7000-8000-000000000201");
        private static readonly Guid TenantId =
            Guid.Parse("01990a2a-bc00-7000-8000-000000000202");
        private readonly WebApplication _app;
        private readonly RequestDelegate _pipeline;

        private SecurityTestHost(WebApplication app, RequestDelegate pipeline)
        {
            _app = app;
            _pipeline = pipeline;
        }

        internal static SecurityTestHost Create(
            string environment,
            IReadOnlyDictionary<string, string?>? overrides = null,
            ILoggerProvider? loggerProvider = null)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    EnvironmentName = environment,
                });
            builder.Configuration.Sources.Clear();
            Dictionary<string, string?> settings = ValidSettings();
            if (overrides is not null)
            {
                foreach ((string key, string? value) in overrides)
                {
                    settings[key] = value;
                }
            }

            builder.Configuration.AddInMemoryCollection(settings);
            builder.Logging.ClearProviders();
            if (loggerProvider is not null)
            {
                builder.Logging.AddProvider(loggerProvider);
            }

            var credentials = new FakeCredentialAuthenticators();
            builder.Services.AddSingleton<IPlatformCookieAuthenticator>(credentials);
            builder.Services.AddSingleton<IPlatformApiKeyAuthenticator>(credentials);
            builder.Services.AddSingleton<IPlatformBearerAuthenticator>(credentials);
            builder.Services.AddVistaraApiSecurity(
                builder.Configuration,
                builder.Environment);
            builder.Services.AddVistaraApiPlatform(builder.Configuration);

            WebApplication app = builder.Build();
            app.UseVistaraApiSecurity();
            app.UseVistaraPlatform();
            app.MapMethods(
                    "/api/v1/probe",
                    ["GET", "POST", "OPTIONS"],
                    () => Results.Text("probe"))
                .AllowAnonymous();
            app.MapPost(
                    "/api/v1/protected",
                    () => Results.Text("protected"))
                .RequireAuthorization();
            app.MapPost(
                    "/api/v1/malformed",
                    (ProbeBody body) => Results.Json(body))
                .AllowAnonymous();
            app.MapGet(
                    "/api/v1/fail",
                    (Func<IResult>)(() =>
                        throw new InvalidOperationException(
                            "sensitive failure detail")))
                .AllowAnonymous();
            app.MapGet("/app.js", () => Results.Text("spa-script"))
                .AllowAnonymous();
            app.MapGet("/media/example.webp", () => Results.Text("media-body"))
                .AllowAnonymous();
            RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
            return new SecurityTestHost(app, pipeline);
        }

        internal async Task<TestResponse> SendAsync(
            string method,
            string path,
            IReadOnlyDictionary<string, string>? headers = null,
            bool isHttps = false,
            string? contentType = null,
            string? body = null)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
            };
            context.Request.Method = method;
            context.Request.Scheme = isHttps ? "https" : "http";
            context.Request.Host = new HostString("vistara.example.test");
            int queryIndex = path.IndexOf('?', StringComparison.Ordinal);
            context.Request.Path = queryIndex < 0 ? path : path[..queryIndex];
            context.Request.QueryString = queryIndex < 0
                ? QueryString.Empty
                : new QueryString(path[queryIndex..]);
            context.Response.Body = new MemoryStream();
            if (headers is not null)
            {
                foreach ((string name, string value) in headers)
                {
                    context.Request.Headers[name] = value;
                }
            }

            if (body is not null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.ContentType = contentType;
            }

            await _pipeline(context);
            context.Response.Body.Position = 0;
            string responseBody = await new StreamReader(
                    context.Response.Body,
                    Encoding.UTF8)
                .ReadToEndAsync();
            return new TestResponse(
                (HttpStatusCode)context.Response.StatusCode,
                context.Response.Headers,
                responseBody);
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private static Dictionary<string, string?> ValidSettings() => new()
        {
            ["Security:Cors:AllowedOrigins:0"] = AllowedOrigin,
            ["Security:Limits:MaxRequestBodyBytes"] = "52428800",
            ["Security:Limits:RequestsPerWindow"] = "100",
        };

        private sealed class FakeCredentialAuthenticators :
            IPlatformCookieAuthenticator,
            IPlatformApiKeyAuthenticator,
            IPlatformBearerAuthenticator
        {
            public ValueTask<PlatformCredentialResult> AuthenticateCookieAsync(
                string sessionToken,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(
                    PlatformCredentialResult.Success(
                        new PlatformIdentity(
                            UserId,
                            TenantId,
                            "Member",
                            ["assets.read"],
                            CookieTokenCryptography.ComputeDigest(
                                AntiforgeryToken))));

            public ValueTask<PlatformCredentialResult> AuthenticateApiKeyAsync(
                string apiKey,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(
                    PlatformCredentialResult.Success(
                        new PlatformIdentity(
                            UserId,
                            TenantId,
                            "Member",
                            ["assets.read"],
                            null)));

            public ValueTask<PlatformCredentialResult> AuthenticateBearerAsync(
                string bearerToken,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(
                    PlatformCredentialResult.Success(
                        new PlatformIdentity(
                            UserId,
                            TenantId,
                            "Member",
                            ["assets.read"],
                            null)));
        }
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        IHeaderDictionary Headers,
        string Body)
    {
        internal string Header(string name) => Headers[name].ToString();

        internal string? ProblemCode()
        {
            using JsonDocument document = JsonDocument.Parse(Body);
            return document.RootElement.TryGetProperty("code", out JsonElement code)
                ? code.GetString()
                : null;
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        internal IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<string> messages) : ILogger
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
                messages.Add(formatter(state, exception));
            }
        }
    }
}
