using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Vistara.Api.Features.Albums;
using Vistara.Api.Features.Assets;
using Vistara.Api.Features.Favorites;
using Vistara.Api.Features.Lifecycle;
using Vistara.Api.Features.Shares;
using Vistara.Api.Features.Tags;
using Vistara.Api.OpenApi.Gallery;

namespace Vistara.Api.Composition.Gallery;

public static partial class GalleryEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapVistaraGalleryFeatures(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        HashSet<string> existing = RuntimeKeys(endpoints);

        endpoints.MapVistaraAssetQueries();
        endpoints.MapVistaraAlbums();
        endpoints.MapVistaraTags();
        endpoints.MapVistaraFavorites();
        endpoints.MapVistaraShares();
        endpoints.MapVistaraLifecycle();

        ValidateOpenApiParity(endpoints, existing);
        return endpoints;
    }

    private static void ValidateOpenApiParity(
        IEndpointRouteBuilder endpoints,
        HashSet<string> existing)
    {
        HashSet<string> expected = GalleryOpenApiCatalog.Operations
            .Select(operation => Key(operation.Method, operation.Path))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> mapped = RuntimeKeys(endpoints);
        mapped.ExceptWith(existing);
        string[] missing = expected.Except(mapped).Order().ToArray();
        string[] unexpected = mapped.Except(expected).Order().ToArray();
        if (missing.Length == 0 && unexpected.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "The gallery OpenAPI catalog does not match the mapped runtime routes. " +
            $"Missing: {string.Join(", ", missing)}. " +
            $"Unexpected: {string.Join(", ", unexpected)}.");
    }

    private static string Key(string method, string route) =>
        string.Concat(
            method.ToUpperInvariant(),
            " ",
            RouteParameterPattern().Replace(route, "{}"));

    private static HashSet<string> RuntimeKeys(
        IEndpointRouteBuilder endpoints) =>
        endpoints.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                endpoint.Metadata
                    .GetMetadata<IHttpMethodMetadata>()?
                    .HttpMethods
                    .Select(method => Key(
                        method,
                        endpoint.RoutePattern.RawText ?? string.Empty)) ??
                [])
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameterPattern();
}
