using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vistara.Api.Features.Derivatives;
using Xunit;

namespace Vistara.Api.ContractTests.Derivatives;

public sealed class DerivativeEndpointMappingTests
{
    [Fact]
    public void Mapping_extension_registers_versioned_derivative_routes()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();

        app.MapVistaraDerivatives();

        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        AssertRoute(endpoints, "GET", "/api/v1/derivative-presets");
        AssertRoute(endpoints, "GET", "/api/v1/assets/{assetId:guid}/derivatives");
        AssertRoute(endpoints, "POST", "/api/v1/assets/{assetId:guid}/derivatives");
        AssertRoute(
            endpoints,
            "GET",
            "/api/v1/assets/{assetId:guid}/derivatives/{requestId:guid}");
    }

    private static void AssertRoute(
        IEnumerable<RouteEndpoint> endpoints,
        string method,
        string route)
    {
        RouteEndpoint endpoint = Assert.Single(
            endpoints,
            candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains(method, StringComparer.Ordinal));
        Assert.Contains(
            method,
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }
}
