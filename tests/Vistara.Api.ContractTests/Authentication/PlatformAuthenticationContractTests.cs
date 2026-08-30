using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Media;
using Vistara.Auth.Cookies;
using Vistara.Contracts.Media;
using Xunit;

namespace Vistara.Api.ContractTests.Authentication;

public sealed class PlatformAuthenticationContractTests
{
    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000201");
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000202");
    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000203");
    private const string PublicMediaPath =
        "/media/v1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/" +
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.webp";

    [Fact]
    public void Authentication_selector_rejects_credential_scheme_confusion()
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer token";
        context.Request.Headers["X-API-Key"] = "vst_key";
        context.Request.Headers.Cookie =
            $"{CookieAuthOptions.ProductionCookieName}=session";

        string scheme = PlatformAuthenticationSelector.Select(context.Request);

        Assert.Equal(PlatformAuthenticationDefaults.ConfusedScheme, scheme);
    }

    [Theory]
    [InlineData("Bearer token", null, null, PlatformAuthenticationDefaults.BearerScheme)]
    [InlineData(null, "vst_key", null, PlatformAuthenticationDefaults.ApiKeyScheme)]
    [InlineData(null, null, "session", PlatformAuthenticationDefaults.CookieScheme)]
    [InlineData(null, null, null, PlatformAuthenticationDefaults.AnonymousScheme)]
    public void Authentication_selector_uses_explicit_secure_precedence(
        string? authorization,
        string? apiKey,
        string? cookie,
        string expectedScheme)
    {
        DefaultHttpContext context = new();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        if (apiKey is not null)
        {
            context.Request.Headers["X-API-Key"] = apiKey;
        }

        if (cookie is not null)
        {
            context.Request.Headers.Cookie =
                $"{CookieAuthOptions.ProductionCookieName}={cookie}";
        }

        Assert.Equal(
            expectedScheme,
            PlatformAuthenticationSelector.Select(context.Request));
    }

    [Fact]
    public async Task Public_media_bypasses_cookie_authentication_and_rotation()
    {
        var credentials = new FakeCredentialAuthenticators
        {
            CookieResult = PlatformCredentialResult.Success(
                new PlatformIdentity(
                    UserId,
                    TenantId,
                    "Member",
                    ["assets.read"],
                    CookieTokenCryptography.ComputeDigest("valid-csrf")),
                "vistara_session=rotated; Path=/; Secure; HttpOnly"),
        };
        await using TestPlatformHost host = CreateHost(credentials);

        TestResponse response = await host.SendAsync(
            "GET",
            PublicMediaPath,
            headers: new Dictionary<string, string>
            {
                ["Cookie"] = $"{CookieAuthOptions.ProductionCookieName}=valid",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public-media", response.Body);
        Assert.Equal(0, credentials.CookieCalls);
        Assert.False(response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal(
            MediaDeliveryHttpContract.PublicImmutableCacheControl,
            response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Non_public_delivery_paths_still_authenticate_cookie_requests()
    {
        var credentials = new FakeCredentialAuthenticators
        {
            CookieResult = PlatformCredentialResult.Success(
                new PlatformIdentity(
                    UserId,
                    TenantId,
                    "Member",
                    ["assets.read"],
                    CookieTokenCryptography.ComputeDigest("valid-csrf")),
                "vistara_session=rotated; Path=/; Secure; HttpOnly"),
        };
        await using TestPlatformHost host = CreateHost(credentials);

        TestResponse response = await host.SendAsync(
            "GET",
            "/delivery/private.webp",
            headers: new Dictionary<string, string>
            {
                ["Cookie"] = $"{CookieAuthOptions.ProductionCookieName}=valid",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId.ToString("D"), response.Body);
        Assert.Equal(1, credentials.CookieCalls);
        Assert.True(response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Confused_credentials_fail_closed_before_any_authenticator_runs()
    {
        var credentials = new FakeCredentialAuthenticators();
        await using TestPlatformHost host = CreateHost(credentials);

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer valid",
                ["X-API-Key"] = "valid",
                ["Cookie"] = $"{CookieAuthOptions.ProductionCookieName}=valid",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authentication.scheme_confusion", response.ProblemCode());
        Assert.Equal(0, credentials.TotalCalls);
    }

    [Fact]
    public async Task Invalid_higher_precedence_credential_never_falls_back()
    {
        var credentials = new FakeCredentialAuthenticators
        {
            BearerResult = PlatformCredentialResult.Invalid("jwt.invalid_token"),
        };
        await using TestPlatformHost host = CreateHost(credentials);

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer invalid",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, credentials.BearerCalls);
        Assert.Equal(0, credentials.ApiKeyCalls);
        Assert.Equal(0, credentials.CookieCalls);
    }

    [Fact]
    public async Task Missing_credentials_are_challenged()
    {
        await using TestPlatformHost host = CreateHost(
            new FakeCredentialAuthenticators());

        TestResponse response = await host.SendAsync("GET", "/api/v1/probe");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authentication.required", response.ProblemCode());
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(PlatformAuthenticationKind.Bearer)]
    [InlineData(PlatformAuthenticationKind.ApiKey)]
    public async Task Non_cookie_credentials_do_not_require_antiforgery(
        PlatformAuthenticationKind kind)
    {
        var credentials = new FakeCredentialAuthenticators();
        await using TestPlatformHost host = CreateHost(credentials);

        Dictionary<string, string> headers = kind switch
        {
            PlatformAuthenticationKind.Bearer => new()
            {
                ["Authorization"] = "Bearer valid",
            },
            PlatformAuthenticationKind.ApiKey => new()
            {
                ["X-API-Key"] = "valid",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        TestResponse response = await host.SendAsync(
            "POST",
            "/api/v1/probe",
            headers: headers);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId.ToString("D"), response.Body);
    }

    [Fact]
    public async Task Unsafe_cookie_requests_require_the_bound_antiforgery_token()
    {
        const string antiforgeryToken = "correct-antiforgery-token";
        var credentials = new FakeCredentialAuthenticators
        {
            CookieResult = PlatformCredentialResult.Success(
                new PlatformIdentity(
                    UserId,
                    TenantId,
                    "Member",
                    ["assets.read"],
                    CookieTokenCryptography.ComputeDigest(antiforgeryToken))),
        };
        await using TestPlatformHost host = CreateHost(credentials);
        var cookie = new Dictionary<string, string>
        {
            ["Cookie"] = $"{CookieAuthOptions.ProductionCookieName}=valid",
        };

        TestResponse missing = await host.SendAsync(
            "POST",
            "/api/v1/probe",
            headers: cookie);
        cookie["X-Vistara-CSRF"] = antiforgeryToken;
        TestResponse valid = await host.SendAsync(
            "POST",
            "/api/v1/probe",
            headers: cookie);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal("cookie_auth.antiforgery_required", missing.ProblemCode());
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task Tenant_context_comes_from_the_validated_identity_not_a_header()
    {
        await using TestPlatformHost host = CreateHost(
            new FakeCredentialAuthenticators());

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer valid",
                ["X-Tenant-ID"] = OtherTenantId.ToString("D"),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId.ToString("D"), response.Body);
    }

    [Fact]
    public async Task Authentication_and_tenant_context_run_before_authorized_endpoint()
    {
        await using TestPlatformHost host = CreateHost(
            new FakeCredentialAuthenticators());

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            headers: new Dictionary<string, string>
            {
                ["X-API-Key"] = "valid",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId.ToString("D"), response.Body);
        Assert.True(response.Headers.ContainsKey("X-Correlation-ID"));
    }

    [Fact]
    public async Task Rate_limit_hook_runs_before_authentication_and_endpoints()
    {
        var credentials = new FakeCredentialAuthenticators();
        await using TestPlatformHost host = TestPlatformHost.Create(
            credentials,
            ValidSettings(),
            new RejectingRateLimitHook());

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer valid",
            });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("rate_limit.exceeded", response.ProblemCode());
        Assert.Equal("5", response.Headers.RetryAfter);
        Assert.Equal(0, credentials.TotalCalls);
    }

    [Fact]
    public async Task Startup_rejects_invalid_pepper_and_missing_issuer_without_disclosing_secrets()
    {
        const string secret = "do-not-print-this-secret";
        var missingPepper = ValidSettings();
        missingPepper["Platform:Authentication:ApiKeys:Peppers:v1"] = secret;
        OptionsValidationException pepperError =
            await AssertInvalidStartupAsync(missingPepper);

        var missingIssuer = ValidSettings();
        missingIssuer.Remove("Platform:Authentication:Jwt:Issuers:0:Issuer");
        OptionsValidationException issuerError =
            await AssertInvalidStartupAsync(missingIssuer);

        Assert.DoesNotContain(secret, pepperError.ToString(), StringComparison.Ordinal);
        Assert.Contains("pepper", pepperError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("issuer", issuerError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/v1/missing")]
    [InlineData("/api/v1/missing")]
    [InlineData("/media/missing")]
    [InlineData("/health/missing")]
    public async Task Spa_fallback_never_captures_reserved_routes(string path)
    {
        await using TestPlatformHost host = CreateHost(
            new FakeCredentialAuthenticators());

        TestResponse response = await host.SendAsync("GET", path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("spa-shell", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spa_fallback_is_last_and_handles_only_navigation_routes()
    {
        await using TestPlatformHost host = CreateHost(
            new FakeCredentialAuthenticators());

        TestResponse response = await host.SendAsync("GET", "/gallery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("spa-shell", response.Body);
    }

    [Fact]
    public async Task Request_cancellation_reaches_authentication_without_becoming_a_problem()
    {
        var credentials = new FakeCredentialAuthenticators
        {
            CancelBearer = true,
        };
        await using TestPlatformHost host = CreateHost(credentials);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await host.SendAsync(
                "GET",
                "/api/v1/probe",
                headers: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer valid",
                },
                cancellationToken: cancellation.Token));
    }

    private static TestPlatformHost CreateHost(
        FakeCredentialAuthenticators credentials) =>
        TestPlatformHost.Create(credentials, ValidSettings());

    private static async Task<OptionsValidationException> AssertInvalidStartupAsync(
        IReadOnlyDictionary<string, string?> settings)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        var credentials = new FakeCredentialAuthenticators();
        builder.Services.AddSingleton<IPlatformCookieAuthenticator>(credentials);
        builder.Services.AddSingleton<IPlatformApiKeyAuthenticator>(credentials);
        builder.Services.AddSingleton<IPlatformBearerAuthenticator>(credentials);
        builder.Services.AddVistaraApiPlatform(builder.Configuration);
        using IHost host = builder.Build();
        return await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
        ["Platform:Authentication:ApiKeys:Peppers:v1"] =
            "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=",
        ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] = "contract-tests",
        ["Platform:Authentication:Jwt:Issuers:0:Issuer"] =
            "https://issuer.example",
        ["Platform:Authentication:Jwt:Issuers:0:Audience"] = "vistara-api",
        ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
            "https://issuer.example/.well-known/openid-configuration",
        ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] = "RS256",
    };

    private sealed class TestPlatformHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RequestDelegate _pipeline;

        private TestPlatformHost(WebApplication app, RequestDelegate pipeline)
        {
            _app = app;
            _pipeline = pipeline;
        }

        internal static TestPlatformHost Create(
            FakeCredentialAuthenticators credentials,
            IReadOnlyDictionary<string, string?> settings,
            IPlatformRateLimitHook? rateLimitHook = null)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    EnvironmentName = Environments.Development,
                });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<IPlatformCookieAuthenticator>(credentials);
            builder.Services.AddSingleton<IPlatformApiKeyAuthenticator>(credentials);
            builder.Services.AddSingleton<IPlatformBearerAuthenticator>(credentials);
            if (rateLimitHook is not null)
            {
                builder.Services.AddSingleton<IPlatformRateLimitHook>(rateLimitHook);
            }

            builder.Services.AddSingleton<IMediaDeliveryApplicationPort>(
                new PublicMediaApplicationPort());
            builder.Services.AddVistaraApiPlatform(builder.Configuration);

            WebApplication app = builder.Build();
            app.UseVistaraPlatform();
            app.MapMethods(
                    "/api/v1/probe",
                    ["GET", "POST"],
                    TenantText)
                .RequireAuthorization();
            app.MapVistaraMedia();
            app.MapGet("/delivery/private.webp", TenantText)
                .RequireAuthorization();
            app.UseVistaraSpaFallback(
                context => context.Response.WriteAsync("spa-shell"));
            RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
            return new TestPlatformHost(app, pipeline);
        }

        private static IResult TenantText(HttpContext context) =>
            Results.Text(
                context.RequestServices
                    .GetRequiredService<IPlatformTenantContext>()
                    .TenantId?
                    .ToString("D") ?? "missing");

        internal async Task<TestResponse> SendAsync(
            string method,
            string path,
            IReadOnlyDictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                RequestAborted = cancellationToken,
            };
            context.Request.Method = method;
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();
            if (headers is not null)
            {
                foreach ((string name, string value) in headers)
                {
                    context.Request.Headers[name] = value;
                }
            }

            await _pipeline(context);
            context.Response.Body.Position = 0;
            string body = await new StreamReader(context.Response.Body, Encoding.UTF8)
                .ReadToEndAsync(CancellationToken.None);
            return new TestResponse(
                (HttpStatusCode)context.Response.StatusCode,
                context.Response.Headers,
                body);
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        IHeaderDictionary Headers,
        string Body)
    {
        internal string? ProblemCode()
        {
            using JsonDocument document = JsonDocument.Parse(Body);
            return document.RootElement.TryGetProperty("code", out JsonElement code)
                ? code.GetString()
                : null;
        }
    }

    private sealed class FakeCredentialAuthenticators :
        IPlatformCookieAuthenticator,
        IPlatformApiKeyAuthenticator,
        IPlatformBearerAuthenticator
    {
        public PlatformCredentialResult CookieResult { get; init; } =
            PlatformCredentialResult.Success(
                new PlatformIdentity(
                    UserId,
                    TenantId,
                    "Member",
                    ["assets.read"],
                    CookieTokenCryptography.ComputeDigest("valid-csrf")));

        public PlatformCredentialResult ApiKeyResult { get; init; } =
            PlatformCredentialResult.Success(
                new PlatformIdentity(
                    UserId,
                    TenantId,
                    "Member",
                    ["assets.read"],
                    null));

        public PlatformCredentialResult BearerResult { get; init; } =
            PlatformCredentialResult.Success(
                new PlatformIdentity(
                    UserId,
                    TenantId,
                    "Member",
                    ["assets.read"],
                    null));

        public bool CancelBearer { get; init; }
        public int CookieCalls { get; private set; }
        public int ApiKeyCalls { get; private set; }
        public int BearerCalls { get; private set; }
        public int TotalCalls => CookieCalls + ApiKeyCalls + BearerCalls;

        public ValueTask<PlatformCredentialResult> AuthenticateCookieAsync(
            string sessionToken,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CookieCalls++;
            return ValueTask.FromResult(CookieResult);
        }

        public ValueTask<PlatformCredentialResult> AuthenticateApiKeyAsync(
            string apiKey,
            HttpContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApiKeyCalls++;
            return ValueTask.FromResult(ApiKeyResult);
        }

        public ValueTask<PlatformCredentialResult> AuthenticateBearerAsync(
            string bearerToken,
            CancellationToken cancellationToken)
        {
            if (CancelBearer)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            BearerCalls++;
            return ValueTask.FromResult(BearerResult);
        }
    }

    private sealed class RejectingRateLimitHook : IPlatformRateLimitHook
    {
        public ValueTask<PlatformRateLimitDecision> CheckAsync(
            HttpContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                PlatformRateLimitDecision.Reject(TimeSpan.FromSeconds(5)));
    }

    private sealed class PublicMediaApplicationPort :
        IMediaDeliveryApplicationPort
    {
        private static readonly byte[] Content =
            Encoding.UTF8.GetBytes("public-media");

        public ValueTask<MediaDeliveryResult> ResolvePublicDerivativeAsync(
            MediaDerivativeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                MediaDeliveryResult.Ready(
                    new MediaRepresentation(
                        Content.Length,
                        "image/webp",
                        new string('c', 64),
                        new PublicMediaContentSource())));
        }

        public ValueTask<MediaDeliveryResult> ResolvePrivateDerivativeAsync(
            MediaTenantScope scope,
            MediaDerivativeRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MediaDeliveryResult> ResolveOriginalAsync(
            MediaAssetScope scope,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class PublicMediaContentSource : IMediaContentSource
        {
            public ValueTask<MediaReadHandle> OpenReadAsync(
                MediaByteRange? range,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    new MediaReadHandle(
                        new MemoryStream(Content, writable: false)));
            }
        }
    }
}
