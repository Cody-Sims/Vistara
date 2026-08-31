using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Admin;
using Vistara.Api.Features.ApiKeys;
using Vistara.Api.Features.Capabilities;
using Vistara.Api.Features.Jobs;
using Vistara.Api.Features.Tenants;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Composes the platform and account surface: capability discovery, job
/// status, API key administration, tenant and member administration, browser
/// sessions, and one-time first-owner provisioning. A composition root needs
/// only <see cref="AddVistaraPlatformSurface"/> and
/// <see cref="MapVistaraPlatformSurface"/>.
/// </summary>
public static class PlatformSurfaceServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraPlatformSurface(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddVistaraCapabilities();
        services.AddVistaraJobStatus();
        services.AddVistaraApiKeyAdministration();
        services.AddVistaraTenantAdministration();
        services.AddVistaraAccountSurface();
        services.AddVistaraAdministration();
        services.AddVistaraPlatformSurfacePolicies();
        return services;
    }

    /// <summary>
    /// Registers the authorization policies referenced by the platform and
    /// account routes. Object-level and scope checks stay in each feature's
    /// authorization port, so the policies only require an authenticated
    /// principal.
    /// </summary>
    public static IServiceCollection AddVistaraPlatformSurfacePolicies(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            foreach (string policy in PlatformSurfacePolicies.All)
            {
                options.AddPolicy(
                    policy,
                    builder => builder.RequireAuthenticatedUser());
            }
        });
        return services;
    }
}

public static class PlatformSurfacePolicies
{
    public static IReadOnlyList<string> All { get; } =
    [
        CapabilitiesEndpointMapping.PolicyName,
        JobStatusEndpointMapping.PolicyName,
        ApiKeyEndpointMapping.PolicyName,
        TenantEndpointMapping.PolicyName,
        AccountEndpointMapping.PolicyName,
        AdminEndpointMapping.PolicyName,
    ];
}

public static class PlatformSurfaceEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapVistaraPlatformSurface(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapVistaraCapabilities();
        endpoints.MapVistaraJobStatus();
        endpoints.MapVistaraApiKeys();
        endpoints.MapVistaraTenants();
        endpoints.MapVistaraAccount();
        endpoints.MapVistaraAdministration();
        return endpoints;
    }
}
