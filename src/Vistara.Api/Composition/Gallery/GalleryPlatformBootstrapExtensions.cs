using Microsoft.Extensions.Configuration;
using Vistara.Api.Composition.Gallery;

namespace Vistara.Api.Composition.Platform;

public static class GalleryPlatformBootstrapExtensions
{
    // Program passes ConfigurationManager, allowing gallery services to join
    // the persistence bootstrap without a second top-level registration call.
    public static IServiceCollection AddVistaraApiPersistence(
        this IServiceCollection services,
        ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        PlatformServiceCollectionExtensions.AddVistaraApiPersistence(
            services,
            (IConfiguration)configuration);
        services.AddVistaraGallery(configuration);
        return services;
    }
}
