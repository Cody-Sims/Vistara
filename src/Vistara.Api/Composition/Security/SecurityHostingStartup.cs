using Microsoft.AspNetCore.Hosting;

[assembly: HostingStartup(
    typeof(Vistara.Api.Composition.Security.VistaraSecurityHostingStartup))]

namespace Vistara.Api.Composition.Security;

public sealed class VistaraSecurityHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureServices(
            (context, services) => services.AddVistaraApiSecurity(
                context.Configuration,
                context.HostingEnvironment));
    }
}
