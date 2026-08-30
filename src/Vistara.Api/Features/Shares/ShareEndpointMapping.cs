using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Sharing;

namespace Vistara.Api.Features.Shares;

public static class ShareEndpointMapping
{
    public static IEndpointRouteBuilder MapVistaraShares(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(
            "/api/v1/shares",
            (HttpContext context, CancellationToken cancellationToken) =>
                ShareEndpoint.ListAsync(
                    context,
                    context.RequestServices
                        .GetRequiredService<IShareAuthorizationPort>(),
                    context.RequestServices.GetRequiredService<ShareService>(),
                    cancellationToken));
        endpoints.MapPost(
            "/api/v1/shares",
            (HttpContext context, CancellationToken cancellationToken) =>
                ShareEndpoint.CreateAsync(
                    context,
                    context.RequestServices
                        .GetRequiredService<IShareAuthorizationPort>(),
                    context.RequestServices.GetRequiredService<ShareService>(),
                    cancellationToken));
        endpoints.MapGet(
            "/api/v1/shares/{shareId:guid}",
            (
                Guid shareId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ShareEndpoint.GetAsync(
                    context,
                    shareId,
                    context.RequestServices
                        .GetRequiredService<IShareAuthorizationPort>(),
                    context.RequestServices.GetRequiredService<ShareService>(),
                    cancellationToken));
        endpoints.MapPatch(
            "/api/v1/shares/{shareId:guid}",
            (
                Guid shareId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ShareEndpoint.UpdateAsync(
                    context,
                    shareId,
                    context.RequestServices
                        .GetRequiredService<IShareAuthorizationPort>(),
                    context.RequestServices.GetRequiredService<ShareService>(),
                    cancellationToken));
        endpoints.MapDelete(
            "/api/v1/shares/{shareId:guid}",
            (
                Guid shareId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ShareEndpoint.RevokeAsync(
                    context,
                    shareId,
                    context.RequestServices
                        .GetRequiredService<IShareAuthorizationPort>(),
                    context.RequestServices.GetRequiredService<ShareService>(),
                    cancellationToken));
        endpoints.MapGet(
            "/api/v1/public/shares/{publicToken}",
            (
                string publicToken,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ShareEndpoint.GetPublicAsync(
                    context,
                    publicToken,
                    context.RequestServices.GetRequiredService<ShareService>(),
                    cancellationToken));
        endpoints.MapPost(
            "/api/v1/public/shares/{publicToken}/challenge",
            (
                string publicToken,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ShareEndpoint.ChallengeAsync(
                    context,
                    publicToken,
                    context.RequestServices.GetRequiredService<ShareService>(),
                    cancellationToken));
        return endpoints;
    }
}
