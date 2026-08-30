using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Albums;
using Vistara.Application.Common;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Favorites;

namespace Vistara.Api.Features.Favorites;

public static class FavoriteEndpointMapping
{
    public static IEndpointRouteBuilder MapVistaraFavorites(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Map(endpoints.MapPut("/api/v1/assets/{id:guid}/favorite", FavoriteAsync));
        Map(endpoints.MapDelete("/api/v1/assets/{id:guid}/favorite", UnfavoriteAsync));
        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(GalleryCurationEndpointSupport.PolicyName);

    private static IGalleryCurationAuthorizationPort Authorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IGalleryCurationAuthorizationPort>();

    private static IFavoriteApplication Application(HttpContext context) =>
        context.RequestServices.GetRequiredService<IFavoriteApplication>();

    private static Task FavoriteAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        FavoriteEndpoint.SetAsync(
            context,
            id,
            favorite: true,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);

    private static Task UnfavoriteAsync(
        Guid id,
        HttpContext context,
        CancellationToken cancellationToken) =>
        FavoriteEndpoint.SetAsync(
            context,
            id,
            favorite: false,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            cancellationToken);
}

public static class FavoriteEndpoint
{
    public static async Task SetAsync(
        HttpContext context,
        Guid assetId,
        bool favorite,
        IGalleryCurationAuthorizationPort authorization,
        IFavoriteApplication application,
        IClock clock,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.ManageFavorites,
            assetId,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        long? version = await GalleryCurationEndpointSupport.ReadExpectedVersionAsync(
            context,
            cancellationToken);
        string? key = await GalleryCurationEndpointSupport.ReadIdempotencyKeyAsync(
            context,
            cancellationToken);
        if (version is null || key is null)
        {
            return;
        }

        CurationResult<CuratedAssetSnapshot> result = await application.SetAsync(
            actor,
            assetId,
            version.Value,
            favorite,
            key,
            clock.UtcNow,
            cancellationToken);
        if (await GalleryCurationEndpointSupport.WriteFailureAsync(
                context,
                result,
                cancellationToken))
        {
            return;
        }

        await GalleryCurationEndpointSupport.WriteAssetAsync(
            context,
            result.Value!,
            cancellationToken);
    }
}
