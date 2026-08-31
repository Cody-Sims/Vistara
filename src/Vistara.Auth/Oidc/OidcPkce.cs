using System.Security.Cryptography;
using System.Text;

namespace Vistara.Auth.Oidc;

/// <summary>
/// RFC 7636 proof key for code exchange. Only the S256 method is produced or
/// accepted; the "plain" method offers no protection against an intercepted
/// authorization code.
/// </summary>
public static class OidcPkce
{
    public const string ChallengeMethod = "S256";
    public const int MinimumVerifierLength = 43;
    public const int MaximumVerifierLength = 128;

    public static string CreateChallenge(string codeVerifier)
    {
        if (!IsWellFormedVerifier(codeVerifier))
        {
            throw new ArgumentException(
                "A PKCE code verifier must be 43-128 unreserved characters.",
                nameof(codeVerifier));
        }

        byte[] material = Encoding.ASCII.GetBytes(codeVerifier);
        try
        {
            return OidcBase64Url.Encode(SHA256.HashData(material));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public static bool IsWellFormedVerifier(string? codeVerifier) =>
        codeVerifier is { Length: >= MinimumVerifierLength and <= MaximumVerifierLength } &&
        codeVerifier.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.' or '_' or '~');
}

internal static class OidcBase64Url
{
    internal const int HandleByteLength = 32;
    internal const int EncodedHandleLength = 43;

    internal static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static bool IsHandleShaped(string? value) =>
        value is { Length: EncodedHandleLength } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
