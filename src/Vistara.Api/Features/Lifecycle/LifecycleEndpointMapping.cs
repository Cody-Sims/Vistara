using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Lifecycle;

namespace Vistara.Api.Features.Lifecycle;

public static class LifecycleEndpointMapping
{
    public const string LifecyclePolicyName = "Vistara.Lifecycle";

    public static IEndpointRouteBuilder MapVistaraLifecycle(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints.MapGet(
            "/api/v1/trash",
            (HttpContext context, CancellationToken cancellationToken) =>
                LifecycleEndpoint.ListTrashAsync(
                    context,
                    ResolveAuthorization(context),
                    ResolveService(context),
                    context.RequestServices.GetRequiredService<
                        ILifecycleCursorCodec>(),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/trash/restore",
            (HttpContext context, CancellationToken cancellationToken) =>
                LifecycleEndpoint.RestoreAsync(
                    context,
                    ResolveAuthorization(context),
                    ResolveService(context),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/trash/purge",
            (HttpContext context, CancellationToken cancellationToken) =>
                LifecycleEndpoint.CreatePurgeDryRunAsync(
                    context,
                    ResolveAuthorization(context),
                    ResolveService(context),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/trash/purge/{batchId:guid}/confirm",
            (
                Guid batchId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                LifecycleEndpoint.ConfirmPurgeAsync(
                    context,
                    batchId,
                    ResolveAuthorization(context),
                    ResolveService(context),
                    cancellationToken)));
        Map(endpoints.MapGet(
            "/api/v1/trash/purge/{batchId:guid}",
            (
                Guid batchId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                LifecycleEndpoint.GetPurgeBatchAsync(
                    context,
                    batchId,
                    ResolveAuthorization(context),
                    ResolveService(context),
                    cancellationToken)));

        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(LifecyclePolicyName);

    private static ILifecycleAuthorizationPort ResolveAuthorization(
        HttpContext context) =>
        context.RequestServices.GetRequiredService<ILifecycleAuthorizationPort>();

    private static LifecycleService ResolveService(HttpContext context) =>
        context.RequestServices.GetRequiredService<LifecycleService>();
}
