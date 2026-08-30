using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Lifecycle;
using Xunit;

namespace Vistara.IntegrationTests.Lifecycle;

public sealed class LifecycleApiTests
{
    [Fact]
    public void Lifecycle_routes_match_the_frozen_gallery_contract()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(
                LifecycleEndpointMapping.LifecyclePolicyName,
                policy => policy.RequireAuthenticatedUser()));
        WebApplication app = builder.Build();
        app.MapVistaraLifecycle();

        string[] routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
                $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} " +
                endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "GET /api/v1/trash",
                "GET /api/v1/trash/purge/{batchId:guid}",
                "POST /api/v1/trash/purge",
                "POST /api/v1/trash/purge/{batchId:guid}/confirm",
                "POST /api/v1/trash/restore",
            ],
            routes);
    }
}
