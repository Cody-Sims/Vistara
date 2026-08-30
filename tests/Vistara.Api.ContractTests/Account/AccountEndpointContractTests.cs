using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class AccountEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");

    private const string SessionCookie = "__Host-vistara-session";

    [Fact]
    public void Mapping_exposes_anonymous_bootstrap_routes_and_a_guarded_profile()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraAccountSurface();
        WebApplication app = builder.Build();

        app.MapVistaraAccount();

        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(4, endpoints.Length);
        foreach (string route in new[]
                 {
                     "/api/v1/auth/login",
                     "/api/v1/auth/logout",
                     "/api/v1/setup",
                 })
        {
            RouteEndpoint endpoint = Assert.Single(
                endpoints,
                candidate => candidate.RoutePattern.RawText == route);
            Assert.Contains(
                endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>(),
                metadata => metadata is not null);
            Assert.Contains(
                "POST",
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        }

        RouteEndpoint me = Assert.Single(
            endpoints,
            candidate => candidate.RoutePattern.RawText == "/api/v1/me");
        Assert.Equal(
            AccountEndpointMapping.PolicyName,
            Assert.Single(me.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
    }

    [Fact]
    public async Task Login_sets_the_session_cookie_and_returns_the_csrf_token()
    {
        var sessions = new FakeBrowserSessionPort();

        TestResponse response = await SendAsync(
            "login",
            sessions,
            body: """{"login":"owner@example.com","password":"correct-horse-battery"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Contains(
            $"{SessionCookie}=token",
            response.SetCookie,
            StringComparison.Ordinal);
        Assert.Contains("HttpOnly", response.SetCookie, StringComparison.Ordinal);
        Assert.Contains("Secure", response.SetCookie, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("csrf-token", json.RootElement.GetProperty("csrfToken").GetString());
        JsonElement user = json.RootElement.GetProperty("user");
        Assert.Equal(UserId, user.GetProperty("userId").GetGuid());
        Assert.Equal(TenantId, user.GetProperty("tenantId").GetGuid());
        Assert.Equal("X-Vistara-CSRF", user.GetProperty("csrfHeaderName").GetString());
        Assert.DoesNotContain(
            "correct-horse-battery",
            response.Body,
            StringComparison.Ordinal);
        Assert.NotNull(sessions.LoginCommand);
        Assert.Equal("owner@example.com", sessions.LoginCommand.Login);
    }

    [Fact]
    public async Task Login_forwards_an_existing_session_cookie_for_rotation()
    {
        var sessions = new FakeBrowserSessionPort();

        await SendAsync(
            "login",
            sessions,
            body: """{"login":"owner@example.com","password":"correct-horse-battery"}""",
            configure: context =>
                context.Request.Headers.Cookie = $"{SessionCookie}=previous");

        Assert.Equal("previous", sessions.LoginCommand!.ExistingSessionToken);
    }

    [Theory]
    [InlineData("""{"login":"","password":"x"}""")]
    [InlineData("""{"login":"owner@example.com"}""")]
    [InlineData("""{}""")]
    public async Task Login_rejects_missing_credentials_without_probing_storage(string body)
    {
        var sessions = new FakeBrowserSessionPort();

        TestResponse response = await SendAsync("login", sessions, body: body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "auth_invalid_credentials",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Null(sessions.LoginCommand);
    }

    [Fact]
    public async Task Login_reports_a_generic_failure_for_rejected_credentials()
    {
        var sessions = new FakeBrowserSessionPort
        {
            LoginResult = Result.Failure<BrowserSessionResult>(
                CookieAuthErrors.InvalidCredentials),
        };

        TestResponse response = await SendAsync(
            "login",
            sessions,
            body: """{"login":"owner@example.com","password":"wrong-password"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "auth_invalid_credentials",
            problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("owner@example.com", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_reports_a_tenant_that_is_unavailable()
    {
        var sessions = new FakeBrowserSessionPort
        {
            LoginResult = Result.Failure<BrowserSessionResult>(
                CookieAuthErrors.TenantUnavailable),
        };

        TestResponse response = await SendAsync(
            "login",
            sessions,
            body: """{"login":"owner@example.com","password":"correct-horse-battery"}""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Logout_always_clears_the_cookie_and_is_idempotent()
    {
        var sessions = new FakeBrowserSessionPort();

        TestResponse first = await SendAsync(
            "logout",
            sessions,
            configure: context =>
                context.Request.Headers.Cookie = $"{SessionCookie}=token");
        TestResponse second = await SendAsync("logout", sessions);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Contains("Max-Age=0", first.SetCookie, StringComparison.Ordinal);
        Assert.Contains("Max-Age=0", second.SetCookie, StringComparison.Ordinal);
        Assert.Equal("token", sessions.FirstLogoutToken);
        Assert.Null(sessions.LastLogoutToken);
    }

    [Fact]
    public async Task Current_user_returns_the_principal_and_memberships()
    {
        var sessions = new FakeBrowserSessionPort();

        TestResponse response = await SendAsync("me", sessions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal(TenantId, sessions.DescribedTenantId);
        Assert.Equal(UserId, sessions.DescribedUserId);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("owner@example.com", json.RootElement.GetProperty("email").GetString());
        Assert.Equal("TenantOwner", json.RootElement.GetProperty("role").GetString());
        JsonElement tenant = Assert.Single(
            json.RootElement.GetProperty("tenants").EnumerateArray().ToArray());
        Assert.Equal(TenantId, tenant.GetProperty("id").GetGuid());
        Assert.Equal(
            "X-Vistara-CSRF",
            json.RootElement.GetProperty("csrfHeaderName").GetString());
    }

    [Fact]
    public async Task Current_user_requires_authentication()
    {
        var sessions = new FakeBrowserSessionPort();

        TestResponse response = await SendAsync(
            "me",
            sessions,
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(sessions.DescribedUserId);
    }

    [Fact]
    public async Task Current_user_never_trusts_a_tenant_header()
    {
        var sessions = new FakeBrowserSessionPort();

        await SendAsync(
            "me",
            sessions,
            configure: context => context.Request.Headers["X-Tenant-Id"] =
                Guid.CreateVersion7().ToString("D"));

        Assert.Equal(TenantId, sessions.DescribedTenantId);
    }

    private static async Task<TestResponse> SendAsync(
        string operation,
        IBrowserSessionPort sessions,
        string? body = null,
        ClaimsPrincipal? principal = null,
        Action<HttpContext>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<IFirstOwnerProvisioningPort>(
            new UnusedProvisioningPort());
        builder.Services.AddVistaraAccountSurface();
        WebApplication app = builder.Build();
        app.MapVistaraAccount();

        string route = operation switch
        {
            "login" => "/api/v1/auth/login",
            "logout" => "/api/v1/auth/logout",
            "me" => "/api/v1/me",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", TenantId.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new Claim(ClaimTypes.Role, "TenantOwner"),
                ],
                "test")),
        };
        context.Request.Method = operation == "me" ? HttpMethods.Get : HttpMethods.Post;
        if (body is not null)
        {
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentType = "application/json";
        }

        context.Response.Body = new MemoryStream();
        configure?.Invoke(context);

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.SetCookie.ToString(),
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string CacheControl,
        string SetCookie,
        string Body);

    private sealed class UnusedProvisioningPort : IFirstOwnerProvisioningPort
    {
        public ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
            FirstOwnerProvisioningCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeBrowserSessionPort : IBrowserSessionPort
    {
        private int _logouts;

        public BrowserLoginCommand? LoginCommand { get; private set; }

        public Guid? DescribedTenantId { get; private set; }

        public Guid? DescribedUserId { get; private set; }

        public string? FirstLogoutToken { get; private set; }

        public string? LastLogoutToken { get; private set; }

        public Result<BrowserSessionResult>? LoginResult { get; init; }

        public ValueTask<Result<BrowserSessionResult>> LoginAsync(
            BrowserLoginCommand command,
            CancellationToken cancellationToken)
        {
            LoginCommand = command;
            return ValueTask.FromResult(LoginResult ?? Result.Success(
                new BrowserSessionResult(
                    View(),
                    $"{SessionCookie}=token; Path=/; Max-Age=1800; Secure; HttpOnly; SameSite=Lax",
                    "csrf-token")));
        }

        public ValueTask<string> LogoutAsync(
            string? sessionToken,
            CancellationToken cancellationToken)
        {
            if (_logouts == 0)
            {
                FirstLogoutToken = sessionToken;
            }

            _logouts++;
            LastLogoutToken = sessionToken;
            return ValueTask.FromResult(
                $"{SessionCookie}=; Path=/; Max-Age=0; Secure; HttpOnly; SameSite=Lax");
        }

        public ValueTask<Result<CurrentUserView>> DescribeAsync(
            Guid tenantId,
            Guid userId,
            CancellationToken cancellationToken)
        {
            DescribedTenantId = tenantId;
            DescribedUserId = userId;
            return ValueTask.FromResult(Result.Success(View()));
        }

        private static CurrentUserView View() =>
            new(
                UserId,
                "owner@example.com",
                "Owner",
                TenantId,
                "TenantOwner",
                [
                    new CurrentUserTenantView(
                        TenantId,
                        "acme",
                        "Acme",
                        "TenantOwner",
                        "Active"),
                ]);
    }
}
