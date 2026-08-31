using Microsoft.AspNetCore.Http;
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
    /// Bootstrap routes that must authenticate anonymously. A browser can
    /// legitimately hold a stale, revoked, or wrong-tenant session cookie while
    /// signing in, signing out, or provisioning the first owner, and that
    /// cookie must never turn those requests into a challenge.
    /// </summary>
    private static readonly string[] AnonymousBootstrapPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/auth/logout",
        "/api/v1/setup",
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

    internal static bool IsAnonymousBootstrapRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        AnonymousBootstrapPaths.Any(path =>
            request.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static bool IsPublicMediaRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
        request.Path.StartsWithSegments("/media");
}
