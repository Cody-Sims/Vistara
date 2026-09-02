using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Auth.Cookies;

namespace Vistara.Api.Features.Oidc;

/// <summary>
/// Maps the hosted OpenID Connect browser routes.
///
/// All four are anonymous by design and by necessity: three of them are reply
/// URLs a provider drives with no Vistara credential, and the fourth starts a
/// sign-in for a visitor who has none. Each is registered individually with an
/// explicit method, so nothing else on the API becomes anonymous with them.
/// </summary>
public static class OidcEndpointMapping
{
    public static IEndpointRouteBuilder MapVistaraOidcAuthentication(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

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
        endpoints.MapGet(
                OidcRoutes.FrontChannelLogoutPath,
                (HttpContext context, CancellationToken cancellationToken) =>
                    OidcAuthenticationEndpoint.FrontChannelLogoutAsync(
                        context,
                        context.RequestServices.GetRequiredService<IOidcLogoutPort>(),
                        Cookies(context),
                        cancellationToken))
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
