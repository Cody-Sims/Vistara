using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Assets;
using Vistara.Application.Gallery.Queries;
using Xunit;

namespace Vistara.Api.ContractTests.AssetQueries;

public sealed class AssetEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000711");
    private static readonly Guid ActorId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000712");
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000713");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Mapping_registers_only_the_frozen_asset_query_routes_with_policy_and_metadata()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();

        app.MapVistaraAssetQueries();

        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(7, endpoints.Length);
        AssertRoute(endpoints, "GET", "/api/v1/assets", "listAssets");
        AssertRoute(endpoints, "GET", "/api/v1/assets/{id:guid}", "getAsset");
        AssertRoute(endpoints, "PATCH", "/api/v1/assets/{id:guid}", "updateAsset");
        AssertRoute(
            endpoints,
            "POST",
            "/api/v1/assets/bulk",
            "bulkMutateAssets");
        AssertRoute(
            endpoints,
            "GET",
            "/api/v1/assets/{id:guid}/metadata",
            "getAssetMetadata");
        AssertRoute(endpoints, "GET", "/api/v1/timeline", "getTimeline");
        AssertRoute(endpoints, "GET", "/api/v1/search/facets", "getSearchFacets");
        Assert.All(endpoints, endpoint =>
        {
            IAuthorizeData authorization =
                Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(AssetEndpointMapping.AssetQueryPolicyName, authorization.Policy);
        });
    }

    [Fact]
    public async Task Authorization_precedes_queries_and_conceals_cross_tenant_assets()
    {
        var authorization = new FakeAssetAuthorizationPort
        {
            AssetAccess = AssetQueryAccess.Denied(AssetQueryAccessStatus.Concealed),
        };
        var application = new FakeAssetQueryService();

        TestResponse response = await SendAsync(
            authorization,
            application,
            "GET",
            "/api/v1/assets/{id:guid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, application.TotalCalls);
        Assert.DoesNotContain(AssetId.ToString("D"), response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_parses_bounded_filters_and_returns_opaque_cursor_without_totals()
    {
        var application = new FakeAssetQueryService
        {
            PageResult = AssetQueryPageResult.Success(new AssetQueryPage(
                [Item()],
                "opaque-cursor")),
        };

        TestResponse response = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets",
            query: "?limit=20&search=lake&statuses=ready&contentTypes=image%2Fjpeg" +
                "&favorite=true&sort=title&direction=asc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = response.Json();
        Assert.Equal("opaque-cursor", body.RootElement.GetProperty("nextCursor").GetString());
        Assert.False(body.RootElement.TryGetProperty("total", out _));
        Assert.Equal("lake", application.LastCriteria?.Search);
        Assert.Equal(AssetSort.Title, application.LastCriteria?.Sort);
        Assert.Equal(SortDirection.Ascending, application.LastCriteria?.Direction);
        Assert.Equal(["Ready"], application.LastCriteria?.Statuses);
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("?limit=201", "asset_query_invalid")]
    [InlineData("?statuses=Ready", "asset_query_invalid")]
    [InlineData("?statuses=trashed", "asset_query_invalid")]
    [InlineData("?statuses=purged", "asset_query_invalid")]
    [InlineData("?sort=privateMetadata", "asset_query_invalid")]
    [InlineData("?filter=%7B%22gps%22%3Atrue%7D", "asset_query_invalid")]
    [InlineData("?cursor=not-a-valid-cursor", "asset_cursor_invalid")]
    public async Task Invalid_bounds_sorts_and_cursors_are_safe_problems(
        string query,
        string expectedCode)
    {
        var application = new FakeAssetQueryService
        {
            PageResult = AssetQueryPageResult.Failure(
                expectedCode == "asset_cursor_invalid"
                    ? AssetQueryResultStatus.InvalidCursor
                    : AssetQueryResultStatus.InvalidQuery),
        };

        TestResponse response = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets",
            query: query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, response.ProblemCode());
        Assert.DoesNotContain("filterHash", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_or_json_filter_parameters_are_rejected_before_application_queries()
    {
        var application = new FakeAssetQueryService();

        TestResponse response = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets",
            query: "?filter=%7B%22gps%22%3Atrue%7D");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("asset_query_invalid", response.ProblemCode());
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public async Task Detail_and_metadata_project_only_safe_fields_and_application_paths()
    {
        var application = new FakeAssetQueryService
        {
            DetailResult = AssetDetailResult.Success(new AssetDetail(
                Item() with
                {
                    Renditions =
                    [
                        new AssetDeliverySource(
                            "thumb",
                            "/delivery/assets/asset/rendition",
                            400,
                            300,
                            "image/webp"),
                    ],
                },
                Metadata(),
                [])),
            MetadataResult = AssetMetadataResult.Success(Metadata()),
        };

        TestResponse detail = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets/{id:guid}");
        TestResponse metadata = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets/{id:guid}/metadata");

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("\"v3\"", detail.Headers.ETag.ToString());
        Assert.Contains("/delivery/assets/", detail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", detail.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", detail.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Contains("\"cameraMake\":\"Vistara\"", metadata.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("gps", metadata.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", metadata.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeline_groups_by_capture_date_with_import_fallback_and_stable_month_keys()
    {
        AssetQueryItem captured = Item() with
        {
            Id = Guid.Parse("01990a2a-bc00-7000-8000-000000000714"),
            CapturedAt = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            ImportedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        };
        AssetQueryItem fallback = Item() with
        {
            Id = Guid.Parse("01990a2a-bc00-7000-8000-000000000715"),
            CapturedAt = null,
            ImportedAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
        };
        var application = new FakeAssetQueryService
        {
            PageResult = AssetQueryPageResult.Success(
                new AssetQueryPage([captured, fallback], null)),
        };

        TestResponse response = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/timeline",
            query: "?groupBy=month");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement group = Assert.Single(
            response.Json().RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("2026-08", group.GetProperty("key").GetString());
        Assert.Equal(2, group.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Metadata_requires_explicit_permission_without_querying_the_asset()
    {
        var authorization = new FakeAssetAuthorizationPort
        {
            AssetAccess = AssetQueryAccess.Authorized(
                TenantId,
                ActorId,
                canReadRestrictedMetadata: false,
                canUpdateMetadata: false),
        };
        var application = new FakeAssetQueryService();

        TestResponse response = await SendAsync(
            authorization,
            application,
            "GET",
            "/api/v1/assets/{id:guid}/metadata");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public async Task Update_requires_canonical_if_match_and_idempotency_key()
    {
        var application = new FakeAssetQueryService();

        TestResponse missing = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "PATCH",
            "/api/v1/assets/{id:guid}",
            body: """{"title":"Updated"}""");
        TestResponse malformed = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "PATCH",
            "/api/v1/assets/{id:guid}",
            body: """{"title":"Updated"}""",
            ifMatch: "3",
            idempotencyKey: "update-1");

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("if_match_required", missing.ProblemCode());
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("if_match_invalid", malformed.ProblemCode());
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public async Task Update_returns_new_etag_and_maps_version_conflicts_without_current_state()
    {
        var application = new FakeAssetQueryService
        {
            UpdateResult = AssetUpdateResult.Success(new AssetDetail(
                Item() with { Title = "Updated", Version = 4 },
                Metadata(),
                [])),
        };

        TestResponse updated = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "PATCH",
            "/api/v1/assets/{id:guid}",
            body: """{"title":"Updated","description":null}""",
            ifMatch: "\"v3\"",
            idempotencyKey: "update-2");
        Assert.True(application.LastPatch?.HasDescription);
        Assert.Null(application.LastPatch?.Description);
        application.UpdateResult =
            AssetUpdateResult.Failure(AssetQueryResultStatus.VersionConflict);
        TestResponse conflict = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "PATCH",
            "/api/v1/assets/{id:guid}",
            body: """{"title":"Updated again"}""",
            ifMatch: "\"v3\"",
            idempotencyKey: "update-3");

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("\"v4\"", updated.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.Equal("asset_version_conflict", conflict.ProblemCode());
        Assert.DoesNotContain("\"v4\"", conflict.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listed_and_detailed_assets_publish_documented_enum_tokens()
    {
        AssetQueryItem stored = Item() with
        {
            Renditions =
            [
                new AssetDeliverySource(
                    "thumb",
                    "/media/pipeline/source/thumb-512.webp",
                    512,
                    384,
                    "image/webp"),
            ],
        };
        var application = new FakeAssetQueryService
        {
            PageResult = AssetQueryPageResult.Success(
                new AssetQueryPage([stored], null)),
            DetailResult = AssetDetailResult.Success(
                new AssetDetail(stored, Metadata(), [])),
        };

        TestResponse list = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets");
        TestResponse detail = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets/{id:guid}");

        JsonElement listed = list.Json().RootElement.GetProperty("items")[0];
        Assert.Equal("ready", listed.GetProperty("status").GetString());
        Assert.Equal("private", listed.GetProperty("visibility").GetString());
        Assert.Equal(
            "thumb",
            listed.GetProperty("renditions")[0].GetProperty("kind").GetString());
        JsonElement detailed = detail.Json().RootElement.GetProperty("asset");
        Assert.Equal("ready", detailed.GetProperty("status").GetString());
        Assert.Equal("private", detailed.GetProperty("visibility").GetString());
        Assert.DoesNotContain("\"Ready\"", list.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Private\"", list.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_accepts_the_documented_visibility_token_and_rejects_stored_casing()
    {
        var application = new FakeAssetQueryService
        {
            UpdateResult = AssetUpdateResult.Success(
                new AssetDetail(Item(), Metadata(), [])),
        };

        TestResponse documented = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "PATCH",
            "/api/v1/assets/{id:guid}",
            body: """{"visibility":"tenant"}""",
            ifMatch: "\"v3\"",
            idempotencyKey: "visibility-1");
        string? forwarded = application.LastPatch?.Visibility;
        TestResponse stored = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "PATCH",
            "/api/v1/assets/{id:guid}",
            body: """{"visibility":"Tenant"}""",
            ifMatch: "\"v3\"",
            idempotencyKey: "visibility-2");

        Assert.Equal(HttpStatusCode.OK, documented.StatusCode);
        Assert.Equal("Tenant", forwarded);
        Assert.Equal(HttpStatusCode.BadRequest, stored.StatusCode);
        Assert.Equal("asset_update_invalid", stored.ProblemCode());
    }

    [Fact]
    public async Task Documented_status_filters_reach_the_query_on_list_and_timeline()
    {
        var application = new FakeAssetQueryService
        {
            PageResult = AssetQueryPageResult.Success(
                new AssetQueryPage([Item()], null)),
        };

        TestResponse list = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets",
            query: "?statuses=ready,processing");
        IReadOnlyList<string>? listStatuses = application.LastCriteria?.Statuses;
        TestResponse timeline = await SendAsync(
            new FakeAssetAuthorizationPort(),
            application,
            "GET",
            "/api/v1/timeline",
            query: "?statuses=ready");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(["Processing", "Ready"], listStatuses);
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        Assert.Equal(["Ready"], application.LastCriteria?.Statuses);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_without_becoming_a_problem_response()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var application = new FakeAssetQueryService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(
                new FakeAssetAuthorizationPort(),
                application,
                "GET",
                "/api/v1/assets",
                cancellationToken: cancellation.Token));

        Assert.True(application.CancellationObserved);
    }

    private static AssetQueryItem Item() =>
        new(
            AssetId,
            "Lake",
            "Safe description",
            "Ready",
            "Private",
            1,
            "image/jpeg",
            "jpeg",
            800,
            600,
            10_000,
            Now.AddDays(-1),
            Now.AddDays(-2),
            Now,
            false,
            [],
            [],
            3);

    private static AssetMetadata Metadata() =>
        new(
            AssetId,
            1,
            Now.AddDays(-1),
            1,
            "Vistara",
            "One",
            null,
            "sRGB",
            RestrictedMetadataAvailable: true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cameraMake"] = "Vistara",
            });

    private static void AssertRoute(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route,
        string operationId)
    {
        RouteEndpoint endpoint = Assert.Single(
            endpoints,
            candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains(method, StringComparer.Ordinal));
        Assert.Equal(operationId, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
    }

    private static async Task<TestResponse> SendAsync(
        IAssetQueryAuthorizationPort authorization,
        IAssetQueryService application,
        string method,
        string route,
        string? query = null,
        string? body = null,
        string? ifMatch = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped(_ => authorization);
        builder.Services.AddScoped(_ => application);
        await using WebApplication app = builder.Build();
        app.MapVistaraAssetQueries();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains(method, StringComparer.Ordinal));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
        };
        context.Request.Method = method;
        context.Request.Path = route;
        context.Request.QueryString = new QueryString(query ?? string.Empty);
        context.Request.RouteValues["id"] = AssetId.ToString("D");
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }

        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        if (idempotencyKey is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers,
            context.Response.ContentType,
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        IHeaderDictionary Headers,
        string? ContentType,
        string Body)
    {
        public JsonDocument Json() => JsonDocument.Parse(Body);

        public string ProblemCode() =>
            Json().RootElement.GetProperty("code").GetString()!;
    }
}

internal sealed class FakeAssetAuthorizationPort : IAssetQueryAuthorizationPort
{
    public AssetQueryAccess CollectionAccess { get; init; } =
        AssetQueryAccess.Authorized(
            AssetEndpointContractTestsTenantIds.TenantId,
            AssetEndpointContractTestsTenantIds.ActorId,
            canReadRestrictedMetadata: true,
            canUpdateMetadata: true);

    public AssetQueryAccess AssetAccess { get; init; } =
        AssetQueryAccess.Authorized(
            AssetEndpointContractTestsTenantIds.TenantId,
            AssetEndpointContractTestsTenantIds.ActorId,
            canReadRestrictedMetadata: true,
            canUpdateMetadata: true);

    public ValueTask<AssetQueryAccess> AuthorizeCollectionAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CollectionAccess);

    public ValueTask<AssetQueryAccess> AuthorizeAssetAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(AssetAccess);
}

internal static class AssetEndpointContractTestsTenantIds
{
    internal static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000711");
    internal static readonly Guid ActorId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000712");
}

internal sealed class FakeAssetQueryService : IAssetQueryService
{
    public AssetQueryPageResult PageResult { get; set; } =
        AssetQueryPageResult.Success(new AssetQueryPage([], null));
    public AssetDetailResult DetailResult { get; set; } =
        AssetDetailResult.Failure(AssetQueryResultStatus.NotFound);
    public AssetMetadataResult MetadataResult { get; set; } =
        AssetMetadataResult.Failure(AssetQueryResultStatus.NotFound);
    public AssetUpdateResult UpdateResult { get; set; } =
        AssetUpdateResult.Failure(AssetQueryResultStatus.NotFound);
    public AssetFacetResult FacetResult { get; set; } =
        AssetFacetResult.Success([]);
    public int TotalCalls { get; private set; }
    public bool CancellationObserved { get; private set; }
    public AssetQueryCriteria? LastCriteria { get; private set; }
    public AssetMetadataPatch? LastPatch { get; private set; }

    public ValueTask<AssetQueryPageResult> ListAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        string? cursor,
        CancellationToken cancellationToken)
    {
        Observe(cancellationToken);
        LastCriteria = criteria;
        return ValueTask.FromResult(PageResult);
    }

    public ValueTask<AssetQueryPageResult> TimelineAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        string groupBy,
        string? cursor,
        CancellationToken cancellationToken)
    {
        Observe(cancellationToken);
        LastCriteria = criteria;
        return ValueTask.FromResult(PageResult);
    }

    public ValueTask<AssetFacetResult> GetFacetsAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        CancellationToken cancellationToken)
    {
        Observe(cancellationToken);
        LastCriteria = criteria;
        return ValueTask.FromResult(FacetResult);
    }

    public ValueTask<AssetDetailResult> GetAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        Observe(cancellationToken);
        return ValueTask.FromResult(DetailResult);
    }

    public ValueTask<AssetMetadataResult> GetMetadataAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        Observe(cancellationToken);
        return ValueTask.FromResult(MetadataResult);
    }

    public ValueTask<AssetUpdateResult> UpdateAsync(
        AssetQueryScope scope,
        Guid assetId,
        long expectedVersion,
        string idempotencyKey,
        AssetMetadataPatch patch,
        CancellationToken cancellationToken)
    {
        Observe(cancellationToken);
        LastPatch = patch;
        return ValueTask.FromResult(UpdateResult);
    }

    private void Observe(CancellationToken cancellationToken)
    {
        TotalCalls++;
        CancellationObserved = cancellationToken.IsCancellationRequested;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
