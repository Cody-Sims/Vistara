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
using Vistara.Api.Features.Admin;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class AdminEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009f2");

    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");

    [Fact]
    public void Mapping_registers_the_guarded_admin_routes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraAdministration();
        WebApplication app = builder.Build();

        app.MapVistaraAdministration();

        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(4, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Equal(
            AdminEndpointMapping.PolicyName,
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy));
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/admin/storage");
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/admin/audit");
        Assert.Equal(
            2,
            endpoints.Count(endpoint =>
                endpoint.RoutePattern.RawText == "/api/v1/admin/policies"));
    }

    [Fact]
    public async Task Storage_reports_consumption_and_health_without_topology()
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync("storage", admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal(TenantId, admin.StorageTenantId);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement bucket = json.RootElement.GetProperty("buckets")[0];
        Assert.Equal("originals", bucket.GetProperty("id").GetString());
        Assert.Equal("s3", bucket.GetProperty("kind").GetString());
        Assert.Equal("healthy", bucket.GetProperty("status").GetString());
        Assert.Equal(4096, json.RootElement.GetProperty("originalBytes").GetInt64());
        foreach (string secret in new[]
                 {
                     "private-bucket",
                     "storage.internal.example",
                     "/srv/vistara",
                     "AccountKey",
                 })
        {
            Assert.DoesNotContain(
                secret,
                response.Body,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Policies_publish_an_entity_tag()
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync("policies-get", admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v7\"", response.ETag);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal(
            30,
            json.RootElement.GetProperty("retention")
                .GetProperty("trashRetentionDays").GetInt32());
        Assert.True(
            json.RootElement.GetProperty("sharing")
                .GetProperty("publicLinksEnabled").GetBoolean());
        Assert.Equal(
            4,
            json.RootElement.GetProperty("quotas")
                .GetProperty("concurrentUploads").GetInt64());
        Assert.Equal(
            JsonValueKind.Null,
            json.RootElement.GetProperty("quotas")
                .GetProperty("dailyTransformPixels").ValueKind);
        Assert.Equal(7, json.RootElement.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Patching_policies_applies_the_precondition()
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync(
            "policies-patch",
            admin,
            body: """{"retention":{"trashRetentionDays":14},"quotas":{"concurrentUploads":8}}""",
            ifMatch: "\"v7\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v8\"", response.ETag);
        Assert.Equal(7, admin.ExpectedVersion);
        Assert.NotNull(admin.Patch);
        Assert.Equal(14, admin.Patch.TrashRetentionDays);
        Assert.True(admin.Patch.ConcurrentUploads.IsPresent);
        Assert.Equal(8, admin.Patch.ConcurrentUploads.Value);
        Assert.False(admin.Patch.StorageBytes.IsPresent);
        Assert.Null(admin.Patch.PurgeGraceDays);
        Assert.Equal(UserId, admin.ActorUserId);
    }

    [Fact]
    public async Task An_explicit_null_quota_is_forwarded_as_a_cleared_limit()
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync(
            "policies-patch",
            admin,
            body: """{"quotas":{"storageBytes":null}}""",
            ifMatch: "\"v7\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(admin.Patch);
        Assert.True(admin.Patch.StorageBytes.IsPresent);
        Assert.Null(admin.Patch.StorageBytes.Value);
        Assert.False(admin.Patch.ConcurrentUploads.IsPresent);
    }

    [Fact]
    public async Task A_retention_only_patch_never_mentions_a_quota()
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync(
            "policies-patch",
            admin,
            body: """{"retention":{"trashRetentionDays":21}}""",
            ifMatch: "\"v7\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(admin.Patch);
        Assert.False(admin.Patch.StorageBytes.IsPresent);
        Assert.False(admin.Patch.DailyTransformPixels.IsPresent);
        Assert.False(admin.Patch.ConcurrentUploads.IsPresent);
    }

    [Fact]
    public async Task Patching_policies_without_a_precondition_is_rejected()
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync(
            "policies-patch",
            admin,
            body: """{"retention":{"trashRetentionDays":14}}""");

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Null(admin.Patch);
    }

    [Fact]
    public async Task Patching_policies_with_a_stale_precondition_answers_412()
    {
        var admin = new FakeAdminPort
        {
            PolicyResult = Result.Failure<TenantPolicyView>(ResultError.Conflict(
                "policies.version_conflict",
                "The policy document changed since it was read.")),
        };

        TestResponse response = await SendAsync(
            "policies-patch",
            admin,
            body: """{"retention":{"trashRetentionDays":14}}""",
            ifMatch: "\"v1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Audit_pages_are_redacted_and_carry_a_cursor()
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync("audit", admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement item = Assert.Single(
            json.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal("tenant.member.updated", item.GetProperty("action").GetString());
        Assert.Equal("succeeded", item.GetProperty("outcome").GetString());
        Assert.Equal("user", item.GetProperty("actor").GetProperty("kind").GetString());
        foreach (string leaked in new[] { "before", "after", "beforeJson", "afterJson" })
        {
            Assert.False(item.TryGetProperty(leaked, out _));
        }

        Assert.False(string.IsNullOrWhiteSpace(
            json.RootElement.GetProperty("nextCursor").GetString()));
    }

    [Theory]
    [InlineData("succeeded", "Succeeded")]
    [InlineData("Succeeded", "Succeeded")]
    [InlineData("failed", "Failed")]
    public async Task Audit_accepts_both_outcome_spellings(
        string requested,
        string stored)
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync(
            "audit",
            admin,
            query: $"?outcome={requested}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(stored, admin.AuditQuery!.Outcome);
    }

    [Theory]
    [InlineData("?outcome=whatever", "outcome")]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=500", "limit")]
    public async Task Audit_rejects_an_invalid_query(string query, string field)
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync("audit", admin, query: query);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.True(
            problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Null(admin.AuditQuery);
    }

    [Fact]
    public async Task An_audit_cursor_from_another_tenant_is_a_conflict()
    {
        var admin = new FakeAdminPort();
        TestResponse first = await SendAsync("audit", admin);
        using JsonDocument json = JsonDocument.Parse(first.Body);
        string cursor = json.RootElement.GetProperty("nextCursor").GetString()!;

        TestResponse replayed = await SendAsync(
            "audit",
            admin,
            query: $"?cursor={Uri.EscapeDataString(cursor)}",
            principal: Principal("TenantOwner", OtherTenantId, "members.manage"));

        Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("policies-get")]
    [InlineData("audit")]
    public async Task Anonymous_callers_are_rejected(string operation)
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync(
            operation,
            admin,
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("storage", "TenantAdmin", "quotas.manage")]
    [InlineData("policies-get", "TenantOwner", "members.manage")]
    [InlineData("audit", "Member", "members.manage")]
    public async Task Callers_without_the_role_and_scope_are_forbidden(
        string operation,
        string role,
        string scope)
    {
        var admin = new FakeAdminPort();

        TestResponse response = await SendAsync(
            operation,
            admin,
            principal: Principal(role, TenantId, scope));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static ClaimsPrincipal Principal(
        string role,
        Guid tenantId,
        params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString("D")),
            new(ClaimTypes.NameIdentifier, UserId.ToString("D")),
            new(ClaimTypes.Role, role),
            new("vistara_auth_kind", "Cookie"),
        };
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<TestResponse> SendAsync(
        string operation,
        IAdminPort admin,
        string? query = null,
        string? body = null,
        string? ifMatch = null,
        ClaimsPrincipal? principal = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(admin);
        builder.Services.AddVistaraAdministration();
        WebApplication app = builder.Build();
        app.MapVistaraAdministration();

        (string route, string method) = operation switch
        {
            "storage" => ("/api/v1/admin/storage", HttpMethods.Get),
            "policies-get" => ("/api/v1/admin/policies", HttpMethods.Get),
            "policies-patch" => ("/api/v1/admin/policies", HttpMethods.Patch),
            "audit" => ("/api/v1/admin/audit", HttpMethods.Get),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains(method));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal ?? Principal(
                "TenantOwner",
                TenantId,
                "quotas.manage",
                "members.manage"),
        };
        context.Request.Method = method;
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

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

    private sealed class FakeAdminPort : IAdminPort
    {
        public Guid? StorageTenantId { get; private set; }

        public TenantPolicyPatch? Patch { get; private set; }

        public long? ExpectedVersion { get; private set; }

        public Guid? ActorUserId { get; private set; }

        public AuditQuery? AuditQuery { get; private set; }

        public Result<TenantPolicyView>? PolicyResult { get; init; }

        public ValueTask<StorageSummaryView> GetStorageAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            StorageTenantId = tenantId;
            DateTimeOffset now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
            return ValueTask.FromResult(new StorageSummaryView(
                [
                    new StorageBucketView(
                        "originals",
                        "s3",
                        "healthy",
                        4096,
                        1_000_000,
                        2,
                        now,
                        null),
                ],
                4096,
                512,
                256,
                1_000_000,
                256));
        }

        public ValueTask<Result<TenantPolicyView>> GetPolicyAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(
                new TenantPolicyView(30, 7, true, 30, false, 1_000_000, null, 4, 7)));

        public ValueTask<Result<TenantPolicyView>> UpdatePolicyAsync(
            Guid tenantId,
            Guid actorUserId,
            TenantPolicyPatch patch,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            Patch = patch;
            ExpectedVersion = expectedVersion;
            ActorUserId = actorUserId;
            return ValueTask.FromResult(PolicyResult ?? Result.Success(
                new TenantPolicyView(
                    patch.TrashRetentionDays ?? 30,
                    patch.PurgeGraceDays ?? 7,
                    patch.PublicLinksEnabled ?? true,
                    patch.MaxLinkLifetimeDays ?? 30,
                    patch.RequirePasswordForPublicLinks ?? false,
                    patch.StorageBytes.IsPresent ? patch.StorageBytes.Value : 1_000_000,
                    patch.DailyTransformPixels.IsPresent
                        ? patch.DailyTransformPixels.Value
                        : null,
                    patch.ConcurrentUploads.IsPresent
                        ? patch.ConcurrentUploads.Value
                        : 4,
                    expectedVersion + 1)));
        }

        public ValueTask<AuditPage> ReadAuditAsync(
            AuditQuery query,
            CancellationToken cancellationToken)
        {
            AuditQuery = query;
            DateTimeOffset occurred = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
            Guid id = Guid.CreateVersion7();
            return ValueTask.FromResult(new AuditPage(
                [
                    new AuditEventView(
                        id,
                        occurred,
                        "User",
                        UserId.ToString("D"),
                        "tenant.member.updated",
                        "Succeeded",
                        "tenant_membership",
                        UserId.ToString("D")),
                ],
                occurred,
                id));
        }
    }
}
