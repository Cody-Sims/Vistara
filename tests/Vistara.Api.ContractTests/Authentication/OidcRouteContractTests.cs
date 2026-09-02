using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Oidc;
using Vistara.Auth.Cookies;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.Authentication;

/// <summary>
/// Pins the hosted OpenID Connect browser surface.
///
/// The reply URLs are registered with Entra by the deployment template, so
/// they are asserted against the same frozen fixture the template and the
/// deployment verification read. Everything else here is about the property
/// that makes four anonymous GET routes safe on an otherwise authenticated
/// API: the anonymous decision is taken on the method and the path together,
/// and nothing about a failure is visible to the browser.
/// </summary>
public sealed class OidcRouteContractTests
{
    [Fact]
    public void Routes_match_the_registered_entra_reply_urls()
    {
        JsonElement routes = LoadRouteFixture().GetProperty("routes");

        Assert.Equal(
            "entra",
            LoadRouteFixture().GetProperty("providerId").GetString());
        AssertFixtureRoute(routes, "callback", OidcRoutes.CallbackPath);
        AssertFixtureRoute(routes, "signedOut", OidcRoutes.SignedOutPath);

        // The front-channel path is still served for compatibility, so it is
        // asserted while the fixture describes it. It must stop being
        // registered as the Entra web.logoutUrl - the endpoint cannot revoke
        // anything, and registering a control that cannot act is worse than
        // registering none - so HB-09 removing this entry is expected and must
        // not fail the API contract.
        if (routes.TryGetProperty("frontChannelLogout", out JsonElement frontChannel))
        {
            Assert.Equal(
                OidcRoutes.FrontChannelLogoutPath,
                frontChannel.GetProperty("path").GetString());
            Assert.Equal("GET", frontChannel.GetProperty("method").GetString());
        }
    }

