using System.Text;

namespace Vistara.Auth.Oidc;

/// <summary>
/// Builds the browser-facing authorization request. The builder re-checks the
/// discovered authorization endpoint against the configured authority so a
/// cached or tampered metadata projection cannot send a user to a look-alike
/// consent screen, and it carries only the parameters the code flow needs: the
/// code verifier and the return target stay on the server.
/// </summary>
public static class OidcAuthorizationUrlBuilder
{
    public static Uri Build(
        OidcProviderOptions options,
        OidcProviderMetadata metadata,
        OidcLoginHandle handle)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(handle);

        if (!options.IsAllowedEndpoint(metadata.AuthorizationEndpoint) ||
            !string.IsNullOrEmpty(metadata.AuthorizationEndpoint.Query))
        {
            throw new ArgumentException(
                "The authorization endpoint is not an approved endpoint of the configured authority.",
                nameof(metadata));
        }

        var builder = new StringBuilder(
            metadata.AuthorizationEndpoint.GetLeftPart(UriPartial.Path));
        builder.Append('?');
        Append(builder, "client_id", options.ClientId, first: true);
        Append(builder, "response_type", "code");
        Append(builder, "response_mode", "query");
        Append(builder, "redirect_uri", options.RedirectUri.AbsoluteUri);
        Append(builder, "scope", options.ScopeParameter);
        Append(builder, "state", handle.State);
        Append(builder, "nonce", handle.Nonce);
        Append(builder, "code_challenge", handle.CodeChallenge);
        Append(builder, "code_challenge_method", handle.CodeChallengeMethod);
        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private static void Append(
        StringBuilder builder,
        string name,
        string value,
        bool first = false)
    {
        if (!first)
        {
            builder.Append('&');
        }

        builder
            .Append(Uri.EscapeDataString(name))
            .Append('=')
            .Append(Uri.EscapeDataString(value));
    }
}
