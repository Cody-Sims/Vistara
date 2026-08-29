using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Vistara.Api.Features.Derivatives;

public static class DerivativeEndpointMapping
{
    public static IEndpointRouteBuilder MapVistaraDerivatives(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/api/v1/derivative-presets",
            (HttpContext context, CancellationToken cancellationToken) =>
                DerivativeEndpoint.ListPresetsAsync(
                    context,
                    context.RequestServices
                        .GetRequiredService<IDerivativeAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IDerivativeApplicationPort>(),
                    cancellationToken));
        endpoints.MapGet(
            "/api/v1/assets/{assetId:guid}/derivatives",
            (Guid assetId, HttpContext context, CancellationToken cancellationToken) =>
                DerivativeEndpoint.ListAsync(
                    context,
                    assetId,
                    context.RequestServices
                        .GetRequiredService<IDerivativeAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IDerivativeApplicationPort>(),
                    cancellationToken));
        endpoints.MapPost(
            "/api/v1/assets/{assetId:guid}/derivatives",
            (Guid assetId, HttpContext context, CancellationToken cancellationToken) =>
                DerivativeEndpoint.RequestAsync(
                    context,
                    assetId,
                    context.RequestServices
                        .GetRequiredService<IDerivativeAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IDerivativeApplicationPort>(),
                    cancellationToken));
        endpoints.MapGet(
            "/api/v1/assets/{assetId:guid}/derivatives/{requestId:guid}",
            (
                Guid assetId,
                Guid requestId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                DerivativeEndpoint.GetStatusAsync(
                    context,
                    assetId,
                    requestId,
                    context.RequestServices
                        .GetRequiredService<IDerivativeAuthorizationPort>(),
                    context.RequestServices
                        .GetRequiredService<IDerivativeApplicationPort>(),
                    cancellationToken));

        return endpoints;
    }
}
