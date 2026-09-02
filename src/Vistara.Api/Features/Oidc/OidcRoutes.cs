using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Vistara.Api.Features.Oidc;

/// <summary>
/// The hosted OpenID Connect route contract.
///
/// The callback, front-channel logout, and signed-out paths are frozen: they
/// are registered with Entra by
/// <c>deploy/azure/infra/entra/app-registration.bicep</c> and asserted against
/// the deployed application, so the API must serve exactly these paths.
/// <c>eng/tests/fixtures/azure-graph-registration/hosted-oidc-routes.json</c>
/// is the shared source of truth; changing a value here is a breaking change
/// for the Entra registration and the deployment verification alike.
/// </summary>
public static class OidcRoutes
{
    /// <summary>The only provider key the hosted entry point supports.</summary>
    public const string EntraProviderId = "entra";

    public const string ProviderRouteParameter = "providerId";

    /// <summary>
    /// The inline route constraint name that binds the provider segment to
    /// <see cref="IsProviderKey"/>. Routing rejects anything else, so a
    /// hostile segment never reaches a handler, an adapter, or the audit sink.
    /// </summary>
    public const string ProviderKeyConstraintName = "vistaraOidcProviderKey";

    /// <summary>
    /// The fixed token recorded in place of a provider key that is not one.
    /// Audit records are operator-facing text, and attacker-chosen bytes must
    /// never reach them.
    /// </summary>
    public const string UnknownProviderAuditToken = "(not-a-provider-key)";

    public const string Prefix = "/api/v1/auth/oidc";

    private const string ConstrainedProviderParameter =
        $"{{{ProviderRouteParameter}:{ProviderKeyConstraintName}}}";

    /// <summary>
    /// The sign-in entry point. It is parameterized because it is Vistara's
    /// own route and is never registered with a provider; the reply URLs below
    /// are not.
    /// </summary>
    public const string StartPathTemplate =
        $"{Prefix}/{ConstrainedProviderParameter}/start";

    /// <summary>
    /// Relying-party initiated sign-out. It is a POST because it revokes the
    /// browser session, which means it is same-site, carries the session
    /// cookie, and is covered by the antiforgery policy - none of which is
    /// true of the front-channel reply URL.
    /// </summary>
    public const string SignOutPathTemplate =
        $"{Prefix}/{ConstrainedProviderParameter}/sign-out";

    public const string CallbackPath = $"{Prefix}/{EntraProviderId}/callback";

    public const string FrontChannelLogoutPath =
        $"{Prefix}/{EntraProviderId}/frontchannel-logout";

    public const string SignedOutPath = $"{Prefix}/{EntraProviderId}/signed-out";

    private const string StartSuffix = "/start";

    /// <summary>The maximum length of a provider key in the start route.</summary>
    private const int MaximumProviderIdLength = 64;

    /// <summary>
    /// The reply URLs registered with the identity provider, paired with the
    /// only method the provider may use to reach them.
    /// </summary>
    public static IReadOnlyList<OidcRoute> ProviderReplyRoutes { get; } =
    [
        new(HttpMethods.Get, CallbackPath),
        new(HttpMethods.Get, FrontChannelLogoutPath),
        new(HttpMethods.Get, SignedOutPath),
    ];

    public static string StartPath(string providerId) =>
        $"{Prefix}/{providerId}{StartSuffix}";

    public static string SignOutPath(string providerId) =>
        $"{Prefix}/{providerId}/sign-out";

    /// <summary>
    /// Renders a provider key for an operator-facing audit record. A value
    /// that is not a provider key is replaced outright rather than escaped,
    /// because there is nothing an attacker-chosen segment could usefully tell
    /// a reader that its rejection does not already say.
    /// </summary>
    public static string ForAudit(string? providerId) =>
        IsProviderKey(providerId) ? providerId! : UnknownProviderAuditToken;

    /// <summary>
    /// Matches <c>/api/v1/auth/oidc/{providerId}/start</c> for exactly one
    /// bounded, URL-safe provider segment. A nested or empty segment is not the
    /// start route and stays authenticated.
    /// </summary>
    public static bool IsStartPath(PathString path)
    {
        if (!path.HasValue ||
            !path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase, out PathString rest) ||
            !rest.HasValue)
        {
            return false;
        }

        string remainder = rest.Value!;
        if (!remainder.EndsWith(StartSuffix, StringComparison.OrdinalIgnoreCase) ||
            remainder.Length < StartSuffix.Length + 2)
        {
            return false;
        }

        ReadOnlySpan<char> provider = remainder.AsSpan(
            1,
            remainder.Length - StartSuffix.Length - 1);
        return IsProviderSegment(provider);
    }

    /// <summary>
    /// The provider key vocabulary shared by the route, the configuration, and
    /// the login request store: ASCII letters, digits, hyphen, and underscore.
    /// </summary>
    public static bool IsProviderKey(string? providerId) =>
        providerId is not null && IsProviderSegment(providerId.AsSpan());

    private static bool IsProviderSegment(ReadOnlySpan<char> provider)
    {
        if (provider.Length is 0 or > MaximumProviderIdLength)
        {
            return false;
        }

        foreach (char character in provider)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One route of the hosted OIDC contract, method and path together.</summary>
public sealed record OidcRoute(string Method, string Path);

/// <summary>
/// The route constraint behind <see cref="OidcRoutes.ProviderKeyConstraintName"/>.
///
/// It exists so the provider segment is judged by the routing layer, before
/// any handler, provider registry, sign-in adapter, or audit sink can observe
/// it. A segment that is not a provider key produces an ordinary 404 with no
/// record of what was attempted, which is the only honest answer: a route that
/// cannot exist has nothing to report.
/// </summary>
public sealed class OidcProviderKeyRouteConstraint : IRouteConstraint
{
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        ArgumentNullException.ThrowIfNull(routeKey);
        ArgumentNullException.ThrowIfNull(values);
        return values.TryGetValue(routeKey, out object? value) &&
            OidcRoutes.IsProviderKey(value as string);
    }
}
