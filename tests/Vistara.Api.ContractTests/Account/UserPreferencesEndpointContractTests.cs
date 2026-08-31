using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class UserPreferencesEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");

    [Fact]
    public async Task Reading_publishes_the_document_and_its_entity_tag()
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(HttpMethods.Get, preferences);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v3\"", response.ETag);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal(UserId, preferences.ReadUserId);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("comfortable", json.RootElement.GetProperty("density").GetString());
        Assert.False(json.RootElement.GetProperty("reducedMotion").GetBoolean());
        Assert.False(
            json.RootElement.GetProperty("screenReaderPagedMode").GetBoolean());
        Assert.Equal("en-US", json.RootElement.GetProperty("locale").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Patching_applies_a_merge_patch_and_returns_the_new_version()
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(
            HttpMethods.Patch,
            preferences,
            body: """{"density":"compact","timeZone":null}""",
            ifMatch: "\"v3\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v4\"", response.ETag);
        Assert.Equal(3, preferences.ExpectedVersion);
        Assert.NotNull(preferences.Patch);
        Assert.Equal("compact", preferences.Patch.Density);
        Assert.Null(preferences.Patch.ReducedMotion);
        Assert.False(preferences.Patch.Locale.IsPresent);
        Assert.True(preferences.Patch.TimeZone.IsPresent);
        Assert.Null(preferences.Patch.TimeZone.Value);
    }

    [Fact]
    public async Task Patching_without_a_precondition_is_rejected()
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(
            HttpMethods.Patch,
            preferences,
            body: """{"density":"compact"}""");

        Assert.Equal(
            HttpStatusCode.PreconditionRequired,
            response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "preferences_if_match_required",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Null(preferences.Patch);
    }

    [Theory]
    [InlineData("not-a-tag")]
    [InlineData("\"3\"")]
    [InlineData("\"v3\", \"v4\"")]
    public async Task Patching_with_a_malformed_precondition_is_rejected(string ifMatch)
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(
            HttpMethods.Patch,
            preferences,
            body: """{"density":"compact"}""",
            ifMatch: ifMatch);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(preferences.Patch);
    }

    [Fact]
    public async Task A_stale_precondition_answers_precondition_failed()
    {
        var preferences = new FakeUserPreferencesPort
        {
            Result = Result.Failure<UserPreferencesView>(ResultError.Conflict(
                "preferences.version_conflict",
                "The preference document changed since it was read.")),
        };

        TestResponse response = await SendAsync(
            HttpMethods.Patch,
            preferences,
            body: """{"density":"compact"}""",
            ifMatch: "\"v1\"");

        Assert.Equal(
            HttpStatusCode.PreconditionFailed,
            response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "preferences_stale_version",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"density":42}""", "density")]
    [InlineData("""{"reducedMotion":"yes"}""", "reducedMotion")]
    [InlineData("""{"screenReaderPagedMode":1}""", "screenReaderPagedMode")]
    [InlineData("""{"locale":7}""", "locale")]
    [InlineData("""{"timeZone":false}""", "timeZone")]
    [InlineData("""[]""", "body")]
    public async Task Patching_rejects_wrongly_typed_members(string body, string field)
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(
            HttpMethods.Patch,
            preferences,
            body: body,
            ifMatch: "\"v3\"");

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.True(
            problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Null(preferences.Patch);
    }

    [Fact]
    public async Task Patching_rejects_a_malformed_body()
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(
            HttpMethods.Patch,
            preferences,
            body: "{not json",
            ifMatch: "\"v3\"");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(preferences.Patch);
    }

    [Fact]
    public async Task A_wildcard_precondition_uses_the_current_version()
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(
            HttpMethods.Patch,
            preferences,
            body: """{"density":"compact"}""",
            ifMatch: "*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, preferences.ExpectedVersion);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var preferences = new FakeUserPreferencesPort();

        TestResponse response = await SendAsync(
            HttpMethods.Get,
            preferences,
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(preferences.ReadUserId);
    }

    [Fact]
    public async Task Preferences_follow_the_principal_not_the_tenant()
    {
        var preferences = new FakeUserPreferencesPort();

        await SendAsync(
            HttpMethods.Get,
            preferences,
            principal: new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", Guid.CreateVersion7().ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new Claim(ClaimTypes.Role, "Viewer"),
                    new Claim("vistara_auth_kind", "Cookie"),
                ],
                "test")));

        Assert.Equal(UserId, preferences.ReadUserId);
    }

    private static async Task<TestResponse> SendAsync(
        string method,
        IUserPreferencesPort preferences,
        string? body = null,
        string? ifMatch = null,
        ClaimsPrincipal? principal = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(preferences);
        builder.Services.AddSingleton<IBrowserSessionPort>(new UnusedSessionPort());
        builder.Services.AddSingleton<IFirstOwnerProvisioningPort>(
            new UnusedProvisioningPort());
        builder.Services.AddVistaraAccountSurface();
        WebApplication app = builder.Build();
        app.MapVistaraAccount();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/me/preferences" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains(method));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", TenantId.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new Claim(ClaimTypes.Role, "TenantOwner"),
                    new Claim("vistara_auth_kind", "Cookie"),
                ],
                "test")),
        };
        context.Request.Method = method;
        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
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
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.ETag.ToString(),
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string CacheControl,
        string ETag,
        string Body);

    private sealed class FakeUserPreferencesPort : IUserPreferencesPort
    {
        public Guid? ReadUserId { get; private set; }

        public UserPreferencesPatch? Patch { get; private set; }

        public long? ExpectedVersion { get; private set; }

        public Result<UserPreferencesView>? Result { get; init; }

        public ValueTask<UserPreferencesView> GetAsync(
            Guid userId,
            CancellationToken cancellationToken)
        {
            ReadUserId = userId;
            return ValueTask.FromResult(
                new UserPreferencesView("comfortable", false, false, "en-US", null, 3));
        }

        public ValueTask<Result<UserPreferencesView>> UpdateAsync(
            Guid userId,
            UserPreferencesPatch patch,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            Patch = patch;
            ExpectedVersion = expectedVersion;
            return ValueTask.FromResult(Result ?? Domain.Common.Result.Success(
                new UserPreferencesView(
                    patch.Density ?? "comfortable",
                    patch.ReducedMotion ?? false,
                    patch.ScreenReaderPagedMode ?? false,
                    patch.Locale.IsPresent ? patch.Locale.Value : "en-US",
                    patch.TimeZone.IsPresent ? patch.TimeZone.Value : null,
                    expectedVersion + 1)));
        }
    }

    private sealed class UnusedSessionPort : IBrowserSessionPort
    {
        public ValueTask<Result<BrowserSessionResult>> LoginAsync(
            BrowserLoginCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> IssueAntiforgeryTokenAsync(
            string? sessionToken,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string> LogoutAsync(
            string? sessionToken,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<CurrentUserView>> DescribeAsync(
            Guid tenantId,
            Guid userId,
            bool includeOtherTenants,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedProvisioningPort : IFirstOwnerProvisioningPort
    {
        public ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
            FirstOwnerProvisioningCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
