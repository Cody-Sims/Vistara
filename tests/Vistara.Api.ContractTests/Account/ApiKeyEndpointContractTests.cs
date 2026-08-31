using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.ApiKeys;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class ApiKeyEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009f2");

    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");

    private static readonly Guid KeyId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000c01");

    private static readonly string[] ExpectedCreatedScopes =
        ["assets.read", "assets.upload"];

    [Fact]
    public void Mapping_registers_the_authenticated_versioned_routes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraApiKeyAdministration();
        WebApplication app = builder.Build();

        app.MapVistaraApiKeys();

        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(3, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Equal(
            ApiKeyEndpointMapping.PolicyName,
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy));
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/api-keys" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("GET"));
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/api-keys" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("POST"));
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/api-keys/{keyId:guid}" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("DELETE"));
    }

    [Fact]
    public async Task Listing_returns_tenant_keys_without_any_secret_material()
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(HttpMethods.Get, "list", administration);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal(TenantId, administration.ListedTenantId);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement item = Assert.Single(
            json.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(KeyId, item.GetProperty("id").GetGuid());
        Assert.Equal("vst_v1abc", item.GetProperty("prefix").GetString());
        Assert.Equal("Active", item.GetProperty("status").GetString());
        Assert.False(item.TryGetProperty("secret", out _));
        Assert.False(item.TryGetProperty("digest", out _));
        Assert.DoesNotContain(
            "super-secret",
            response.Body,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Listing_is_scoped_to_the_authenticated_tenant()
    {
        var administration = new FakeApiKeyAdministrationPort();

        await SendAsync(HttpMethods.Get, "list", administration, tenantId: OtherTenantId);

        Assert.Equal(OtherTenantId, administration.ListedTenantId);
    }

    [Fact]
    public async Task Creation_returns_the_secret_exactly_once()
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Post,
            "create",
            administration,
            body: """{"scopes":["assets.upload","assets.read","assets.read"]}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal($"/api/v1/api-keys/{KeyId:D}", response.Location);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "vst_v1abc_super-secret",
            json.RootElement.GetProperty("secret").GetString());
        Assert.Equal(
            KeyId,
            json.RootElement.GetProperty("key").GetProperty("id").GetGuid());
        Assert.NotNull(administration.CreatedCommand);
        Assert.Equal(TenantId, administration.CreatedCommand.TenantId);
        Assert.Equal(UserId, administration.CreatedCommand.OwnerId);
        Assert.Equal(ExpectedCreatedScopes, administration.CreatedCommand.Scopes);
    }

    [Theory]
    [InlineData("""{"scopes":[]}""")]
    [InlineData("""{"scopes":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"scopes":["platform.admin"]}""")]
    [InlineData("""{"scopes":["assets.read","assets.read","a","b","c","d","e","f","g"]}""")]
    public async Task Creation_rejects_unsupported_or_missing_scopes(string body)
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Post,
            "create",
            administration,
            body: body);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "api_keys_invalid_request",
            problem.RootElement.GetProperty("code").GetString());
        Assert.True(
            problem.RootElement.GetProperty("errors").TryGetProperty("scopes", out _));
        Assert.Null(administration.CreatedCommand);
    }

    [Fact]
    public async Task Creation_rejects_a_malformed_body()
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Post,
            "create",
            administration,
            body: "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(administration.CreatedCommand);
    }

    [Fact]
    public async Task Creation_rejects_a_non_utc_expiry()
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Post,
            "create",
            administration,
            body: """{"scopes":["assets.read"],"expiresAt":"2027-01-01T00:00:00+02:00"}""");

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Null(administration.CreatedCommand);
    }

    [Fact]
    public async Task Revocation_returns_no_content_and_records_the_actor()
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Delete,
            "revoke",
            administration,
            keyId: KeyId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, response.Body);
        Assert.Equal(TenantId, administration.RevokedTenantId);
        Assert.Equal(UserId, administration.RevokedActorId);
        Assert.Equal(KeyId, administration.RevokedKeyId);
    }

    [Fact]
    public async Task Revoking_an_unknown_or_cross_tenant_key_is_concealed()
    {
        var administration = new FakeApiKeyAdministrationPort
        {
            RevokeResult = Result.Failure(ResultError.NotFound(
                "api_keys.not_found",
                "The API key was not found.")),
        };

        TestResponse response = await SendAsync(
            HttpMethods.Delete,
            "revoke",
            administration,
            keyId: KeyId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "api_keys_not_found",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Revoking_a_non_uuid7_identifier_is_concealed()
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Delete,
            "revoke",
            administration,
            keyId: Guid.Parse("01990a2a-bc00-4000-8000-000000000c01"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(administration.RevokedKeyId);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("create")]
    [InlineData("revoke")]
    public async Task Anonymous_callers_are_rejected(string operation)
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            operation == "list" ? HttpMethods.Get : HttpMethods.Post,
            operation,
            administration,
            principal: new ClaimsPrincipal(new ClaimsIdentity()),
            keyId: KeyId,
            body: """{"scopes":["assets.read"]}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(administration.ListedTenantId);
        Assert.Null(administration.CreatedCommand);
        Assert.Null(administration.RevokedKeyId);
    }

    [Theory]
    [InlineData("Viewer", "assets.read")]
    [InlineData("Member", "assets.read")]
    [InlineData("TenantAdmin", "assets.read")]
    public async Task Callers_without_the_api_key_scope_and_role_are_forbidden(
        string role,
        string scope)
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Get,
            "list",
            administration,
            principal: new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", TenantId.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new Claim(ClaimTypes.Role, role),
                    new Claim("scope", scope),
                ],
                "test")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(administration.ListedTenantId);
    }

    [Fact]
    public async Task Members_holding_the_scope_but_not_the_role_are_forbidden()
    {
        var administration = new FakeApiKeyAdministrationPort();

        TestResponse response = await SendAsync(
            HttpMethods.Get,
            "list",
            administration,
            principal: new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", TenantId.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new Claim(ClaimTypes.Role, "Member"),
                    new Claim("scope", "api_keys.manage"),
                ],
                "test")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(administration.ListedTenantId);
    }

    private static async Task<TestResponse> SendAsync(
        string method,
        string operation,
        IApiKeyAdministrationPort administration,
        Guid? tenantId = null,
        Guid? keyId = null,
        string? body = null,
        ClaimsPrincipal? principal = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(administration);
        builder.Services.AddVistaraApiKeyAdministration();
        WebApplication app = builder.Build();
        app.MapVistaraApiKeys();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => Matches(candidate, operation));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", (tenantId ?? TenantId).ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new Claim(ClaimTypes.Role, "TenantOwner"),
                    new Claim("scope", "api_keys.manage"),
                    new Claim("vistara_auth_kind", "Cookie"),
                ],
                "test")),
        };
        context.Request.Method = method;
        if (keyId is { } id)
        {
            context.Request.RouteValues["keyId"] = id.ToString("D");
        }

        if (body is not null)
        {
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentType = "application/json";
        }

        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.Location.ToString(),
            responseBody);
    }

    private static bool Matches(RouteEndpoint endpoint, string operation)
    {
        HttpMethodMetadata methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!;
        return operation switch
        {
            "list" => methods.HttpMethods.Contains("GET"),
            "create" => methods.HttpMethods.Contains("POST"),
            "revoke" => methods.HttpMethods.Contains("DELETE"),
            _ => false,
        };
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        string CacheControl,
        string Location,
        string Body);

    private sealed class FakeApiKeyAdministrationPort : IApiKeyAdministrationPort
    {
        public Guid? ListedTenantId { get; private set; }

        public ApiKeyCreateCommand? CreatedCommand { get; private set; }

        public Guid? RevokedTenantId { get; private set; }

        public Guid? RevokedActorId { get; private set; }

        public Guid? RevokedKeyId { get; private set; }

        public Result RevokeResult { get; init; } = Result.Success();

        public ValueTask<IReadOnlyList<ApiKeyView>> ListAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            ListedTenantId = tenantId;
            return ValueTask.FromResult<IReadOnlyList<ApiKeyView>>(
            [
                new ApiKeyView(
                    KeyId,
                    "vst_v1abc",
                    UserId,
                    ["assets.read"],
                    "Active",
                    new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                    null,
                    null,
                    null),
            ]);
        }

        public ValueTask<Result<IssuedApiKeyView>> CreateAsync(
            ApiKeyCreateCommand command,
            CancellationToken cancellationToken)
        {
            CreatedCommand = command;
            return ValueTask.FromResult(Result.Success(new IssuedApiKeyView(
                new ApiKeyView(
                    KeyId,
                    "vst_v1abc",
                    command.OwnerId,
                    command.Scopes,
                    "Active",
                    new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                    command.ExpiresAt,
                    null,
                    null),
                "vst_v1abc_super-secret")));
        }

        public ValueTask<Result> RevokeAsync(
            Guid tenantId,
            Guid actorUserId,
            Guid keyId,
            CancellationToken cancellationToken)
        {
            RevokedTenantId = tenantId;
            RevokedActorId = actorUserId;
            RevokedKeyId = keyId;
            return ValueTask.FromResult(RevokeResult);
        }
    }
}
