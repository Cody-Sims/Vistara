using System.Text;
using Vistara.Api.Composition.Gallery;

namespace Vistara.Api.OpenApi.Gallery;

public static class GalleryOpenApiEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapVistaraGalleryOpenApi(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.ServiceProvider.ValidateVistaraGalleryComposition();
        endpoints.MapVistaraGalleryFeatures();
        endpoints.MapGet(
                GalleryOpenApiDocument.Route,
                static () => Results.Text(
                    GalleryOpenApiDocument.Json,
                    "application/vnd.oai.openapi+json;version=3.1",
                    Encoding.UTF8))
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(
                "/openapi/{**path}",
                static () => Results.NotFound())
            .AllowAnonymous()
            .ExcludeFromDescription();
        return endpoints;
    }
}
