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
using Vistara.Persistence.Identity;

namespace Vistara.Api.Features.Account;

public static class AccountServiceCollectionExtensions
{
    /// <summary>
    /// Registers every dependency the browser session, current-principal, and
    /// first-owner provisioning surface needs on top of
    /// <c>AddVistaraPersistence</c>. Registrations use try-add semantics so a
    /// composition root that already supplies a service keeps it.
    /// </summary>
    public static IServiceCollection AddVistaraAccountSurface(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IAccountAuthorizationPort, ClaimsAccountAuthorizationPort>();
        services.TryAddSingleton<IPlatformRateLimitHook, PermitAllPlatformRateLimitHook>();
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IUuid7Generator, Uuid7Generator>();
        services.TryAddSingleton(new CookieAuthOptions());
        services.TryAddSingleton<ICookieTokenSource, CryptographicCookieTokenSource>();
        services.TryAddSingleton<ILocalPasswordHasher, Pbkdf2LocalPasswordHasher>();
        services.TryAddSingleton<DummyLocalPasswordVerifier>();
        services.TryAddSingleton<
            IFirstOwnerProvisioningGuard,
            NoOpFirstOwnerProvisioningGuard>();
        services.TryAddScoped<TenantFactory>();
        services.TryAddScoped<IdentityFactory>();
        services.TryAddScoped<RelationalIdentityCatalog>();
        services.TryAddScoped<RelationalFirstOwnerProvisioningStore>();
        services.TryAddScoped<PlatformLoginSessionFactory>();
        services.TryAddScoped<
            ILocalCredentialVerifier,
            PlatformLocalCredentialVerifier>();
        services.TryAddScoped<IBrowserSessionPort, PlatformBrowserSessionAdapter>();
        services.TryAddScoped<RelationalUserPreferenceStore>();
        services.TryAddScoped<IUserPreferencesPort, PlatformUserPreferencesAdapter>();
        services.TryAddScoped<
            IFirstOwnerProvisioningPort,
            PlatformFirstOwnerProvisioningAdapter>();
        return services;
    }
}

public static class AccountEndpointMapping
{
    public const string PolicyName = "Vistara.Account";

    /// <summary>Routes that must remain reachable without any credential.</summary>
    public static IReadOnlyList<string> AnonymousRoutes { get; } =
    [
        "/api/v1/auth/login",
        "/api/v1/auth/logout",
        "/api/v1/setup",
    ];

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
        endpoints.MapGet(
                "/api/v1/setup",
                (HttpContext context, CancellationToken cancellationToken) =>
                    AccountEndpoint.DescribeSetupAsync(
                        context,
                        context.RequestServices
                            .GetRequiredService<IFirstOwnerProvisioningPort>(),
                        context.RequestServices
                            .GetRequiredService<IPlatformRateLimitHook>(),
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
                "/api/v1/me/preferences",
                (HttpContext context, CancellationToken cancellationToken) =>
                    UserPreferencesEndpoint.GetAsync(
                        context,
                        Authorization(context),
                        Preferences(context),
                        cancellationToken))
            .RequireAuthorization(PolicyName);
        endpoints.MapPatch(
                "/api/v1/me/preferences",
                (HttpContext context, CancellationToken cancellationToken) =>
                    UserPreferencesEndpoint.PatchAsync(
                        context,
                        Authorization(context),
                        Preferences(context),
                        cancellationToken))
            .RequireAuthorization(PolicyName);
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

    private static IAccountAuthorizationPort Authorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAccountAuthorizationPort>();

    private static IUserPreferencesPort Preferences(HttpContext context) =>
        context.RequestServices.GetRequiredService<IUserPreferencesPort>();

    private static IBrowserSessionPort Sessions(HttpContext context) =>
        context.RequestServices.GetRequiredService<IBrowserSessionPort>();

    private static CookieAuthOptions Cookies(HttpContext context) =>
        context.RequestServices.GetRequiredService<CookieAuthOptions>();
}
