using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Capabilities;
using Vistara.Persistence.Capabilities;

namespace Vistara.Api.Features.Capabilities;

public static class CapabilitiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the capability surface defaults. Every registration uses
    /// try-add semantics so a composition root may substitute any port.
    /// </summary>
    public static IServiceCollection AddVistaraCapabilities(
        this IServiceCollection services,
        Action<CapabilitiesSurfaceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CapabilitiesSurfaceOptions();
        configure?.Invoke(options);
        Validate(options);
        services.TryAddSingleton(options);
        services.TryAddScoped<
            ICapabilitiesAuthorizationPort,
            ClaimsCapabilitiesAuthorizationPort>();
        services.TryAddScoped<
            ITenantCapabilitySource,
            RelationalTenantCapabilitySource>();
        services.TryAddScoped<
            ICapabilitySnapshotProvider,
            PlatformCapabilitySnapshotProvider>();
        return services;
    }

    private static void Validate(CapabilitiesSurfaceOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DefaultPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.DefaultPageSize,
            options.MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.CacheLifetime.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Imaging.MaxEncodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Imaging.MaxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Imaging.MaxHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Imaging.MaxAggregatePixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Imaging.MaxEstimatedDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Imaging.ProcessingDeadline.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.Imaging.MaxConcurrentTransforms);
    }
}

public static class CapabilitiesEndpointMapping
{
    public const string PolicyName = "Vistara.Capabilities";

    public static IEndpointRouteBuilder MapVistaraCapabilities(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
                "/api/v1/capabilities",
                (HttpContext context, CancellationToken cancellationToken) =>
                    CapabilitiesEndpoint.GetAsync(
                        context,
                        context.RequestServices
                            .GetRequiredService<ICapabilitiesAuthorizationPort>(),
                        context.RequestServices
                            .GetRequiredService<ICapabilitySnapshotProvider>(),
                        context.RequestServices
                            .GetRequiredService<CapabilitiesSurfaceOptions>(),
                        cancellationToken))
            .RequireAuthorization(PolicyName);
        return endpoints;
    }
}
