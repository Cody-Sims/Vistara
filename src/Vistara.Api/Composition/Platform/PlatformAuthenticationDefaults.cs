using Microsoft.AspNetCore.Http;
using Vistara.Api.Features.Oidc;
using Vistara.Auth.Cookies;

namespace Vistara.Api.Composition.Platform;

public static class PlatformAuthenticationDefaults
{
    public const string SelectorScheme = "Vistara";
    public const string CookieScheme = "Vistara.Cookie";
    public const string ApiKeyScheme = "Vistara.ApiKey";
    public const string BearerScheme = "Vistara.Bearer";
    public const string ConfusedScheme = "Vistara.Confused";
    public const string AnonymousScheme = "Vistara.Anonymous";
    public const string ApiKeyHeaderName = "X-API-Key";
    public const string SchemeConfusionCode = "authentication.scheme_confusion";
}

public static class PlatformAuthenticationSelector
{
    /// <summary>
    /// Bootstrap routes that must authenticate anonymously, each pinned to the
    /// one method it is reachable by. A browser can legitimately hold a stale,
    /// revoked, or wrong-tenant session cookie while signing in, signing out,
    /// or provisioning the first owner, and that cookie must never turn those
    /// requests into a challenge. Pairing the method with the path is what
    /// keeps this list from making any other request on the same path
    /// anonymous. The hosted reply URLs come from the frozen route contract
    /// rather than being repeated here, so the allowlist cannot drift from the
    /// paths the API actually serves.
    /// </summary>
    private static readonly OidcRoute[] AnonymousBootstrapRoutes =
    [
        new(HttpMethods.Post, "/api/v1/auth/login"),
        new(HttpMethods.Post, "/api/v1/auth/logout"),
        new(HttpMethods.Post, "/api/v1/setup"),
        .. OidcRoutes.ProviderReplyRoutes,
    ];

    public static string Select(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsPublicMediaRequest(request) || IsAnonymousBootstrapRequest(request))
        {
            return PlatformAuthenticationDefaults.AnonymousScheme;
        }

        bool hasAuthorization = request.Headers.ContainsKey("Authorization");
        bool hasApiKey = request.Headers.ContainsKey(
            PlatformAuthenticationDefaults.ApiKeyHeaderName);
        bool hasCookie = request.Cookies.ContainsKey(
            CookieAuthOptions.ProductionCookieName);
        int credentialCount =
            Convert.ToInt32(hasAuthorization) +
            Convert.ToInt32(hasApiKey) +
            Convert.ToInt32(hasCookie);

        if (credentialCount > 1)
        {
            return PlatformAuthenticationDefaults.ConfusedScheme;
        }

        if (hasAuthorization)
        {
            return PlatformAuthenticationDefaults.BearerScheme;
        }

        if (hasApiKey)
        {
            return PlatformAuthenticationDefaults.ApiKeyScheme;
        }

        return hasCookie
            ? PlatformAuthenticationDefaults.CookieScheme
            : PlatformAuthenticationDefaults.AnonymousScheme;
    }

    /// <summary>
    /// The hosted OIDC routes are reply URLs a provider drives and a sign-in
    /// entry point a visitor has no credential for, so they authenticate
    /// anonymously by method and path. The start route is matched structurally
    /// because its provider segment is a route parameter; every other GET,
    /// including any other GET under the same prefix, keeps its scheme.
    /// </summary>
    internal static bool IsAnonymousBootstrapRequest(HttpRequest request) =>
        AnonymousBootstrapRoutes.Any(route =>
            string.Equals(request.Method, route.Method, StringComparison.OrdinalIgnoreCase) &&
            request.Path.Equals(route.Path, StringComparison.OrdinalIgnoreCase)) ||
        (HttpMethods.IsGet(request.Method) && OidcRoutes.IsStartPath(request.Path));

    private static bool IsPublicMediaRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
        request.Path.StartsWithSegments("/media");
}
