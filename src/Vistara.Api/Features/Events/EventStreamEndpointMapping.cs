using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Vistara.Api.Features.Events;

public static class EventStreamEndpointMapping
{
    public static RouteHandlerBuilder MapVistaraEventStream(
        this IEndpointRouteBuilder endpoints,
        EventStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        EventStreamOptions configuredOptions = options ?? new EventStreamOptions();
        configuredOptions.Validate();

        return endpoints.MapGet(
            "/api/v1/events",
            (HttpContext context, CancellationToken cancellationToken) =>
                EventStreamEndpoint.HandleAsync(
                    context,
                    context.RequestServices.GetRequiredService<IEventStreamAuthorizationPort>(),
                    context.RequestServices.GetRequiredService<IEventStreamSource>(),
                    SystemEventStreamHeartbeatDelay.Instance,
                    configuredOptions,
                    cancellationToken));
    }

    private sealed class SystemEventStreamHeartbeatDelay : IEventStreamHeartbeatDelay
    {
        internal static SystemEventStreamHeartbeatDelay Instance { get; } = new();

        public ValueTask DelayAsync(
            TimeSpan interval,
            CancellationToken cancellationToken) =>
            new(Task.Delay(interval, cancellationToken));
    }
}
