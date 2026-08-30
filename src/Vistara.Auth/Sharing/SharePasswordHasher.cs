using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Sharing;

namespace Vistara.Auth.Sharing;

public sealed class Pbkdf2SharePasswordHasher : ISharePasswordHasher
{
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private readonly IShareRandomSource _randomSource;
    private readonly ISharePepperProvider _peppers;
    private readonly int _iterations;

    public Pbkdf2SharePasswordHasher(
        IShareRandomSource randomSource,
        ISharePepperProvider peppers,
        int iterations = 210_000)
    {
        _randomSource = randomSource ??
            throw new ArgumentNullException(nameof(randomSource));
        _peppers = peppers ?? throw new ArgumentNullException(nameof(peppers));
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 10_000);
        _iterations = iterations;
    }

    public string Hash(string password)
    {
        ValidatePassword(password);
        byte[] salt = new byte[SaltLength];
        byte[]? hash = null;
        try
        {
            _randomSource.Fill(salt);
            hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                _iterations,
                HashAlgorithmName.SHA512,
                HashLength);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"pbkdf2-sha512$v1${_iterations}${Convert.ToBase64String(salt)}$" +
                $"{Convert.ToBase64String(hash)}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            if (hash is not null)
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    public bool Verify(string encodedHash, string password)
    {
        ValidatePassword(password);
        string[] parts = encodedHash.Split('$');
        if (parts.Length != 5 ||
            parts[0] != "pbkdf2-sha512" ||
            parts[1] != "v1" ||
            !int.TryParse(
                parts[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int iterations) ||
            iterations < 10_000)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltLength || expected.Length != HashLength)
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            HashLength);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    public string Fingerprint(string password)
    {
        ValidatePassword(password);
        string version = _peppers.FingerprintVersionId;
        if (!_peppers.TryGetPepper(version, out ReadOnlyMemory<byte> pepper))
        {
            throw new InvalidOperationException(
                "The current share pepper is not configured.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(password);
        byte[]? digest = null;
        try
        {
            digest = HMACSHA256.HashData(pepper.Span, bytes);
            return string.Concat(
                version,
                ":",
                Convert.ToHexStringLower(digest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    private static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 1 or > 256)
        {
            throw new ArgumentException(
                "Share passwords must contain between 1 and 256 characters.",
                nameof(password));
        }
    }
}
