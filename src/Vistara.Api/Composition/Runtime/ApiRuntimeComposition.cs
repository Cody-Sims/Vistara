using Microsoft.AspNetCore.Hosting;
using Vistara.Api.Health;
using Vistara.Observability.Telemetry;

[assembly: HostingStartup(
    typeof(Vistara.Api.Composition.Runtime.VistaraApiRuntimeHostingStartup))]

namespace Vistara.Api.Composition.Runtime;

public static class ApiRuntimeServiceCollectionExtensions
{
    public const string ServiceName = "vistara-api";

    /// <summary>
    /// Registers the API health probes and OpenTelemetry runtime so hosts get
    /// startup, readiness, and liveness reporting with matching telemetry.
    /// </summary>
    public static IServiceCollection AddVistaraApiRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddVistaraApiHealth();
        services.AddVistaraTelemetry(configuration, ServiceName);
        return services;
    }
}

public static class ApiRuntimeApplicationBuilderExtensions
{
    /// <summary>
    /// Installs request telemetry and the health endpoints ahead of the
    /// dependency-bound platform middleware.
    /// </summary>
    public static IApplicationBuilder UseVistaraApiRuntime(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.UseVistaraApiObservability();
        return application.UseVistaraApiHealth();
    }
}

public sealed class VistaraApiRuntimeHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureServices(
            (context, services) =>
                services.AddVistaraApiRuntime(context.Configuration));
    }
}
