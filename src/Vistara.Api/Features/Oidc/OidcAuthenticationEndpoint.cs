using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;

namespace Vistara.Api.Features.Oidc;

/// <summary>
/// The short-lived cookie that binds one browser to one in-flight
/// authorization request.
///
/// It is a host cookie: the <c>__Host-</c> prefix makes a conforming browser
/// refuse it unless it is <c>Secure</c>, carries no <c>Domain</c>, and is
/// path-scoped to the origin root, which is exactly what stops a neighbouring
/// subdomain from planting a login handle for this origin. <c>SameSite=Lax</c>
/// is required rather than preferred: the callback is a top-level cross-site
/// navigation from the identity provider, and a stricter value would drop the
/// cookie and break every sign-in.
///
/// The value carries no session authority. It is a Data Protection payload
/// that only the API can read, it expires with the login request, and it is
/// deleted the moment the callback completes.
/// </summary>
public static class OidcHandleCookie
{
    public const string Name = "__Host-vistara-oidc";

    public static string ToSetCookieHeader(string value, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if (value.Any(character =>
                character <= 0x20 || character >= 0x7f || character is ';' or ','))
        {
            throw new ArgumentException(
                "The login handle cookie value is not a valid cookie octet sequence.",
                nameof(value));
        }

        long seconds = checked((long)lifetime.TotalSeconds);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}={value}; Path=/; Max-Age={seconds}; Secure; HttpOnly; SameSite=Lax");
    }

    public static string DeletionHeader { get; } =
        $"{Name}=; Path=/; Max-Age=0; Secure; HttpOnly; SameSite=Lax";

    public static string? Read(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Cookies.TryGetValue(Name, out string? value) &&
            !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}

/// <summary>
/// The single browser-visible failure vocabulary for hosted sign-in. Every
/// failure - a cancelled consent, a replayed state, a rejected directory, an
/// unreachable provider - produces the same code, so the redirect cannot be
/// used as an oracle. The detail lives only in the server-side audit record.
/// </summary>
public static class OidcBrowserOutcome
{
    public const string SignInPath = "/login";

    public const string FailureCode = "oidc_sign_in_failed";

    public static string FailureLocation { get; } =
        $"{SignInPath}?error={FailureCode}";
}

/// <summary>
/// The hosted OpenID Connect browser surface: sign-in start, the provider
/// callback, provider-initiated front-channel sign-out, and the landing route
/// a provider returns to after sign-out. Every response is a redirect or an
/// empty body; no endpoint here ever renders a token, a code, a state, or a
/// provider error string.
/// </summary>
public static class OidcAuthenticationEndpoint
{
    public static async Task StartAsync(
        HttpContext context,
        IOidcLoginPort login,
        string providerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(login);

        Result<OidcStartResult> started = await login.StartAsync(
            providerId,
            ReadSingleQueryValue(context, "returnTo"),
            cancellationToken);
        if (!started.TryGetValue(out OidcStartResult? result))
        {
            // A sign-in that did not start must not leave the browser holding
            // a handle from an earlier attempt.
            context.Response.Headers.Append(
                HeaderNames.SetCookie,
                OidcHandleCookie.DeletionHeader);
            WriteFailureRedirect(context);
            return;
        }

        context.Response.Headers.Append(
            HeaderNames.SetCookie,
            result.HandleCookieValue);
        Redirect(context, result.AuthorizationUri.AbsoluteUri);
    }

    public static async Task CallbackAsync(
        HttpContext context,
        IOidcLoginPort login,
        CookieAuthOptions cookies,
        string providerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(cookies);

        Result<OidcSignInResult> completed = await login.CompleteAsync(
            new OidcCallbackCommand(
                providerId,
                ReadSingleQueryValue(context, "state"),
                ReadSingleQueryValue(context, "code"),
                ReadSingleQueryValue(context, "error"),
                OidcHandleCookie.Read(context),
                ReadSessionToken(context, cookies)),
            cancellationToken);

        // The login handle is single use whatever happened, so it is cleared on
        // both paths before anything else is written.
        context.Response.Headers.Append(
            HeaderNames.SetCookie,
            OidcHandleCookie.DeletionHeader);
        if (!completed.TryGetValue(out OidcSignInResult? result))
        {
            WriteFailureRedirect(context);
            return;
        }

        context.Response.Headers.Append(HeaderNames.SetCookie, result.SetCookieHeader);
        Redirect(context, result.ReturnTo);
    }

    /// <summary>
    /// Provider-initiated front-channel sign-out. Entra issues this as a plain
    /// GET from an iframe with no antiforgery token and no request body, so the
    /// endpoint can only ever revoke: it never grants, never redirects, and
    /// never reports whether a session existed. Repeating it is a no-op, which
    /// is what makes it safe to answer an unauthenticated cross-site GET.
    /// </summary>
    public static async Task FrontChannelLogoutAsync(
        HttpContext context,
        IOidcLogoutPort logout,
        CookieAuthOptions cookies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logout);
        ArgumentNullException.ThrowIfNull(cookies);

        string deletionHeader = await logout.SignOutAsync(
            ReadSessionToken(context, cookies),
            cancellationToken);
        context.Response.Headers.Append(HeaderNames.SetCookie, deletionHeader);
        context.Response.Headers.Append(
            HeaderNames.SetCookie,
            OidcHandleCookie.DeletionHeader);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = 0;
    }

    /// <summary>
    /// The landing route a provider returns to after sign-out. It is a
    /// registered reply URL, so it must exist, must accept an anonymous GET,
    /// and must send the visitor somewhere safe without reading anything the
    /// provider appended to the URL.
    /// </summary>
    public static Task SignedOutAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.Append(
            HeaderNames.SetCookie,
            OidcHandleCookie.DeletionHeader);
        Redirect(context, OidcBrowserOutcome.SignInPath);
        return Task.CompletedTask;
    }

    private static void WriteFailureRedirect(HttpContext context) =>
        Redirect(context, OidcBrowserOutcome.FailureLocation);

    private static void Redirect(HttpContext context, string location)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Location = location;
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.ContentLength = 0;
    }

    /// <summary>
    /// Reads a query parameter only when it appears exactly once. A repeated
    /// parameter is a smuggling attempt, not a value to pick a winner from.
    /// </summary>
    private static string? ReadSingleQueryValue(HttpContext context, string name)
    {
        if (!context.Request.Query.TryGetValue(name, out StringValues values) ||
            values.Count != 1)
        {
            return null;
        }

        string? value = values[0];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ReadSessionToken(
        HttpContext context,
        CookieAuthOptions cookies) =>
        context.Request.Cookies.TryGetValue(cookies.CookieName, out string? value) &&
            !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}

/// <summary>
/// Revokes the existing Vistara browser session and returns the cookie
/// deletion header. Front-channel sign-out must work with no credential of its
/// own, so the port takes the raw session token the request carried.
/// </summary>
public interface IOidcLogoutPort
{
    ValueTask<string> SignOutAsync(
        string? sessionToken,
        CancellationToken cancellationToken);
}
