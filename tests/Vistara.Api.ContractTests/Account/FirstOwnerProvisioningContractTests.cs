using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class FirstOwnerProvisioningContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");

    private const string ValidBody =
        """
        {"tenantSlug":"acme","tenantName":"Acme","email":"owner@example.com",
         "displayName":"Owner","password":"correct-horse-battery"}
        """;

    [Fact]
    public async Task Provisioning_creates_the_first_owner_and_never_echoes_the_password()
    {
        var provisioning = new FakeProvisioningPort();

        TestResponse response = await SendAsync(provisioning, ValidBody);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal("/api/v1/me", response.Location);
        Assert.NotNull(provisioning.Command);
        Assert.Equal("acme", provisioning.Command.TenantSlug);
        Assert.Equal("correct-horse-battery", provisioning.Command.Password);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal(TenantId, json.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(UserId, json.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal("TenantOwner", json.RootElement.GetProperty("role").GetString());
        Assert.DoesNotContain(
            "correct-horse-battery",
            response.Body,
            StringComparison.Ordinal);
        Assert.False(json.RootElement.TryGetProperty("password", out _));
    }

    [Theory]
    [InlineData("""{"tenantName":"Acme","email":"a@b.com","displayName":"O","password":"correct-horse-battery"}""", "tenantSlug")]
    [InlineData("""{"tenantSlug":"acme","email":"a@b.com","displayName":"O","password":"correct-horse-battery"}""", "tenantName")]
    [InlineData("""{"tenantSlug":"acme","tenantName":"Acme","displayName":"O","password":"correct-horse-battery"}""", "email")]
    [InlineData("""{"tenantSlug":"acme","tenantName":"Acme","email":"a@b.com","password":"correct-horse-battery"}""", "displayName")]
    [InlineData("""{"tenantSlug":"acme","tenantName":"Acme","email":"a@b.com","displayName":"O"}""", "password")]
    public async Task Provisioning_rejects_incomplete_requests(string body, string field)
    {
        var provisioning = new FakeProvisioningPort();

        TestResponse response = await SendAsync(provisioning, body);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.True(
            problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Null(provisioning.Command);
    }

    [Fact]
    public async Task Provisioning_is_refused_once_an_owner_exists()
    {
        var provisioning = new FakeProvisioningPort
        {
            Result = Result.Failure<ProvisionedOwnerView>(ResultError.Conflict(
                "setup.already_provisioned",
                "The platform already has an owner and cannot be provisioned again.")),
        };

        TestResponse response = await SendAsync(provisioning, ValidBody);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "setup_already_provisioned",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Provisioning_reports_a_weak_password_without_leaking_it()
    {
        var provisioning = new FakeProvisioningPort
        {
            Result = Result.Failure<ProvisionedOwnerView>(ResultError.Validation(
                "setup.weak_password",
                "The owner password must contain at least 12 characters.")),
        };

        TestResponse response = await SendAsync(
            provisioning,
            """{"tenantSlug":"acme","tenantName":"Acme","email":"a@b.com","displayName":"O","password":"short"}""");

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.DoesNotContain("short", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provisioning_rejects_a_malformed_body()
    {
        var provisioning = new FakeProvisioningPort();

        TestResponse response = await SendAsync(provisioning, "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(provisioning.Command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setup_availability_is_readable_without_any_credential(
        bool available)
    {
        var provisioning = new FakeProvisioningPort { Available = available };

        TestResponse response = await DescribeAsync(provisioning);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal(
            ["available", "signInProviders"],
            json.RootElement.EnumerateObject()
                .Select(member => member.Name)
                .ToArray());
        Assert.Equal(available, json.RootElement.GetProperty("available").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("signInProviders").EnumerateArray());
    }

    /// <summary>
    /// The hosted providers are published anonymously, so the payload must
    /// stay limited to what a sign-in button needs. A directory tenant, a
    /// client identifier, an authority, or a bootstrap allowlist appearing
    /// here would be a disclosure, not a feature.
    /// </summary>
    [Fact]
    public async Task Setup_availability_publishes_only_the_sign_in_button_fields()
    {
        var provisioning = new FakeProvisioningPort();

        TestResponse response = await DescribeAsync(
            provisioning,
            catalog: new StubOidcProviderCatalog(
                new OidcProviderCapability(
                    "entra",
                    "Microsoft Entra ID",
                    "/api/v1/auth/oidc/entra/start")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement provider = Assert.Single(
            json.RootElement.GetProperty("signInProviders").EnumerateArray());
        Assert.Equal(
            ["id", "displayName", "startUrl"],
            provider.EnumerateObject().Select(member => member.Name).ToArray());
        Assert.Equal("entra", provider.GetProperty("id").GetString());
        Assert.Equal("Microsoft Entra ID", provider.GetProperty("displayName").GetString());
        Assert.Equal(
            "/api/v1/auth/oidc/entra/start",
            provider.GetProperty("startUrl").GetString());
    }

    [Fact]
    public async Task Setup_availability_is_rate_limited_before_it_reads_state()
    {
        var provisioning = new FakeProvisioningPort();

        TestResponse response = await DescribeAsync(
            provisioning,
            new DenyingRateLimitHook());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("30", response.RetryAfter);
        Assert.False(provisioning.AvailabilityWasRead);
    }

    private static async Task<TestResponse> DescribeAsync(
        IFirstOwnerProvisioningPort provisioning,
        IPlatformRateLimitHook? rateLimit = null,
        IOidcProviderCatalog? catalog = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(provisioning);
        if (rateLimit is not null)
        {
            builder.Services.AddSingleton(rateLimit);
        }

        if (catalog is not null)
        {
            builder.Services.AddSingleton(catalog);
        }

        builder.Services.AddVistaraAccountSurface();
        WebApplication app = builder.Build();
        app.MapVistaraAccount();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/setup" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("GET"));
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.Location.ToString(),
            responseBody,
            context.Response.Headers.RetryAfter.ToString());
    }

    private static async Task<TestResponse> SendAsync(
        IFirstOwnerProvisioningPort provisioning,
        string body)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(provisioning);
        builder.Services.AddVistaraAccountSurface();
        WebApplication app = builder.Build();
        app.MapVistaraAccount();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/setup" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("POST"));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.Location.ToString(),
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string CacheControl,
        string Location,
        string Body,
        string RetryAfter = "");

    private sealed class DenyingRateLimitHook : IPlatformRateLimitHook
    {
        public ValueTask<PlatformRateLimitDecision> CheckAsync(
            HttpContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                PlatformRateLimitDecision.Reject(TimeSpan.FromSeconds(30)));
    }

    private sealed class StubOidcProviderCatalog(params OidcProviderCapability[] providers) :
        IOidcProviderCatalog
    {
        public IReadOnlyList<OidcProviderCapability> Providers { get; } = providers;
    }

    private sealed class FakeProvisioningPort : IFirstOwnerProvisioningPort
    {
        public FirstOwnerProvisioningCommand? Command { get; private set; }

        public Result<ProvisionedOwnerView>? Result { get; init; }

        public bool Available { get; init; } = true;

        public bool AvailabilityWasRead { get; private set; }

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            AvailabilityWasRead = true;
            return ValueTask.FromResult(Available);
        }

        public ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
            FirstOwnerProvisioningCommand command,
            CancellationToken cancellationToken)
        {
            Command = command;
            return ValueTask.FromResult(
                Result ?? Domain.Common.Result.Success(new ProvisionedOwnerView(
                    TenantId,
                    command.TenantSlug,
                    command.TenantName,
                    UserId,
                    command.Email,
                    command.DisplayName,
                    "TenantOwner")));
        }
    }
}
