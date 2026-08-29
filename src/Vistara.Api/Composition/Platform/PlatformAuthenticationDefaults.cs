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
    public static string Select(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

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
}