    /// <summary>
    /// The provider drives three of these routes and an anonymous visitor the
    /// fourth, so each must be mapped for exactly one method and must carry
    /// <see cref="IAllowAnonymous"/>. A route that also answered another method
    /// would widen the anonymous surface without changing the allowlist.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/auth/oidc/{providerId:vistaraOidcProviderKey}/start")]
    [InlineData("/api/v1/auth/oidc/entra/callback")]
    [InlineData("/api/v1/auth/oidc/entra/frontchannel-logout")]
    [InlineData("/api/v1/auth/oidc/entra/signed-out")]
    public void Every_hosted_route_is_an_anonymous_get(string pattern)
    {
        RouteEndpoint endpoint = Assert.Single(
            MapRoutes(),
            candidate => candidate.RoutePattern.RawText == pattern);

        Assert.Equal(
            ["GET"],
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    /// <summary>
    /// Relying-party initiated sign-out is the opposite of the reply URLs: it
    /// revokes, so it is a POST that must stay authenticated and antiforgery
    /// covered rather than joining the anonymous set.
    /// </summary>
    [Fact]
    public void Relying_party_sign_out_is_an_authenticated_post()
    {
        RouteEndpoint endpoint = Assert.Single(
            MapRoutes(),
            candidate => candidate.RoutePattern.RawText ==
                "/api/v1/auth/oidc/{providerId:vistaraOidcProviderKey}/sign-out");

        Assert.Equal(
            ["POST"],
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Equal(
            PlatformAuthenticationDefaults.CookieScheme,
            PlatformAuthenticationSelector.Select(
                RequestWithStaleSession("POST", "/api/v1/auth/oidc/entra/sign-out")));
    }

    /// <summary>
    /// The provider segment is judged by routing, so an attacker-chosen
    /// segment never reaches a handler, the provider registry, or the audit
    /// sink. The constraint is the shipped predicate rather than a second
    /// spelling of it.
    /// </summary>
    [Theory]
    [InlineData("entra", true)]
    [InlineData("Entra-2_0", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("has%2Fslash", false)]
    [InlineData("carriage\rreturn", false)]
    [InlineData("line\nfeed", false)]
    [InlineData("null\0byte", false)]
    [InlineData("semi;colon", false)]
    [InlineData("dot.dot", false)]
    public void The_provider_constraint_admits_exactly_the_provider_keys(
        string candidate,
        bool admitted)
    {
        var constraint = new OidcProviderKeyRouteConstraint();

        bool matched = constraint.Match(
            new DefaultHttpContext(),
            null,
            OidcRoutes.ProviderRouteParameter,
            new RouteValueDictionary
            {
                [OidcRoutes.ProviderRouteParameter] = candidate,
            },
            RouteDirection.IncomingRequest);

        Assert.Equal(admitted, matched);
        Assert.Equal(admitted, OidcRoutes.IsProviderKey(candidate));
    }

    [Fact]
    public void An_oversized_provider_segment_is_refused_by_the_constraint()
    {
        var constraint = new OidcProviderKeyRouteConstraint();

        Assert.False(constraint.Match(
            new DefaultHttpContext(),
            null,
            OidcRoutes.ProviderRouteParameter,
            new RouteValueDictionary
            {
                [OidcRoutes.ProviderRouteParameter] = new string('a', 2048),
            },
            RouteDirection.IncomingRequest));
    }

    /// <summary>
    /// Mapping without the constraint registered would leave the provider
    /// segment unbounded, so it is a startup failure rather than a silently
    /// wider route.
    /// </summary>
    [Fact]
    public void Mapping_without_the_constraint_registered_fails()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IOidcProviderCatalog>(new StubOidcProviderCatalog());
        WebApplication app = builder.Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => app.MapVistaraOidcAuthentication());

        Assert.Contains(
            OidcRoutes.ProviderKeyConstraintName,
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A segment that is not a provider key is answered with a bare 404 and no
    /// audit record. The sign-in port is never asked, so nothing can record
    /// what was attempted.
    /// </summary>
    [Fact]
    public async Task A_segment_that_is_not_a_provider_key_is_a_bare_not_found()
    {
        var login = new StubOidcLoginPort();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await OidcAuthenticationEndpoint.StartAsync(
            context,
            login,
            "../../etc/passwd",
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, context.Response.ContentLength);
        Assert.Equal(string.Empty, context.Response.Headers.Location.ToString());
        Assert.False(login.WasStarted);
    }

    /// <summary>
    /// Audit records are operator-facing text, so a provider key that is not
    /// one is replaced with a fixed token at construction. A caller cannot put
    /// attacker-chosen bytes into a log line by forgetting to check first.
    /// </summary>
    [Theory]
    [InlineData("entra", "entra")]
    [InlineData("", OidcRoutes.UnknownProviderAuditToken)]
    [InlineData("evil\r\nInjected: header", OidcRoutes.UnknownProviderAuditToken)]
    [InlineData("../../etc/passwd", OidcRoutes.UnknownProviderAuditToken)]
    public void An_audit_event_never_carries_a_raw_route_segment(
        string providerId,
        string recorded)
    {
        Assert.Equal(
            recorded,
            new OidcAuditEvent(providerId, "start", "provider_not_configured").ProviderId);
    }

    /// <summary>
    /// A deployment with no configured provider has no reply URL registered
    /// with anyone and no visitor who can start a sign-in, so it maps nothing.
    /// </summary>
    [Fact]
    public void An_unconfigured_deployment_maps_no_hosted_route()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IOidcProviderCatalog, EmptyOidcProviderCatalog>();
        builder.Services.AddVistaraOidcRouting();
        WebApplication app = builder.Build();

        app.MapVistaraOidcAuthentication();

        Assert.Empty(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(
                source => source.Endpoints));
    }

    [Fact]
    public void The_hosted_surface_maps_no_other_route()
    {
        string[] patterns = MapRoutes()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Where(pattern => pattern.StartsWith(
                OidcRoutes.Prefix,
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(HostedRoutePatterns, patterns);
    }

    /// <summary>
    /// The stale cookie is the whole point: a browser arriving from the
    /// provider routinely carries a revoked or wrong-tenant session, and if it
    /// selected the cookie scheme a normal sign-in would be challenged.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/v1/auth/oidc/entra/start")]
    [InlineData("GET", "/api/v1/auth/oidc/entra/callback")]
    [InlineData("GET", "/api/v1/auth/oidc/entra/frontchannel-logout")]
    [InlineData("GET", "/api/v1/auth/oidc/entra/signed-out")]
    [InlineData("GET", "/API/V1/AUTH/OIDC/ENTRA/CALLBACK")]
    [InlineData("GET", "/api/v1/auth/oidc/some-other-provider/start")]
    public void The_selector_authenticates_the_hosted_routes_anonymously(
        string method,
        string path)
    {
        Assert.Equal(
            PlatformAuthenticationDefaults.AnonymousScheme,
            PlatformAuthenticationSelector.Select(
                RequestWithStaleSession(method, path)));
    }

    /// <summary>
    /// The allowlist entry is a method and a path, not a path. Anything else
    /// under the hosted prefix - another method, a nested provider segment, an
    /// empty one, or a neighbouring route - keeps the cookie scheme.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/v1/auth/oidc/entra/callback")]
    [InlineData("DELETE", "/api/v1/auth/oidc/entra/frontchannel-logout")]
    [InlineData("POST", "/api/v1/auth/oidc/entra/start")]
    [InlineData("GET", "/api/v1/auth/oidc/entra/token")]
    [InlineData("GET", "/api/v1/auth/oidc//start")]
    [InlineData("GET", "/api/v1/auth/oidc/entra/nested/start")]
    [InlineData("GET", "/api/v1/auth/oidc/entra%2Fnested/start")]
    [InlineData("GET", "/api/v1/auth/oidc/start")]
    [InlineData("GET", "/api/v1/auth/oidcentra/start")]
    [InlineData("GET", "/api/v1/auth/login")]
    public void The_selector_keeps_every_other_request_authenticated(
        string method,
        string path)
    {
        Assert.Equal(
            PlatformAuthenticationDefaults.CookieScheme,
            PlatformAuthenticationSelector.Select(
                RequestWithStaleSession(method, path)));
    }

    /// <summary>
    /// A start route whose provider segment is not a bounded provider key is
    /// not the start route. The bound matters because the segment reaches the
    /// login request store and the audit record.
    /// </summary>
    [Fact]
    public void An_oversized_provider_segment_is_not_the_start_route()
    {
        Assert.Equal(
            PlatformAuthenticationDefaults.CookieScheme,
            PlatformAuthenticationSelector.Select(
                RequestWithStaleSession(
                    "GET",
                    $"/api/v1/auth/oidc/{new string('a', 65)}/start")));
    }

    /// <summary>
    /// Start issues the transient handle and sends the browser to the
    /// provider. The handle must be a host cookie so a neighbouring subdomain
    /// cannot plant one, and SameSite must be Lax because the callback is a
    /// top-level cross-site navigation that a stricter value would drop.
    /// </summary>
    [Fact]
    public async Task Start_redirects_with_a_secure_host_only_handle_cookie()
    {
        var login = new StubOidcLoginPort
        {
            Start = Result.Success(new OidcStartResult(
                new Uri("https://login.microsoftonline.com/authorize?state=abc"),
                OidcHandleCookie.ToSetCookieHeader(
                    "protected-handle",
                    TimeSpan.FromMinutes(10)))),
        };

        TestResponse response = await SendAsync(
            login,
            "/api/v1/auth/oidc/entra/start",
            "?returnTo=/gallery");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "https://login.microsoftonline.com/authorize?state=abc",
            response.Location);
        Assert.Equal("no-store", response.CacheControl);
        string handle = Assert.Single(response.SetCookies);
        Assert.StartsWith("__Host-vistara-oidc=protected-handle;", handle, StringComparison.Ordinal);
        Assert.Contains("Path=/", handle, StringComparison.Ordinal);
        Assert.Contains("Max-Age=600", handle, StringComparison.Ordinal);
        Assert.Contains("Secure", handle, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", handle, StringComparison.Ordinal);
        Assert.Contains("SameSite=Lax", handle, StringComparison.Ordinal);
        Assert.Equal("/gallery", login.ReturnTo);
    }

    /// <summary>
    /// A repeated query parameter is a smuggling attempt, not a value to pick a
    /// winner from, so the endpoint reads nothing rather than guessing.
    /// </summary>
    [Fact]
    public async Task A_repeated_return_target_is_not_read()
    {
        var login = new StubOidcLoginPort
        {
            Start = Result.Success(new OidcStartResult(
                new Uri("https://login.microsoftonline.com/authorize"),
                OidcHandleCookie.ToSetCookieHeader("h", TimeSpan.FromMinutes(10)))),
        };

        _ = await SendAsync(
            login,
            "/api/v1/auth/oidc/entra/start",
            "?returnTo=/gallery&returnTo=https://attacker.example");

        Assert.Null(login.ReturnTo);
    }

    /// <summary>
    /// A cancelled consent, a replayed state, a rejected directory, and an
    /// unreachable provider all have to look identical to a browser, otherwise
    /// the redirect becomes an oracle for which allowlist refused the sign-in.
    /// </summary>
    [Theory]
    [InlineData(nameof(OidcErrors.InvalidState))]
    [InlineData(nameof(OidcErrors.ProviderRejected))]
    [InlineData(nameof(OidcErrors.TenantNotAllowed))]
    [InlineData(nameof(OidcErrors.TokenExchangeFailed))]
    [InlineData(nameof(OidcErrors.MetadataUnavailable))]
    public async Task Every_callback_failure_produces_the_same_redirect(string error)
    {
        var login = new StubOidcLoginPort
        {
            Complete = Result.Failure<OidcSignInResult>(ErrorNamed(error)),
        };

        TestResponse response = await SendAsync(
            login,
            "/api/v1/auth/oidc/entra/callback",
            "?error=access_denied&error_description=user+cancelled");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login?error=oidc_sign_in_failed", response.Location);
        Assert.Equal(string.Empty, response.Body);
        Assert.DoesNotContain("cancelled", response.Body, StringComparison.Ordinal);
        Assert.Contains(
            response.SetCookies,
            cookie => cookie.StartsWith("__Host-vistara-oidc=;", StringComparison.Ordinal));
    }

    /// <summary>
    /// The handle is single use whatever happened, so the callback clears it on
    /// the success path too and never leaves a consumed handle in the browser.
    /// </summary>
    [Fact]
    public async Task A_successful_callback_clears_the_handle_and_issues_the_session()
    {
        var login = new StubOidcLoginPort
        {
            Complete = Result.Success(
                new OidcSignInResult("__Host-vistara-session=token; Path=/", "/gallery")),
        };

        TestResponse response = await SendAsync(
            login,
            "/api/v1/auth/oidc/entra/callback",
            "?code=authorization-code&state=opaque-state");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/gallery", response.Location);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Collection(
            response.SetCookies,
            cookie => Assert.StartsWith(
                "__Host-vistara-oidc=;",
                cookie,
                StringComparison.Ordinal),
            cookie => Assert.Equal("__Host-vistara-session=token; Path=/", cookie));
        Assert.DoesNotContain("authorization-code", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "authorization-code",
            response.Location,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Front-channel sign-out arrives as a cross-site GET inside a provider
    /// iframe, and the Vistara session cookie is SameSite=Lax, so it never
    /// arrives with the request. The endpoint therefore does nothing at all:
    /// no session is touched, no cookie is written, and no audit event claims
    /// a sign-out that did not happen. Anything else would either be a no-op
    /// dressed up as success or an unauthenticated revocation oracle.
    /// </summary>
    [Fact]
    public async Task Front_channel_logout_changes_nothing_and_says_nothing()
    {
        TestResponse response = await SendAsync(
            new StubOidcLoginPort(),
            "/api/v1/auth/oidc/entra/frontchannel-logout",
            "?sid=00000000-0000-0000-0000-000000000000&iss=https%3A%2F%2Fattacker.example");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, response.Body);
        Assert.Equal(string.Empty, response.Location);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Empty(response.SetCookies);
    }

    /// <summary>
    /// Repeating it is a no-op because there was never an operation, and it
    /// reports nothing about whether a session existed.
    /// </summary>
    [Fact]
    public async Task Front_channel_logout_is_identical_however_often_it_is_called()
    {
        TestResponse first = await SendAsync(
            new StubOidcLoginPort(),
            "/api/v1/auth/oidc/entra/frontchannel-logout",
            null);
        TestResponse second = await SendAsync(
            new StubOidcLoginPort(),
            "/api/v1/auth/oidc/entra/frontchannel-logout",
            null);

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(first.SetCookies, second.SetCookies);
    }

    /// <summary>
    /// The signed-out landing route is a registered reply URL, so it has to
    /// exist and answer anonymously without reading anything the provider
    /// appended to the URL.
    /// </summary>
    [Fact]
    public async Task Signed_out_sends_the_visitor_somewhere_safe()
    {
        TestResponse response = await SendAsync(
            new StubOidcLoginPort(),
            "/api/v1/auth/oidc/entra/signed-out",
            "?post_logout_redirect_uri=https://attacker.example");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login", response.Location);
        Assert.DoesNotContain("attacker", response.Location, StringComparison.Ordinal);
    }

    private static ResultError ErrorNamed(string name) => name switch
    {
        nameof(OidcErrors.InvalidState) => OidcErrors.InvalidState,
        nameof(OidcErrors.ProviderRejected) => OidcErrors.ProviderRejected,
        nameof(OidcErrors.TenantNotAllowed) => OidcErrors.TenantNotAllowed,
        nameof(OidcErrors.TokenExchangeFailed) => OidcErrors.TokenExchangeFailed,
        nameof(OidcErrors.MetadataUnavailable) => OidcErrors.MetadataUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static void AssertFixtureRoute(
        JsonElement routes,
        string name,
        string served)
    {
        JsonElement route = routes.GetProperty(name);
        Assert.Equal(served, route.GetProperty("path").GetString());
        Assert.Equal("GET", route.GetProperty("method").GetString());
        Assert.True(route.GetProperty("anonymous").GetBoolean());
    }

    private static JsonElement LoadRouteFixture()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "Vistara.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string path = Path.Combine(
            directory!.FullName,
            "eng",
            "tests",
            "fixtures",
            "azure-graph-registration",
            "hosted-oidc-routes.json");
        Assert.True(File.Exists(path), $"The frozen route fixture is missing at '{path}'.");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static HttpRequest RequestWithStaleSession(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Cookie =
            $"{CookieAuthOptions.ProductionCookieName}=stale-session-token";
        return context.Request;
    }

    private static RouteEndpoint[] MapRoutes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IOidcProviderCatalog>(new StubOidcProviderCatalog());
        builder.Services.AddVistaraOidcRouting();
        WebApplication app = builder.Build();
        app.MapVistaraOidcAuthentication();
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
    }

    private static async Task<TestResponse> SendAsync(
        IOidcLoginPort login,
        string path,
        string? query)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(login);
        builder.Services.AddSingleton(new CookieAuthOptions());
        builder.Services.AddSingleton<IOidcProviderCatalog>(new StubOidcProviderCatalog());
        builder.Services.AddVistaraOidcRouting();
        WebApplication app = builder.Build();
        app.MapVistaraOidcAuthentication();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => Matches(candidate.RoutePattern.RawText!, path));

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("vistara.example.test");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query ?? string.Empty);
        context.Request.RouteValues[OidcRoutes.ProviderRouteParameter] =
            OidcRoutes.EntraProviderId;
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync(
            CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers.Location.ToString(),
            context.Response.Headers.CacheControl.ToString(),
            body,
            [.. context.Response.Headers[HeaderNames.SetCookie]
                .Where(value => value is not null)
                .Select(value => value!)]);
    }

    private static bool Matches(string pattern, string path) =>
        string.Equals(pattern, path, StringComparison.Ordinal) ||
        (pattern == OidcRoutes.StartPathTemplate &&
            path.EndsWith("/start", StringComparison.Ordinal));

    private static readonly string[] HostedRoutePatterns =
    [
        "/api/v1/auth/oidc/entra/callback",
        "/api/v1/auth/oidc/entra/frontchannel-logout",
        "/api/v1/auth/oidc/entra/signed-out",
        "/api/v1/auth/oidc/{providerId:vistaraOidcProviderKey}/sign-out",
        "/api/v1/auth/oidc/{providerId:vistaraOidcProviderKey}/start",
    ];

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string Location,
        string CacheControl,
        string Body,
        IReadOnlyList<string> SetCookies);

