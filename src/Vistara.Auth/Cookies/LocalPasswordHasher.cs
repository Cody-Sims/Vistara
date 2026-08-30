using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vistara.Auth.Cookies;

/// <summary>
/// Hashes and verifies local account passwords. Implementations must be
/// constant time for a given stored verifier.
/// </summary>
public interface ILocalPasswordHasher
{
    /// <summary>Minimum accepted password length.</summary>
    int MinimumPasswordLength { get; }

    string Hash(string password);

    bool Verify(string password, string storedHash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 password verifier using the same encoded layout as the
/// share password hasher: <c>pbkdf2-sha256$iterations$salt$hash</c>.
/// </summary>
public sealed class Pbkdf2LocalPasswordHasher : ILocalPasswordHasher
{
    public const string Prefix = "pbkdf2-sha256";
    private const int SaltByteLength = 16;
    private const int HashByteLength = 32;
    private const int DefaultIterations = 210_000;

    private readonly int _iterations;

    public Pbkdf2LocalPasswordHasher()
        : this(DefaultIterations)
    {
    }

    public Pbkdf2LocalPasswordHasher(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 100_000);
        _iterations = iterations;
    }

    public int MinimumPasswordLength => 12;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"A password must contain at least {MinimumPasswordLength} characters.",
                nameof(password));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteLength);
        byte[] hash = Derive(password, salt, _iterations);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], Prefix, StringComparison.Ordinal) ||
            !int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int iterations) ||
            iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length != HashByteLength)
        {
            return false;
        }

        byte[] actual = Derive(password, salt, iterations);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashByteLength);
}
