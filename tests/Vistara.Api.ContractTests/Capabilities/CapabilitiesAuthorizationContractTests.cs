using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Capabilities;
using Vistara.Application.Capabilities;
using Xunit;

namespace Vistara.Api.ContractTests.Capabilities;

public sealed class CapabilitiesAuthorizationContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009f2");

    [Fact]
    public async Task Anonymous_requests_are_rejected_without_reading_capabilities()
    {
        var snapshots = new RecordingSnapshotProvider();

        TestResponse response = await SendAsync(
            new ClaimsPrincipal(new ClaimsIdentity()),
            snapshots);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Equal("no-store", response.CacheControl);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "capabilities_unauthenticated",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Null(snapshots.RequestedTenantId);
    }

    [Fact]
    public async Task Authenticated_principals_without_a_tenant_claim_are_forbidden()
    {
        var snapshots = new RecordingSnapshotProvider();

        TestResponse response = await SendAsync(
            Principal([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D"))]),
            snapshots);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "capabilities_forbidden",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Null(snapshots.RequestedTenantId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("01990a2a-bc00-4000-8000-000000000901")]
    public async Task Malformed_or_non_uuid7_tenant_claims_are_forbidden(string tenant)
    {
        var snapshots = new RecordingSnapshotProvider();

        TestResponse response = await SendAsync(
            Principal([new Claim("tenant_id", tenant)]),
            snapshots);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(snapshots.RequestedTenantId);
    }

    [Fact]
    public async Task Conflicting_tenant_claims_never_select_a_tenant()
    {
        var snapshots = new RecordingSnapshotProvider();

        TestResponse response = await SendAsync(
            Principal(
            [
                new Claim("tenant_id", TenantId.ToString("D")),
                new Claim("tenant_id", OtherTenantId.ToString("D")),
            ]),
            snapshots);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(snapshots.RequestedTenantId);
    }

    [Fact]
    public async Task Query_and_header_tenant_hints_never_override_the_claim()
    {
        var snapshots = new RecordingSnapshotProvider();

        TestResponse response = await SendAsync(
            Principal([new Claim("tenant_id", TenantId.ToString("D"))]),
            snapshots,
            configure: context =>
            {
                context.Request.Headers["X-Tenant-Id"] = OtherTenantId.ToString("D");
                context.Request.QueryString =
                    new QueryString($"?tenantId={OtherTenantId:D}");
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId, snapshots.RequestedTenantId);
    }

    [Fact]
    public async Task Matching_conditional_requests_are_not_modified()
    {
        var snapshots = new RecordingSnapshotProvider();
        ClaimsPrincipal principal =
            Principal([new Claim("tenant_id", TenantId.ToString("D"))]);

        TestResponse first = await SendAsync(principal, snapshots);
        TestResponse second = await SendAsync(
            principal,
            snapshots,
            configure: context =>
                context.Request.Headers.IfNoneMatch = first.ETag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(string.Empty, second.Body);
        Assert.Equal(first.ETag, second.ETag);
    }

    private static ClaimsPrincipal Principal(Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private static async Task<TestResponse> SendAsync(
        ClaimsPrincipal principal,
        ICapabilitySnapshotProvider snapshots,
        Action<HttpContext>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(snapshots);
        builder.Services.AddVistaraCapabilities();
        WebApplication app = builder.Build();
        app.MapVistaraCapabilities();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>());
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal,
        };
        context.Response.Body = new MemoryStream();
        configure?.Invoke(context);

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.ETag.ToString(),
            body);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        string CacheControl,
        string ETag,
        string Body);

    private sealed class RecordingSnapshotProvider : ICapabilitySnapshotProvider
    {
        public Guid? RequestedTenantId { get; private set; }

        public ValueTask<CapabilitySnapshot> GetAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            RequestedTenantId = tenantId;
            return ValueTask.FromResult(new CapabilitySnapshot(
                1,
                "sqlite",
                new StorageCapabilityView("local", false, false, true, 1, 1, 1, 1),
                new ImagingCapabilityView(
                    "net-vips",
                    ["jpeg"],
                    ["jpeg"],
                    1,
                    1,
                    1,
                    1,
                    1,
                    1,
                    30,
                    1),
                new UploadCapabilityView(1, 0, true, 1, true, false, false),
                new SearchCapabilityView(true, true, true, false),
                new ApiCapabilityView(60, 200, 1)));
        }
    }
}
