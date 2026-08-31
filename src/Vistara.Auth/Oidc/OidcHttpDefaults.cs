using System.Net;

namespace Vistara.Auth.Oidc;

/// <summary>
/// The transport contract every OIDC provider call must be composed with.
///
/// A composition root MUST register the OIDC <see cref="HttpClient"/> with a
/// handler built by <see cref="CreateHandler"/>, or with an equivalent handler
/// that disables automatic redirects. Redirect following is a real
/// server-side request forgery primitive here: discovery, JWKS, and token
/// endpoints are validated against the configured authority before the request
/// is issued, and a followed redirect would move the actual request to a URL
/// that never passed that check.
///
/// The library does not trust that registration on its own. Every response is
/// additionally checked against the exact URL that was requested, so a client
/// composed with redirects enabled fails closed instead of silently following.
/// Both layers are required: the handler prevents the request, and the
/// response check catches a misconfigured client.
/// </summary>
public static class OidcHttpDefaults
{
    /// <summary>
    /// The name a composition root should use when registering the OIDC
    /// <see cref="HttpClient"/>.
    /// </summary>
    public const string HttpClientName = "vistara-oidc";

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Builds the handler the OIDC client requires: no automatic redirects, no
    /// cookies, no proxy-supplied credentials, and no ambient authentication.
    /// A provider call carries its own client credential in the request body
    /// and must never pick one up from the transport.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = ConnectTimeout,
            Credentials = null,
            PooledConnectionLifetime = PooledConnectionLifetime,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false,
        };
}

/// <summary>
/// Confirms that a response came from the exact URL that was requested and
/// validated.
/// </summary>
internal static class OidcRequestIntegrity
{
    /// <summary>
    /// A redirect rewrites <see cref="HttpResponseMessage.RequestMessage"/> to
    /// the final URL, so comparing it against the URL the caller validated
    /// detects any hop, whether it landed off-host or back on the same host.
    /// Comparison follows RFC 3986 case rules: scheme and host are compared
    /// case-insensitively, path and query ordinally, and userinfo is refused
    /// outright rather than normalized away.
    /// </summary>
    internal static bool CameFromRequestedUri(HttpResponseMessage response, Uri requested)
    {
        Uri? actual = response.RequestMessage?.RequestUri;
        return actual is not null &&
            actual.IsAbsoluteUri &&
            requested.IsAbsoluteUri &&
            string.IsNullOrEmpty(actual.UserInfo) &&
            string.IsNullOrEmpty(requested.UserInfo) &&
            string.Equals(actual.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(actual.Host, requested.Host, StringComparison.OrdinalIgnoreCase) &&
            actual.Port == requested.Port &&
            string.Equals(actual.AbsolutePath, requested.AbsolutePath, StringComparison.Ordinal) &&
            string.Equals(actual.Query, requested.Query, StringComparison.Ordinal);
    }
}
