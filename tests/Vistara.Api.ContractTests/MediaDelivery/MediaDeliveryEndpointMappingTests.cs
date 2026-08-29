using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vistara.Api.Features.Media;
using Xunit;

namespace Vistara.Api.ContractTests.MediaDelivery;

public sealed class MediaDeliveryEndpointMappingTests
{
    [Fact]
    public void Mapping_extension_registers_get_and_head_delivery_routes_without_credentials()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();

        app.MapVistaraMedia();

        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        AssertMethods(
            endpoints,
            "/media/{pipeline}/{sourceHash}/{recipeHash}.{extension}",
            "GET",
            "HEAD");
        AssertMethods(
            endpoints,
            "/delivery/{pipeline}/{sourceHash}/{recipeHash}.{extension}",
            "GET",
            "HEAD");
        Assert.DoesNotContain(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText?.Contains(
                "grant",
                StringComparison.OrdinalIgnoreCase) == true);
        AssertMethods(
            endpoints,
            "/api/v1/assets/{assetId:guid}/original",
            "GET",
            "HEAD");
    }

    private static void AssertMethods(
        IEnumerable<RouteEndpoint> endpoints,
        string route,
        params string[] methods)
    {
        RouteEndpoint endpoint = Assert.Single(
            endpoints,
            candidate => candidate.RoutePattern.RawText == route);
        HttpMethodMetadata metadata =
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!;
        Assert.All(
            methods,
            method => Assert.Contains(
                method,
                metadata.HttpMethods,
                StringComparer.Ordinal));
    }
}
