using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Application.Common;
using Vistara.Application.Tenancy;
using Vistara.Persistence.Tenancy;

namespace Vistara.Api.Features.Tenants;

public static class TenantServiceCollectionExtensions
{
    /// <summary>
    /// Registers tenant discovery and member administration on top of the
    /// existing tenancy repositories, factory, and audit writer.
    /// </summary>
    public static IServiceCollection AddVistaraTenantAdministration(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IAccountAuthorizationPort, ClaimsAccountAuthorizationPort>();
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IUuid7Generator, Uuid7Generator>();
        services.TryAddScoped<TenantFactory>();
        services.TryAddScoped<RelationalTenantDirectory>();
        services.TryAddScoped<ITenantDirectoryPort, PlatformTenantDirectoryAdapter>();
        return services;
    }
}

public static class TenantEndpointMapping
{
    public const string PolicyName = "Vistara.Tenants";

    public static IEndpointRouteBuilder MapVistaraTenants(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints.MapGet(
            "/api/v1/tenants",
            (HttpContext context, CancellationToken cancellationToken) =>
                TenantEndpoint.ListTenantsAsync(
                    context,
                    Authorization(context),
                    Directory(context),
                    cancellationToken)));
        Map(endpoints.MapGet(
            "/api/v1/tenants/{tenantId:guid}/members",
            (
                Guid tenantId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                TenantEndpoint.ListMembersAsync(
                    context,
                    tenantId,
                    Authorization(context),
                    Directory(context),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/tenants/{tenantId:guid}/members",
            (
                Guid tenantId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                TenantEndpoint.InviteMemberAsync(
                    context,
                    tenantId,
                    Authorization(context),
                    Directory(context),
                    cancellationToken)));
        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(PolicyName);

    private static IAccountAuthorizationPort Authorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAccountAuthorizationPort>();

    private static ITenantDirectoryPort Directory(HttpContext context) =>
        context.RequestServices.GetRequiredService<ITenantDirectoryPort>();
}
