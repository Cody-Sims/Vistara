using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Albums;
using Vistara.Application.Common;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Favorites;
using Vistara.Contracts.Assets;

namespace Vistara.Api.Features.Favorites;

public static class FavoriteEndpointMapping
{
    public static IEndpointRouteBuilder MapVistaraFavorites(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Map(endpoints.MapPut("/api/v1/assets/{id:guid}/favorite", FavoriteAsync));
        Map(endpoints.MapDelete("/api/v1/assets/{id:guid}/favorite", UnfavoriteAsync));
        Map(endpoints.MapPost("/api/v1/assets/bulk", BulkAsync));
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

    private static Task BulkAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        FavoriteEndpoint.BulkAsync(
            context,
            Authorization(context),
            Application(context),
            context.RequestServices.GetRequiredService<IClock>(),
            context.RequestServices.GetRequiredService<IUuid7Generator>(),
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

    public static async Task BulkAsync(
        HttpContext context,
        IGalleryCurationAuthorizationPort authorization,
        IFavoriteApplication application,
        IClock clock,
        IUuid7Generator ids,
        CancellationToken cancellationToken)
    {
        CurationActor? actor = await GalleryCurationEndpointSupport.AuthorizeAsync(
            context,
            authorization,
            GalleryCurationOperation.BulkMutate,
            null,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        string? key = await GalleryCurationEndpointSupport.ReadIdempotencyKeyAsync(
            context,
            cancellationToken);
        AssetBulkMutationRequest? request =
            await GalleryCurationEndpointSupport.ReadRequestAsync<AssetBulkMutationRequest>(
                context,
                cancellationToken);
        if (key is null || request is null)
        {
            return;
        }

        var bulk = new BulkCurationRequest(
            request.Items.Select(item =>
                new BulkCurationTarget(item.Id, item.Version.Value)).ToArray(),
            new BulkCurationAction(
                request.Action.Kind,
                request.Action.TagId,
                request.Action.AlbumId,
                request.Action.Favorite));
        CurationResult<BulkCurationSubmission> result = await application.QueueBulkAsync(
            actor,
            ids.NewId(),
            bulk,
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

        BulkCurationSubmission submission = result.Value!;
        var response = new OperationJobResponse(
            submission.JobId,
            submission.State,
            submission.SubmittedCount,
            submission.SubmittedAt);
        context.Response.Headers.Location = $"/api/v1/jobs/{submission.JobId:D}";
        await GalleryCurationEndpointSupport.WriteJsonAsync(
            context,
            StatusCodes.Status202Accepted,
            response,
            cancellationToken);
    }
}
