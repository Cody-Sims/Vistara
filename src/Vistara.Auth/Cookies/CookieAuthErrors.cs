using Vistara.Domain.Common;

namespace Vistara.Auth.Cookies;

public static class CookieAuthErrors
{
    public static readonly ResultError InvalidCredentials = ResultError.Unauthorized(
        "cookie_auth.invalid_credentials",
        "The credentials are invalid.");

    public static readonly ResultError InvalidSession = ResultError.Unauthorized(
        "cookie_auth.invalid_session",
        "The browser session is invalid.");

    public static readonly ResultError TenantUnavailable = ResultError.Forbidden(
        "cookie_auth.tenant_unavailable",
        "The requested tenant is unavailable.");

    public static readonly ResultError AntiforgeryRequired = ResultError.Forbidden(
        "cookie_auth.antiforgery_required",
        "A valid antiforgery token is required.");
}
