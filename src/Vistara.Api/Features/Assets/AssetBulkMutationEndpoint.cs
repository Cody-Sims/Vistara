using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Albums;
using Vistara.Api.Features.Lifecycle;
using Vistara.Application.Common;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Favorites;
using Vistara.Application.Lifecycle;
using Vistara.Contracts.Assets;

namespace Vistara.Api.Features.Assets;

internal static class AssetBulkMutationEndpoint
{
    internal static async Task ExecuteAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
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

        if (request.Action.Kind == "trash")
        {
            await LifecycleEndpoint.TrashAssetsAsync(
                context,
                request,
                key,
                context.RequestServices.GetRequiredService<
                    ILifecycleAuthorizationPort>(),
                context.RequestServices.GetRequiredService<LifecycleService>(),
                cancellationToken);
            return;
        }

        await QueueCurationAsync(context, request, key, cancellationToken);
    }

    private static async Task QueueCurationAsync(
        HttpContext context,
        AssetBulkMutationRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        IGalleryCurationAuthorizationPort authorization =
            context.RequestServices.GetRequiredService<
                IGalleryCurationAuthorizationPort>();
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

        var bulk = new BulkCurationRequest(
            request.Items.Select(item =>
                new BulkCurationTarget(item.Id, item.Version.Value)).ToArray(),
            new BulkCurationAction(
                request.Action.Kind,
                request.Action.TagId,
                request.Action.AlbumId,
                request.Action.Favorite));
        IFavoriteApplication application =
            context.RequestServices.GetRequiredService<IFavoriteApplication>();
        IUuid7Generator ids =
            context.RequestServices.GetRequiredService<IUuid7Generator>();
        IClock clock = context.RequestServices.GetRequiredService<IClock>();
        CurationResult<BulkCurationSubmission> result =
            await application.QueueBulkAsync(
                actor,
                ids.NewId(),
                bulk,
                idempotencyKey,
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
