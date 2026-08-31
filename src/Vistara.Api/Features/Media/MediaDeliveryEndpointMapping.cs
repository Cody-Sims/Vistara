using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Shares;
using Vistara.Application.Common;
using Vistara.Application.Sharing;
using Vistara.Auth.Sharing;
using Vistara.Persistence;

namespace Vistara.Api.Features.Media;

public static class MediaDeliveryEndpointMapping
{
    private static readonly string[] GetAndHeadMethods = ["GET", "HEAD"];

    public static IEndpointRouteBuilder MapVistaraMedia(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods(
            "/media/{pipeline}/{sourceHash}/{recipeHash}.{extension}",
            GetAndHeadMethods,
            (
                string pipeline,
                string sourceHash,
                string recipeHash,
                string extension,
                HttpContext context,
                CancellationToken cancellationToken) =>
                MediaDeliveryEndpoint.PublicDerivativeAsync(
                    context,
                    pipeline,
                    sourceHash,
                    recipeHash,
                    extension,
                    context.RequestServices
                        .GetRequiredService<IMediaDeliveryApplicationPort>(),
                    cancellationToken));
        endpoints.MapMethods(
            "/delivery/{pipeline}/{sourceHash}/{recipeHash}.{extension}",
            GetAndHeadMethods,
            (
                string pipeline,
                string sourceHash,
                string recipeHash,
                string extension,
                HttpContext context,
                CancellationToken cancellationToken) =>
                MediaDeliveryEndpoint.PrivateDerivativeAsync(
                    context,
                    pipeline,
                    sourceHash,
                    recipeHash,
                    extension,
                    context.RequestServices
                        .GetRequiredService<IMediaDeliveryAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IMediaDeliveryApplicationPort>(),
                    cancellationToken));
        endpoints.MapMethods(
            "/delivery/assets/{assetId:guid}/{renditionId:guid}",
            GetAndHeadMethods,
            (
                Guid assetId,
                Guid renditionId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                MediaDeliveryEndpoint.AssetRenditionAsync(
                    context,
                    assetId,
                    renditionId,
                    context.RequestServices
                        .GetRequiredService<IMediaDeliveryAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IMediaDeliveryApplicationPort>(),
                    cancellationToken));
        endpoints.MapMethods(
            "/api/v1/assets/{assetId:guid}/original",
            GetAndHeadMethods,
            (
                Guid assetId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                MediaDeliveryEndpoint.OriginalAsync(
                    context,
                    assetId,
                    context.RequestServices
                        .GetRequiredService<IMediaDeliveryAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IMediaDeliveryApplicationPort>(),
                    cancellationToken));
        endpoints.MapMethods(
            ShareRenditionRoute.Pattern,
            GetAndHeadMethods,
            (
                string publicToken,
                Guid assetId,
                string renditionId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ShareRenditionEndpoint.GetAsync(
                    context,
                    publicToken,
                    assetId,
                    renditionId,
                    context.RequestServices.GetRequiredService<ShareService>(),
                    context.RequestServices
                        .GetRequiredService<ShareDeliveryGrantAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IServiceScopeFactory>(),
                    context.RequestServices.GetRequiredService<IClock>(),
                    cancellationToken));

        return endpoints;
    }
}
