using Microsoft.AspNetCore.Http;

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

    public const string Prefix = "/api/v1/auth/oidc";

    /// <summary>
    /// The sign-in entry point. It is parameterized because it is Vistara's
    /// own route and is never registered with a provider; the reply URLs below
    /// are not.
    /// </summary>
    public const string StartPathTemplate = $"{Prefix}/{{{ProviderRouteParameter}}}/start";

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
