using Vistara.Api.Health;
using Vistara.Observability.Health;
using Vistara.Observability.Telemetry;

namespace Vistara.Api.Composition.Runtime;

public sealed class ApiRuntimeMarker;

public static class ApiRuntimeServiceCollectionExtensions
{
    public const string ServiceName = "vistara-api";

    /// <summary>
    /// Registers the API health probes and OpenTelemetry runtime so hosts get
    /// startup, readiness, and liveness reporting with matching telemetry.
    /// Repeated calls are idempotent.
    /// </summary>
    public static IServiceCollection AddVistaraApiRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(ApiRuntimeMarker)))
        {
            return services;
        }

        services.AddSingleton<ApiRuntimeMarker>();
        services.AddVistaraApiHealth();
        services.AddVistaraTelemetry(configuration, ServiceName);
        return services;
    }
}

public static class ApiRuntimeApplicationBuilderExtensions
{
    private const string WiredKey = "vistara.runtime.wired";

    /// <summary>
    /// Installs request telemetry, the liveness short circuit, and the
    /// governed readiness and startup endpoints. Fails fast when the runtime
    /// services were never registered, and never wires the pipeline twice.
    /// </summary>
    public static IApplicationBuilder UseVistaraApiRuntime(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application.Properties.ContainsKey(WiredKey))
        {
            return application;
        }

        if (application.ApplicationServices
                .GetService<ApiRuntimeMarker>() is null)
        {
            throw new InvalidOperationException(
                "AddVistaraApiRuntime must be called before the runtime pipeline is wired.");
        }

        application.Properties[WiredKey] = true;
        application.UseVistaraApiObservability();
        return application.UseVistaraApiHealth();
    }
}
