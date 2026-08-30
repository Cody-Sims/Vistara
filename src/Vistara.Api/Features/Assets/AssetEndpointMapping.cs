using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.OpenApi.Gallery;
using Vistara.Application.Gallery.Queries;

namespace Vistara.Api.Features.Assets;

public static class AssetEndpointMapping
{
    public const string AssetQueryPolicyName = "Vistara.Assets";

    public static IEndpointRouteBuilder MapVistaraAssetQueries(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints.MapGet(
                "/api/v1/assets",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AssetEndpoint.ListAsync(
                        context,
                        ResolveAuthorization(context),
                        ResolveApplication(context),
                        cancellationToken)),
            "listAssets");
        Map(endpoints.MapGet(
                "/api/v1/assets/{id:guid}",
                (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                    AssetEndpoint.GetAsync(
                        context,
                        id,
                        ResolveAuthorization(context),
                        ResolveApplication(context),
                        cancellationToken)),
            "getAsset");
        Map(endpoints.MapPatch(
                "/api/v1/assets/{id:guid}",
                (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                    AssetEndpoint.UpdateAsync(
                        context,
                        id,
                        ResolveAuthorization(context),
                        ResolveApplication(context),
                        cancellationToken)),
            "updateAsset");
        Map(endpoints.MapPost(
                "/api/v1/assets/bulk",
                AssetBulkMutationEndpoint.ExecuteAsync),
            "bulkMutateAssets");
        Map(endpoints.MapGet(
                "/api/v1/assets/{id:guid}/metadata",
                (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                    AssetEndpoint.GetMetadataAsync(
                        context,
                        id,
                        ResolveAuthorization(context),
                        ResolveApplication(context),
                        cancellationToken)),
            "getAssetMetadata");
        Map(endpoints.MapGet(
                "/api/v1/timeline",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AssetEndpoint.TimelineAsync(
                        context,
                        ResolveAuthorization(context),
                        ResolveApplication(context),
                        cancellationToken)),
            "getTimeline");
        Map(endpoints.MapGet(
                "/api/v1/search/facets",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AssetEndpoint.FacetsAsync(
                        context,
                        ResolveAuthorization(context),
                        ResolveApplication(context),
                        cancellationToken)),
            "getSearchFacets");
        return endpoints;
    }

    private static void Map(RouteHandlerBuilder builder, string operationId) =>
        builder
            .RequireAuthorization(AssetQueryPolicyName)
            .WithGalleryOpenApi(operationId);

    private static IAssetQueryAuthorizationPort ResolveAuthorization(
        HttpContext context) =>
        context.RequestServices.GetRequiredService<IAssetQueryAuthorizationPort>();

    private static IAssetQueryService ResolveApplication(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAssetQueryService>();
}