    private sealed class StubOidcLoginPort : IOidcLoginPort
    {
        public Result<OidcStartResult> Start { get; init; } =
            Result.Failure<OidcStartResult>(OidcErrors.InvalidRequest);

        public Result<OidcSignInResult> Complete { get; init; } =
            Result.Failure<OidcSignInResult>(OidcErrors.InvalidState);

        public string? ReturnTo { get; private set; }

        public bool WasStarted { get; private set; }

        public ValueTask<Result<OidcStartResult>> StartAsync(
            string providerId,
            string? returnTo,
            CancellationToken cancellationToken)
        {
            WasStarted = true;
            ReturnTo = returnTo;
            return ValueTask.FromResult(Start);
        }

        public ValueTask<Result<OidcSignInResult>> CompleteAsync(
            OidcCallbackCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Complete);
    }

    /// <summary>
    /// The routes only exist for a deployment with a configured provider, so
    /// the catalog is populated the way a hosted deployment populates it.
    /// </summary>
    private sealed class StubOidcProviderCatalog : IOidcProviderCatalog
    {
        public IReadOnlyList<OidcProviderCapability> Providers { get; } =
        [
            new(
                OidcRoutes.EntraProviderId,
                "Microsoft Entra ID",
                OidcRoutes.StartPath(OidcRoutes.EntraProviderId)),
        ];
    }

}
