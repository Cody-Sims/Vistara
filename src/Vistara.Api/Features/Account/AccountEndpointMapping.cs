using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Api.Composition.Platform;
using Vistara.Application.Common;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.Cookies;

namespace Vistara.Api.Features.Account;

public static class AccountServiceCollectionExtensions
{
    /// <summary>
    /// Registers browser session, current-principal, and first-owner
    /// provisioning defaults using try-add semantics.
    /// </summary>
    public static IServiceCollection AddVistaraAccountSurface(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IAccountAuthorizationPort, ClaimsAccountAuthorizationPort>();
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IUuid7Generator, Uuid7Generator>();
        services.TryAddScoped<TenantFactory>();
        services.TryAddScoped<IdentityFactory>();
        services.TryAddSingleton<ILocalPasswordHasher, Pbkdf2LocalPasswordHasher>();
        services.TryAddSingleton(new CookieAuthOptions());
        services.TryAddScoped<
            ILocalCredentialVerifier,
            PlatformLocalCredentialVerifier>();
        services.TryAddScoped<IBrowserSessionPort, PlatformBrowserSessionAdapter>();
        services.TryAddScoped<
            IFirstOwnerProvisioningPort,
            PlatformFirstOwnerProvisioningAdapter>();
        return services;
    }
}

public static class AccountEndpointMapping
{
    public const string PolicyName = "Vistara.Account";

    public static IEndpointRouteBuilder MapVistaraAccount(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/api/v1/auth/login",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AccountEndpoint.LoginAsync(
                        context,
                        Sessions(context),
                        Cookies(context),
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapPost(
                "/api/v1/auth/logout",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AccountEndpoint.LogoutAsync(
                        context,
                        Sessions(context),
                        Cookies(context),
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapPost(
                "/api/v1/setup",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AccountEndpoint.ProvisionFirstOwnerAsync(
                        context,
                        context.RequestServices
                            .GetRequiredService<IFirstOwnerProvisioningPort>(),
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapGet(
                "/api/v1/me",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AccountEndpoint.GetCurrentUserAsync(
                        context,
                        context.RequestServices
                            .GetRequiredService<IAccountAuthorizationPort>(),
                        Sessions(context),
                        Cookies(context),
                        cancellationToken))
            .RequireAuthorization(PolicyName);
        return endpoints;
    }

    private static IBrowserSessionPort Sessions(HttpContext context) =>
        context.RequestServices.GetRequiredService<IBrowserSessionPort>();

    private static CookieAuthOptions Cookies(HttpContext context) =>
        context.RequestServices.GetRequiredService<CookieAuthOptions>();
}
