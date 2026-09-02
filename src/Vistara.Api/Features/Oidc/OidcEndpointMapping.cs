using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vistara.Auth.Cookies;

namespace Vistara.Api.Features.Oidc;

/// <summary>
/// Maps the hosted OpenID Connect browser routes.
///
/// All four are anonymous by design and by necessity: three of them are reply
/// URLs a provider drives with no Vistara credential, and the fourth starts a
/// sign-in for a visitor who has none. Each is registered individually with an
/// explicit method, so nothing else on the API becomes anonymous with them.
///
/// A deployment with no configured provider maps nothing at all. There is no
/// reply URL registered with anyone, no visitor can start a sign-in, and the
/// smallest anonymous surface is the one that does not exist.
///
/// Relying-party initiated sign-out is mapped alongside them but is not one of
/// them: it is a POST, it is authenticated by the browser session it revokes,
/// and it is covered by the antiforgery policy.
/// </summary>
public static class OidcEndpointMapping
{
    /// <summary>
    /// Registers the provider-key route constraint. Routing has to be able to
    /// resolve it before any hosted route is mapped, which is why it is a
    /// service-collection concern rather than something the mapping can do.
    /// </summary>
    public static IServiceCollection AddVistaraOidcRouting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<RouteOptions>(static options =>
            options.ConstraintMap[OidcRoutes.ProviderKeyConstraintName] =
                typeof(OidcProviderKeyRouteConstraint));
        return services;
    }

    public static IEndpointRouteBuilder MapVistaraOidcAuthentication(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.ServiceProvider.GetService<IOidcProviderCatalog>()
            is not { Providers.Count: > 0 })
        {
            return endpoints;
        }

        // Without the constraint the provider segment would be unbounded, and
        // routing would hand an arbitrary run of bytes to the sign-in handler.
        // Failing here names the missing registration; failing later would not.
        if (!endpoints.ServiceProvider
                .GetRequiredService<IOptions<RouteOptions>>()
                .Value
                .ConstraintMap
                .TryGetValue(
                    OidcRoutes.ProviderKeyConstraintName,
                    out Type? constraint) ||
            constraint != typeof(OidcProviderKeyRouteConstraint))
        {
            throw new InvalidOperationException(
                $"The '{OidcRoutes.ProviderKeyConstraintName}' route constraint is not "
                + $"registered. Call '{nameof(AddVistaraOidcRouting)}' before mapping "
                + "the hosted OpenID Connect routes.");
        }

        endpoints.MapGet(
                OidcRoutes.StartPathTemplate,
                (HttpContext context, string providerId, CancellationToken cancellationToken) =>
                    OidcAuthenticationEndpoint.StartAsync(
                        context,
                        Login(context),
                        providerId,
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapGet(
                OidcRoutes.CallbackPath,
                (HttpContext context, CancellationToken cancellationToken) =>
                    OidcAuthenticationEndpoint.CallbackAsync(
                        context,
                        Login(context),
                        Cookies(context),
                        OidcRoutes.EntraProviderId,
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapPost(
                OidcRoutes.SignOutPathTemplate,
                (HttpContext context, string providerId, CancellationToken cancellationToken) =>
                    OidcAuthenticationEndpoint.SignOutAsync(
                        context,
                        context.RequestServices.GetRequiredService<IOidcSignOutPort>(),
                        Cookies(context),
                        providerId,
                        cancellationToken));

        // Deliberately not anonymous-listed and deliberately inert: see
        // OidcAuthenticationEndpoint.FrontChannelLogoutAsync. It answers an
        // unauthenticated cross-site GET, so it must be reachable without a
        // credential, but it changes nothing.
        endpoints.MapGet(
                OidcRoutes.FrontChannelLogoutPath,
                OidcAuthenticationEndpoint.FrontChannelLogoutAsync)
            .AllowAnonymous();
        endpoints.MapGet(
                OidcRoutes.SignedOutPath,
                OidcAuthenticationEndpoint.SignedOutAsync)
            .AllowAnonymous();
        return endpoints;
    }

    private static IOidcLoginPort Login(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOidcLoginPort>();

    private static CookieAuthOptions Cookies(HttpContext context) =>
        context.RequestServices.GetRequiredService<CookieAuthOptions>();
}
