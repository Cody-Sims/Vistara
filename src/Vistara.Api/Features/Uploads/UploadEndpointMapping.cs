using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Vistara.Api.Features.Uploads;

public static class UploadEndpointMapping
{
    public const string UploadPolicyName = "Vistara.Uploads";
    public const long MaximumProxyRequestBodyBytes = 50L * 1024 * 1024;

    public static IEndpointRouteBuilder MapVistaraUploads(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints.MapPost(
            "/api/v1/uploads",
            (HttpContext context, CancellationToken cancellationToken) =>
                UploadEndpoint.CreateAsync(
                    context,
                    ResolveAuthorization(context),
                    ResolveApplication(context),
                    context.RequestServices.GetRequiredService<
                        Vistara.Application.Common.IClock>(),
                    context.RequestServices.GetRequiredService<
                        Vistara.Application.Common.IUuid7Generator>(),
                    cancellationToken)));
        Map(endpoints.MapPut(
            "/api/v1/uploads/{id:guid}/content",
            (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                UploadEndpoint.UploadContentAsync(
                    context,
                    id,
                    ResolveAuthorization(context),
                    ResolveApplication(context),
                    context.RequestServices.GetRequiredService<
                        Vistara.Application.Common.IClock>(),
                    cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(
                MaximumProxyRequestBodyBytes)));
        Map(endpoints.MapGet(
            "/api/v1/uploads/{id:guid}",
            (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                UploadEndpoint.GetStatusAsync(
                    context,
                    id,
                    ResolveAuthorization(context),
                    ResolveApplication(context),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/uploads/{id:guid}/parts",
            (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                UploadEndpoint.RefreshPartsAsync(
                    context,
                    id,
                    ResolveAuthorization(context),
                    ResolveApplication(context),
                    context.RequestServices.GetRequiredService<
                        Vistara.Application.Common.IClock>(),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/uploads/{id:guid}/commit",
            (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                UploadEndpoint.CommitAsync(
                    context,
                    id,
                    ResolveAuthorization(context),
                    ResolveApplication(context),
                    cancellationToken)));
        Map(endpoints.MapDelete(
            "/api/v1/uploads/{id:guid}",
            (Guid id, HttpContext context, CancellationToken cancellationToken) =>
                UploadEndpoint.AbortAsync(
                    context,
                    id,
                    ResolveAuthorization(context),
                    ResolveApplication(context),
                    context.RequestServices.GetRequiredService<
                        Vistara.Application.Common.IClock>(),
                    cancellationToken)));

        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(UploadPolicyName);

    private static IUploadAuthorizationPort ResolveAuthorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IUploadAuthorizationPort>();

    private static IUploadApplicationPort ResolveApplication(HttpContext context) =>
        context.RequestServices.GetRequiredService<IUploadApplicationPort>();
}
