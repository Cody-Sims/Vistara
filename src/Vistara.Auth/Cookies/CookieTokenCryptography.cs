using System.Security.Cryptography;
using System.Text;

namespace Vistara.Auth.Cookies;

public static class CookieTokenCryptography
{
    public static string ComputeDigest(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool FixedTimeMatches(string plaintext, string expectedDigest)
    {
        if (string.IsNullOrEmpty(plaintext) ||
            expectedDigest is not { Length: 64 })
        {
            return false;
        }

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] actual = SHA256.HashData(plaintextBytes);
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
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(actual);
            if (expected is not null)
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }
    }
}
