using System.Security.Cryptography;

namespace Vistara.Auth.Cookies;

public sealed class CryptographicCookieTokenSource : ICookieTokenSource
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

/// <summary>
/// Creates antiforgery tokens in the same shape as session tokens so a
/// restored browser session can be handed a usable token without reissuing
/// the session cookie.
/// </summary>
public static class CookieAntiforgeryTokenFactory
{
    private const string DerivationLabel = "vistara.antiforgery.v1";

    public static string Create(ICookieTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CookieTokenFormat.Create(source);
    }

    /// <summary>
    /// Derives the antiforgery token for a browser session from the session
    /// token itself. The result is stable, so every tab of one session holds
    /// the same usable token and reading the session never invalidates one
    /// that is already in flight. Only a caller that can read the session
    /// cookie can compute it, which is exactly the property cross-site request
    /// forgery lacks, and the derived value never equals the session token.
    /// </summary>
    public static string Derive(string sessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        byte[] material = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(DerivationLabel, "\u001f", sessionToken));
        try
        {
            return Convert.ToBase64String(SHA256.HashData(material))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }
}

internal static class CookieTokenFormat
{
    public const int TokenByteLength = 32;
    private const int EncodedTokenLength = 43;

    public static string Create(ICookieTokenSource source)
    {
        byte[] bytes = new byte[TokenByteLength];
        try
        {
            source.Fill(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool TryComputeDigest(string? token, out string digest)
    {
        digest = string.Empty;
        if (token is not { Length: EncodedTokenLength } ||
            !token.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_'))
        {
            return false;
        }

        byte[] decoded = new byte[TokenByteLength];
        try
        {
            string padded = string.Concat(
                token.Replace('-', '+').Replace('_', '/'),
                "=");
            if (!Convert.TryFromBase64String(
                    padded,
                    decoded,
                    out int written) ||
                written != TokenByteLength ||
                !string.Equals(Encode(decoded), token, StringComparison.Ordinal))
            {
                return false;
            }

            digest = CookieTokenCryptography.ComputeDigest(token);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
