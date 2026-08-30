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
        if (expectsHsts)
        {
            Assert.Equal(
                "max-age=31536000; includeSubDomains",
                spa.Header("Strict-Transport-Security"));
        }

        Assert.Equal(
            environment == Environments.Development,
            spa.Header("Content-Security-Policy").Contains(
                "'unsafe-eval'",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.PermanentRedirect)]
    public async Task Plain_http_is_redirected_only_outside_development(
        string environment,
        HttpStatusCode expectedStatus)
    {
        await using SecurityTestHost host = SecurityTestHost.Create(environment);

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe?value=one",
            isHttps: false);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.False(response.Headers.ContainsKey("Strict-Transport-Security"));
        if (environment == Environments.Production)
        {
            Assert.Equal(
                "https://vistara.example.test/api/v1/probe?value=one",
                response.Header("Location"));
        }
    }

    [Fact]
    public async Task Production_http_redirect_can_be_disabled_for_an_http_only_proxy()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Transport:RedirectHttpToHttps"] = "false",
            });

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            isHttps: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.ContainsKey("Location"));
        Assert.False(response.Headers.ContainsKey("Strict-Transport-Security"));
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
            },
            isHttps: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.True(response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Cors_does_not_run_before_plain_http_is_redirected()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production);

        TestResponse response = await host.SendAsync(
            "POST",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["Origin"] = "https://vistara.example.test",
            },
            isHttps: false);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(
            "https://vistara.example.test/api/v1/probe",
            response.Header("Location"));
        Assert.False(response.Headers.ContainsKey("Access-Control-Allow-Origin"));
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
    public async Task Existing_non_seekable_bad_request_content_is_preserved()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production);

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/existing-bad-request",
            useNonSeekableResponseBody: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("existing-bad-request", response.Body);
        Assert.Equal("text/plain", response.Header("Content-Type"));
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
    public async Task Direct_clients_have_independent_rate_limit_partitions()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Limits:RequestsPerWindow"] = "1",
            });

        TestResponse first = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            remoteIpAddress: IPAddress.Parse("198.51.100.10"));
        TestResponse second = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            remoteIpAddress: IPAddress.Parse("198.51.100.11"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Untrusted_forwarded_for_cannot_change_the_rate_limit_partition()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Limits:RequestsPerWindow"] = "1",
            });
        IPAddress untrustedPeer = IPAddress.Parse("198.51.100.20");

        TestResponse first = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.10",
            },
            remoteIpAddress: untrustedPeer);
        TestResponse second = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.11",
            },
            remoteIpAddress: untrustedPeer);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Loopback_is_not_a_trusted_proxy_unless_it_is_configured()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Limits:RequestsPerWindow"] = "1",
            });
        IPAddress loopbackPeer = IPAddress.Loopback;

        TestResponse first = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.30",
            },
            remoteIpAddress: loopbackPeer);
        TestResponse second = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.31",
            },
            remoteIpAddress: loopbackPeer);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Trusted_proxy_forwarded_clients_have_independent_rate_limit_partitions()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Limits:RequestsPerWindow"] = "1",
                ["Security:Proxy:KnownProxies:0"] = "192.0.2.10",
            });
        IPAddress trustedProxy = IPAddress.Parse("192.0.2.10");

        TestResponse first = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.20",
            },
            remoteIpAddress: trustedProxy);
        TestResponse second = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.21",
            },
            remoteIpAddress: trustedProxy);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Forwarded_host_is_accepted_only_from_a_trusted_proxy_and_is_filtered()
    {
        var proxySettings = new Dictionary<string, string?>
        {
            ["Security:Proxy:KnownProxies:0"] = "192.0.2.10",
        };
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            proxySettings);

        TestResponse untrusted = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "vistara.example.test",
                ["X-Forwarded-Proto"] = "https",
            },
            isHttps: false,
            remoteIpAddress: IPAddress.Parse("198.51.100.40"),
            host: "attacker.example.test");
        TestResponse trusted = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "vistara.example.test",
                ["X-Forwarded-Proto"] = "https",
            },
            isHttps: false,
            remoteIpAddress: IPAddress.Parse("192.0.2.10"),
            host: "internal-proxy");
        TestResponse rejectedForwardedHost = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "attacker.example.test",
                ["X-Forwarded-Proto"] = "https",
            },
            isHttps: false,
            remoteIpAddress: IPAddress.Parse("192.0.2.10"),
            host: "internal-proxy");

        Assert.Equal(HttpStatusCode.BadRequest, untrusted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedForwardedHost.StatusCode);
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
    public async Task Request_header_count_is_bounded()
    {
        await using SecurityTestHost host = SecurityTestHost.Create(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Security:Limits:MaxRequestHeaderCount"] = "1",
            });

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/probe",
            new Dictionary<string, string>
            {
                ["X-Extra"] = "value",
            });

        Assert.Equal(
            HttpStatusCode.RequestHeaderFieldsTooLarge,
            response.StatusCode);
        Assert.Equal("request.headers_too_large", response.ProblemCode());
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

    [Theory]
    [InlineData("*")]
    [InlineData("https://vistara.example.test")]
    [InlineData("vistara.example.test:443")]
    public async Task Startup_rejects_unsafe_allowed_hosts(string allowedHost)
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
                ["Security:Hosts:AllowedHosts:0"] = allowedHost,
            });
        builder.Services.AddVistaraApiSecurity(
            builder.Configuration,
            builder.Environment);
        using IHost host = builder.Build();

        OptionsValidationException error =
            await Assert.ThrowsAsync<OptionsValidationException>(
                () => host.StartAsync());

        Assert.Contains("host", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(allowedHost, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Security:Proxy:KnownProxies:0", "not-an-ip-address")]
    [InlineData("Security:Proxy:KnownNetworks:0", "192.0.2.0/not-a-prefix")]
    public async Task Startup_rejects_invalid_trusted_proxy_configuration(
        string key,
        string value)
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
                [key] = value,
            });
        builder.Services.AddVistaraApiSecurity(
            builder.Configuration,
            builder.Environment);
        using IHost host = builder.Build();

        OptionsValidationException error =
            await Assert.ThrowsAsync<OptionsValidationException>(
                () => host.StartAsync());

        Assert.Contains("proxy", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
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
                ["X-Forwarded-For"] = "203.0.113.77",
            });

        string output = string.Join('\n', logs.Messages);
        Assert.Contains("sensitive request data redacted", output, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-url-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-metadata", output, StringComparison.Ordinal);
        Assert.DoesNotContain("test-authorization-marker", output, StringComparison.Ordinal);
        Assert.DoesNotContain("test-api-key-marker", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-header-value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100.10", output, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.77", output, StringComparison.Ordinal);
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
        var logs = new RecordingLoggerProvider();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ApplicationName = typeof(Program).Assembly.FullName,
                EnvironmentName = Environments.Production,
            });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:Hosts:AllowedHosts:0"] = "public.example.test",
                ["Security:Limits:MaxRequestBodyBytes"] = "16",
                ["Security:Proxy:KnownProxies:0"] = "127.0.0.1",
            });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        await using WebApplication app = builder.Build();
        app.MapPost(
                "/api/v1/startup-bind",
                (ProbeBody body) => Results.Json(body))
            .WithDisplayName("unsafe-body-binding");
        app.MapGet("/api/v1/startup-probe", () => Results.Text("ok"))
            .WithDisplayName("safe-startup-probe");
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
        using var oversizedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/startup-bind");
        oversizedRequest.Headers.TryAddWithoutValidation(
            "Origin",
            "https://public.example.test");
        oversizedRequest.Headers.TryAddWithoutValidation(
            "X-Forwarded-For",
            "203.0.113.90");
        oversizedRequest.Headers.TryAddWithoutValidation(
            "X-Forwarded-Host",
            "public.example.test");
        oversizedRequest.Headers.TryAddWithoutValidation(
            "X-Forwarded-Proto",
            "https");
        oversizedRequest.Content = new StringContent(
            """{"value":"too-long"}""",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage oversizedResponse =
            await client.SendAsync(oversizedRequest);
        string oversizedBody = await oversizedResponse.Content.ReadAsStringAsync();
        using var malformedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/startup-bind");
        malformedRequest.Headers.Host = "public.example.test";
        malformedRequest.Headers.TryAddWithoutValidation(
            "X-Forwarded-Proto",
            "https");
        malformedRequest.Content = new StringContent(
            """{"value":""",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage malformedResponse =
            await client.SendAsync(malformedRequest);
        string malformedBody = await malformedResponse.Content.ReadAsStringAsync();
        using var routedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/startup-probe?sig=startup-sensitive-query");
        routedRequest.Headers.Host = "public.example.test";
        routedRequest.Headers.TryAddWithoutValidation(
            "X-Forwarded-Proto",
            "https");
        using HttpResponseMessage routedResponse =
            await client.SendAsync(routedRequest);

        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            oversizedResponse.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(oversizedBody))
        {
            Assert.Equal(
                "request.body_too_large",
                document.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(malformedBody))
        {
            Assert.Equal(
                "request.malformed",
                document.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, routedResponse.StatusCode);
        Assert.Equal(
            "nosniff",
            Assert.Single(oversizedResponse.Headers.GetValues(
                "X-Content-Type-Options")));
        string output = string.Join('\n', logs.Messages);
        Assert.Contains("safe-startup-probe", output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "startup-sensitive-query",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.90", output, StringComparison.Ordinal);
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

        string[] requestDataCategories =
        [
            "Microsoft.AspNetCore.Hosting.Diagnostics",
            "Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware",
            "Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware",
        ];
        foreach (string category in requestDataCategories)
        {
            Assert.Contains(
                filters.Rules,
                rule =>
                    rule.CategoryName == category &&
                    rule.LogLevel == LogLevel.Warning);
        }
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
            app.MapGet(
                    "/api/v1/existing-bad-request",
                    async context =>
                    {
                        context.Response.StatusCode =
                            StatusCodes.Status400BadRequest;
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync(
                            "existing-bad-request");
                    })
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
            bool isHttps = true,
            string? contentType = null,
            string? body = null,
            IPAddress? remoteIpAddress = null,
            bool useNonSeekableResponseBody = false,
            string host = "vistara.example.test")
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
            };
            context.Request.Method = method;
            context.Request.Scheme = isHttps ? "https" : "http";
            context.Request.Host = new HostString(host);
            context.Connection.RemoteIpAddress =
                remoteIpAddress ?? IPAddress.Parse("198.51.100.10");
            int queryIndex = path.IndexOf('?', StringComparison.Ordinal);
            context.Request.Path = queryIndex < 0 ? path : path[..queryIndex];
            context.Request.QueryString = queryIndex < 0
                ? QueryString.Empty
                : new QueryString(path[queryIndex..]);
            var responseStream = new MemoryStream();
            context.Response.Body = useNonSeekableResponseBody
                ? new NonSeekableWriteStream(responseStream)
                : responseStream;
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
            responseStream.Position = 0;
            string responseBody = await new StreamReader(
                    responseStream,
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
            ["Security:Hosts:AllowedHosts:0"] = "vistara.example.test",
            ["Security:Limits:MaxRequestBodyBytes"] = "52428800",
            ["Security:Limits:RequestsPerWindow"] = "100",
        };

        private sealed class NonSeekableWriteStream(Stream inner) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) =>
                inner.FlushAsync(cancellationToken);

            public override void Write(
                byte[] buffer,
                int offset,
                int count) =>
                inner.Write(buffer, offset, count);

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default) =>
                inner.WriteAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }

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
