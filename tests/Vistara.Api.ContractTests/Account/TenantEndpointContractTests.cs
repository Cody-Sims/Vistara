using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Tenants;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class TenantEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009f2");

    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");

    private static readonly Guid MemberId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000702");

    [Fact]
    public void Mapping_registers_the_authenticated_versioned_routes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraTenantAdministration();
        WebApplication app = builder.Build();

        app.MapVistaraTenants();

        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(4, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Equal(
            TenantEndpointMapping.PolicyName,
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy));
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/tenants");
        Assert.Equal(
            2,
            endpoints.Count(endpoint =>
                endpoint.RoutePattern.RawText ==
                "/api/v1/tenants/{tenantId:guid}/members"));
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/api/v1/tenants/{tenantId:guid}/members/{memberUserId:guid}" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("PATCH"));
    }

    [Fact]
    public async Task Tenant_listing_returns_the_memberships_of_the_authenticated_user()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync("tenants", directory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal(UserId, directory.ListedUserId);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement item = Assert.Single(
            json.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(TenantId, item.GetProperty("id").GetGuid());
        Assert.Equal("acme", item.GetProperty("slug").GetString());
        Assert.Equal("TenantOwner", item.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Tenant_listing_is_unrestricted_for_a_browser_session()
    {
        var directory = new FakeTenantDirectoryPort();

        await SendAsync("tenants", directory);

        Assert.False(directory.WasRestricted);
        Assert.Null(directory.RestrictedTenantId);
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("Bearer")]
    public async Task Tenant_listing_is_pinned_to_the_current_tenant_for_tokens(
        string authenticationKind)
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "tenants",
            directory,
            principal: new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", TenantId.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new Claim(ClaimTypes.Role, "TenantOwner"),
                    new Claim("vistara_auth_kind", authenticationKind),
                ],
                "test")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(directory.WasRestricted);
        Assert.Equal(TenantId, directory.RestrictedTenantId);
    }

    [Fact]
    public async Task Tenant_listing_requires_authentication()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "tenants",
            directory,
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(directory.ListedUserId);
    }

    [Fact]
    public async Task Member_listing_returns_the_tenant_roster()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "members",
            directory,
            routeTenantId: TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId, directory.ListedMembersTenantId);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement item = Assert.Single(
            json.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(MemberId, item.GetProperty("userId").GetGuid());
        Assert.Equal("member@example.com", item.GetProperty("email").GetString());
        Assert.Equal(4, item.GetProperty("version").GetInt64());
        Assert.False(item.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task Member_listing_conceals_another_tenant()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "members",
            directory,
            routeTenantId: OtherTenantId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "tenants_not_found",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Null(directory.ListedMembersTenantId);
    }

    [Fact]
    public async Task Member_listing_requires_the_member_management_scope()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "members",
            directory,
            routeTenantId: TenantId,
            principal: Principal("TenantOwner", "assets.read"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(directory.ListedMembersTenantId);
    }

    [Fact]
    public async Task Inviting_a_member_returns_the_created_membership()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "invite",
            directory,
            routeTenantId: TenantId,
            body: """{"email":"Member@Example.com","role":"Member"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/tenants/{TenantId:D}/members", response.Location);
        Assert.NotNull(directory.Invitation);
        Assert.Equal(TenantId, directory.Invitation.TenantId);
        Assert.Equal(UserId, directory.Invitation.ActorUserId);
        Assert.Equal("Member", directory.Invitation.Role);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("Member", json.RootElement.GetProperty("role").GetString());
        Assert.Equal("Invited", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""{"email":"","role":"Member"}""", "email")]
    [InlineData("""{"role":"Member"}""", "email")]
    [InlineData("""{"email":"a@b.com"}""", "role")]
    [InlineData("""{"email":"a@b.com","role":"PlatformAdmin"}""", "role")]
    [InlineData("""{"email":"a@b.com","role":"member"}""", "role")]
    public async Task Inviting_rejects_invalid_requests(string body, string field)
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "invite",
            directory,
            routeTenantId: TenantId,
            body: body);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.True(
            problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Null(directory.Invitation);
    }

    [Fact]
    public async Task Only_an_owner_may_grant_the_owner_role()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "invite",
            directory,
            routeTenantId: TenantId,
            body: """{"email":"a@b.com","role":"TenantOwner"}""",
            principal: Principal("TenantAdmin", "members.manage"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(directory.Invitation);
    }

    [Fact]
    public async Task Inviting_an_existing_member_is_a_conflict()
    {
        var directory = new FakeTenantDirectoryPort
        {
            InviteResult = Result.Failure<TenantMemberView>(ResultError.Conflict(
                "tenants.member_exists",
                "The user already has a membership in this tenant.")),
        };

        TestResponse response = await SendAsync(
            "invite",
            directory,
            routeTenantId: TenantId,
            body: """{"email":"a@b.com","role":"Member"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "tenants_member_exists",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Inviting_into_another_tenant_is_concealed()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "invite",
            directory,
            routeTenantId: OtherTenantId,
            body: """{"email":"a@b.com","role":"Member"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(directory.Invitation);
    }

    [Fact]
    public async Task Inviting_rejects_a_malformed_body()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "invite",
            directory,
            routeTenantId: TenantId,
            body: "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(directory.Invitation);
    }

    [Fact]
    public async Task Updating_a_member_applies_the_precondition_and_returns_the_new_tag()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "update",
            directory,
            routeTenantId: TenantId,
            body: """{"role":"TenantAdmin","status":"Active"}""",
            memberUserId: MemberId,
            ifMatch: "\"v4\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v5\"", response.ETag);
        Assert.Equal(4, directory.ExpectedVersion);
        Assert.NotNull(directory.MemberUpdate);
        Assert.Equal(TenantId, directory.MemberUpdate.TenantId);
        Assert.Equal(MemberId, directory.MemberUpdate.MemberUserId);
        Assert.Equal(UserId, directory.MemberUpdate.ActorUserId);
        Assert.Equal("TenantAdmin", directory.MemberUpdate.Role);
        Assert.Equal("Active", directory.MemberUpdate.Status);
    }

    [Fact]
    public async Task Updating_a_member_without_a_precondition_is_rejected()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "update",
            directory,
            routeTenantId: TenantId,
            body: """{"status":"Suspended"}""",
            memberUserId: MemberId);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Null(directory.MemberUpdate);
    }

    [Fact]
    public async Task Updating_a_member_with_a_stale_precondition_answers_412()
    {
        var directory = new FakeTenantDirectoryPort
        {
            UpdateResult = Result.Failure<TenantMemberView>(ResultError.Conflict(
                "tenants.member_version_conflict",
                "The membership changed since it was read.")),
        };

        TestResponse response = await SendAsync(
            "update",
            directory,
            routeTenantId: TenantId,
            body: """{"status":"Suspended"}""",
            memberUserId: MemberId,
            ifMatch: "\"v1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Demoting_the_last_owner_is_a_state_conflict()
    {
        var directory = new FakeTenantDirectoryPort
        {
            UpdateResult = Result.Failure<TenantMemberView>(ResultError.Conflict(
                "tenants.last_owner",
                "A tenant must keep at least one active owner.")),
        };

        TestResponse response = await SendAsync(
            "update",
            directory,
            routeTenantId: TenantId,
            body: """{"role":"Member"}""",
            memberUserId: MemberId,
            ifMatch: "\"v4\"");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "tenants_last_owner",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Updating_a_member_of_another_tenant_is_concealed()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "update",
            directory,
            routeTenantId: OtherTenantId,
            body: """{"status":"Suspended"}""",
            memberUserId: MemberId,
            ifMatch: "\"v4\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(directory.MemberUpdate);
    }

    [Fact]
    public async Task Only_an_owner_may_promote_a_member_to_owner()
    {
        var directory = new FakeTenantDirectoryPort();

        TestResponse response = await SendAsync(
            "update",
            directory,
            routeTenantId: TenantId,
            body: """{"role":"TenantOwner"}""",
            principal: Principal("TenantAdmin", "members.manage"),
            memberUserId: MemberId,
            ifMatch: "\"v4\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(directory.MemberUpdate);
    }

    private static ClaimsPrincipal Principal(string role, params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", TenantId.ToString("D")),
            new(ClaimTypes.NameIdentifier, UserId.ToString("D")),
            new(ClaimTypes.Role, role),
            new("vistara_auth_kind", "Cookie"),
        };
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<TestResponse> SendAsync(
        string operation,
        ITenantDirectoryPort directory,
        Guid? routeTenantId = null,
        string? body = null,
        ClaimsPrincipal? principal = null,
        Guid? memberUserId = null,
        string? ifMatch = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(directory);
        builder.Services.AddVistaraTenantAdministration();
        WebApplication app = builder.Build();
        app.MapVistaraTenants();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => Matches(candidate, operation));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal ?? Principal(
                "TenantOwner",
                "members.manage",
                "assets.read"),
        };
        context.Request.Method = operation switch
        {
            "invite" => HttpMethods.Post,
            "update" => HttpMethods.Patch,
            _ => HttpMethods.Get,
        };
        if (memberUserId is { } member)
        {
            context.Request.RouteValues["memberUserId"] = member.ToString("D");
        }

        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }
        if (routeTenantId is { } id)
        {
            context.Request.RouteValues["tenantId"] = id.ToString("D");
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
            context.Response.Headers.ETag.ToString(),
            responseBody);
    }

    private static bool Matches(RouteEndpoint endpoint, string operation)
    {
        HttpMethodMetadata methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!;
        bool members = endpoint.RoutePattern.RawText ==
            "/api/v1/tenants/{tenantId:guid}/members";
        return operation switch
        {
            "tenants" => endpoint.RoutePattern.RawText == "/api/v1/tenants",
            "members" => members && methods.HttpMethods.Contains("GET"),
            "invite" => members && methods.HttpMethods.Contains("POST"),
            "update" => endpoint.RoutePattern.RawText ==
                "/api/v1/tenants/{tenantId:guid}/members/{memberUserId:guid}",
            _ => false,
        };
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        string CacheControl,
        string Location,
        string ETag,
        string Body);

    private sealed class FakeTenantDirectoryPort : ITenantDirectoryPort
    {
        public Guid? ListedUserId { get; private set; }

        public Guid? RestrictedTenantId { get; private set; }

        public bool WasRestricted { get; private set; }

        public Guid? ListedMembersTenantId { get; private set; }

        public TenantMemberInvitation? Invitation { get; private set; }

        public Result<TenantMemberView>? InviteResult { get; init; }

        public ValueTask<IReadOnlyList<TenantMembershipView>> ListTenantsForUserAsync(
            Guid userId,
            Guid? restrictToTenantId,
            CancellationToken cancellationToken)
        {
            ListedUserId = userId;
            RestrictedTenantId = restrictToTenantId;
            WasRestricted = restrictToTenantId is not null;
            return ValueTask.FromResult<IReadOnlyList<TenantMembershipView>>(
            [
                new TenantMembershipView(
                    TenantId,
                    "acme",
                    "Acme",
                    "Active",
                    "TenantOwner",
                    "Active",
                    new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                    2),
            ]);
        }

        public ValueTask<IReadOnlyList<TenantMemberView>> ListMembersAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            ListedMembersTenantId = tenantId;
            return ValueTask.FromResult<IReadOnlyList<TenantMemberView>>(
            [
                new TenantMemberView(
                    MemberId,
                    "member@example.com",
                    "Member",
                    "Member",
                    "Active",
                    new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 30, 12, 5, 0, TimeSpan.Zero),
                    4),
            ]);
        }

        public TenantMemberUpdate? MemberUpdate { get; private set; }

        public long? ExpectedVersion { get; private set; }

        public Result<TenantMemberView>? UpdateResult { get; init; }

        public ValueTask<Result<TenantMemberView>> UpdateMemberAsync(
            TenantMemberUpdate update,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            MemberUpdate = update;
            ExpectedVersion = expectedVersion;
            return ValueTask.FromResult(UpdateResult ?? Result.Success(
                new TenantMemberView(
                    update.MemberUserId,
                    "member@example.com",
                    "Member",
                    update.Role ?? "Member",
                    update.Status ?? "Active",
                    new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 30, 12, 5, 0, TimeSpan.Zero),
                    expectedVersion + 1)));
        }

        public ValueTask<Result<TenantMemberView>> InviteMemberAsync(
            TenantMemberInvitation invitation,
            CancellationToken cancellationToken)
        {
            Invitation = invitation;
            return ValueTask.FromResult(InviteResult ?? Result.Success(
                new TenantMemberView(
                    MemberId,
                    invitation.Email.ToLowerInvariant(),
                    "Member",
                    invitation.Role,
                    "Invited",
                    new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                    null,
                    1)));
        }
    }
}
