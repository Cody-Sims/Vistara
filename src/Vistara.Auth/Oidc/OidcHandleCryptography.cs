using System.Security.Cryptography;
using System.Text;

namespace Vistara.Auth.Oidc;

/// <summary>
/// Digest helpers for the single-use handles that bind a browser to one
/// in-flight sign-in. A store only ever holds the digest, so a leaked login
/// row cannot be replayed as a state or nonce value.
/// </summary>
public static class OidcHandleCryptography
{
    public static string ComputeDigest(string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        byte[] bytes = Encoding.UTF8.GetBytes(handle);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Computes the lookup digest of a callback handle and rejects any value
    /// that is not a well-formed handle, so a malformed callback parameter can
    /// never become a store lookup key.
    /// </summary>
    public static bool TryComputeDigest(string? handle, out string digest)
    {
        digest = string.Empty;
        if (!OidcBase64Url.IsHandleShaped(handle))
        {
            return false;
        }

        digest = ComputeDigest(handle!);
        return true;
    }

    public static bool FixedTimeMatches(string? handle, string? expectedDigest)
    {
        if (string.IsNullOrEmpty(handle) || expectedDigest is not { Length: 64 })
        {
            return false;
        }

        byte[] handleBytes = Encoding.UTF8.GetBytes(handle);
        byte[] actual = SHA256.HashData(handleBytes);
        byte[]? expected = null;
        try
        {
            expected = Convert.FromHexString(expectedDigest);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(handleBytes);
            CryptographicOperations.ZeroMemory(actual);
            if (expected is not null)
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }
    }
}
