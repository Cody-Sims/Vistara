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
    public static string Create(ICookieTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CookieTokenFormat.Create(source);
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
