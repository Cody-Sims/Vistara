using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Vistara.Api.Features;
using Vistara.Auth.Cookies;
using Vistara.Contracts.Identity;
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

        // Routing already refused anything that is not a provider key. The
        // check is repeated here so a future caller that binds this handler to
        // an unconstrained route still cannot hand an arbitrary segment to the
        // registry, the adapter, or the audit sink.
        if (!OidcRoutes.IsProviderKey(providerId))
        {
            WriteNotFound(context);
            return;
        }

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
    /// The provider-initiated front-channel sign-out reply URL.
    ///
    /// This endpoint deliberately does nothing. Entra drives it as a plain GET
    /// inside a third-party iframe, and the Vistara session cookie is
    /// <c>SameSite=Lax</c>, so the request arrives with no session cookie at
    /// all: there is nothing here to revoke and no way to learn whose session
    /// to revoke. Anything that looked like revocation would either be a
    /// no-op dressed up as success, or - if it accepted a session identifier
    /// from the query string - an unauthenticated revocation oracle. Neither
    /// is acceptable, and loosening <c>SameSite</c> to make the cookie arrive
    /// would reintroduce the cross-site request forgery exposure the cookie
    /// policy exists to prevent.
    ///
    /// It therefore answers <c>200</c> with no body, sets no cookie, touches
    /// no session, and records no audit event, so it cannot report a sign-out
    /// that did not happen. Revocation is
    /// <c>POST /api/v1/auth/logout</c> and the relying-party initiated
    /// <c>POST /api/v1/auth/oidc/{providerId}/sign-out</c>, both of which are
    /// same-site, carry the session cookie, and are covered by the antiforgery
    /// policy.
    ///
    /// The route is kept only so an already-deployed registration does not
    /// break. It must not be registered as the Entra <c>web.logoutUrl</c>:
    /// registering a control that cannot act is worse than registering none.
    /// </summary>
    public static Task FrontChannelLogoutAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Relying-party initiated sign-out: revoke the Vistara session first, then
    /// tell the caller where the provider session can be ended.
    ///
    /// It is a POST from the application itself, so unlike the front-channel
    /// reply URL it actually receives the session cookie and is covered by the
    /// antiforgery policy. The end-session URL is built from discovered
    /// provider metadata and the reply URL the operator registered; nothing
    /// from this request contributes to it.
    /// </summary>
    public static async Task SignOutAsync(
        HttpContext context,
        IOidcSignOutPort signOut,
        CookieAuthOptions cookies,
        string providerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(signOut);
        ArgumentNullException.ThrowIfNull(cookies);

        if (ReadSessionToken(context, cookies) is not { } sessionToken)
        {
            // Sign-out acts on the session the caller presents. With none
            // presented there is nothing to act on, and answering anyway would
            // turn this into an anonymous endpoint that reports provider
            // configuration.
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "auth.unauthenticated",
                "A browser session is required to sign out.",
                cancellationToken);
            return;
        }

        OidcSignOutResult result = await signOut.SignOutAsync(
            providerId,
            sessionToken,
            cancellationToken);
        context.Response.Headers.Append(HeaderNames.SetCookie, result.SetCookieHeader);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new SignOutResponse(result.EndSessionUrl)),
            cancellationToken);
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

    /// <summary>
    /// The answer to a route segment that cannot name a provider: a plain 404
    /// with no body and nothing recorded. Echoing the segment, or auditing it,
    /// would put attacker-chosen bytes into an operator-facing record for a
    /// request that never reached any Vistara state.
    /// </summary>
    private static void WriteNotFound(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentLength = 0;
    }

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
/// Revokes the Vistara browser session for a relying-party initiated sign-out
/// and reports where the provider session may be ended. The caller has already
/// proved it holds the session by presenting the cookie, so the port takes the
/// raw token rather than an ambient principal.
/// </summary>
public interface IOidcSignOutPort
{
    ValueTask<OidcSignOutResult> SignOutAsync(
        string providerId,
        string sessionToken,
        CancellationToken cancellationToken);
}
