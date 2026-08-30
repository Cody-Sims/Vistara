using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Sharing;

namespace Vistara.Auth.Sharing;

public sealed class CryptographicShareRandomSource : IShareRandomSource
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

public interface ISharePepperProvider
{
    string CurrentVersionId { get; }

    string FingerprintVersionId { get; }

    bool TryGetPepper(string versionId, out ReadOnlyMemory<byte> pepper);
}

public sealed class SharePepperSet : ISharePepperProvider
{
    private readonly Dictionary<string, byte[]> _peppers;

    public SharePepperSet(
        string currentVersionId,
        IReadOnlyDictionary<string, byte[]> peppers,
        string? fingerprintVersionId = null)
    {
        ArgumentNullException.ThrowIfNull(peppers);
        if (!ShareSecretFormat.IsValidVersion(currentVersionId))
        {
            throw new ArgumentException(
                "The current share pepper version is invalid.",
                nameof(currentVersionId));
        }

        _peppers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string version, byte[] pepper) in peppers)
        {
            if (!ShareSecretFormat.IsValidVersion(version) ||
                pepper is null ||
                pepper.Length < 32)
            {
                throw new ArgumentException(
                    "Share peppers require a valid version and at least 256 bits.",
                    nameof(peppers));
            }

            _peppers.Add(version, pepper.ToArray());
        }

        string stableFingerprintVersion =
            fingerprintVersionId ?? currentVersionId;
        if (!_peppers.ContainsKey(currentVersionId) ||
            !_peppers.ContainsKey(stableFingerprintVersion))
        {
            throw new ArgumentException(
                "The current and fingerprint share peppers must be configured.",
                nameof(peppers));
        }

        CurrentVersionId = currentVersionId;
        FingerprintVersionId = stableFingerprintVersion;
    }

    public string CurrentVersionId { get; }

    public string FingerprintVersionId { get; }

    public bool TryGetPepper(string versionId, out ReadOnlyMemory<byte> pepper)
    {
        if (_peppers.TryGetValue(versionId, out byte[]? value))
        {
            pepper = value;
            return true;
        }

        pepper = default;
        return false;
    }
}

public sealed class ShareTokenProtector : IShareTokenProtector
{
    private readonly ShareSecretProtector _protector;

    public ShareTokenProtector(
        IShareRandomSource randomSource,
        ISharePepperProvider peppers)
    {
        _protector = new ShareSecretProtector("vsh", randomSource, peppers);
    }

    public ShareSecretMaterial Issue() => _protector.Issue();

    public bool TryDigest(
        string? plaintext,
        out string pepperVersionId,
        out string digestHex) =>
        _protector.TryDigest(plaintext, out pepperVersionId, out digestHex);
}

public sealed class ShareSessionProtector : IShareSessionProtector
{
    private readonly ShareSecretProtector _protector;

    public ShareSessionProtector(
        IShareRandomSource randomSource,
        ISharePepperProvider peppers)
    {
        _protector = new ShareSecretProtector("vss", randomSource, peppers);
    }

    public ShareSecretMaterial Issue() => _protector.Issue();

    public bool TryDigest(
        string? plaintext,
        out string pepperVersionId,
        out string digestHex) =>
        _protector.TryDigest(plaintext, out pepperVersionId, out digestHex);
}

internal sealed class ShareSecretProtector
{
    private readonly string _prefix;
    private readonly IShareRandomSource _randomSource;
    private readonly ISharePepperProvider _peppers;

    public ShareSecretProtector(
        string prefix,
        IShareRandomSource randomSource,
        ISharePepperProvider peppers)
    {
        _prefix = prefix;
        _randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        _peppers = peppers ?? throw new ArgumentNullException(nameof(peppers));
    }

    public ShareSecretMaterial Issue()
    {
        string version = _peppers.CurrentVersionId;
        if (!_peppers.TryGetPepper(version, out ReadOnlyMemory<byte> pepper))
        {
            throw new InvalidOperationException(
                "The current share pepper is not configured.");
        }

        byte[] secret = new byte[ShareSecretFormat.SecretByteLength];
        byte[]? digest = null;
        try
        {
            _randomSource.Fill(secret);
            string plaintext = ShareSecretFormat.Create(
                _prefix,
                version,
                secret);
            digest = ShareSecretFormat.Digest(pepper.Span, plaintext);
            return new ShareSecretMaterial(
                plaintext,
                version,
                Convert.ToHexStringLower(digest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    public bool TryDigest(
        string? plaintext,
        out string pepperVersionId,
        out string digestHex)
    {
        pepperVersionId = string.Empty;
        digestHex = string.Empty;
        if (!ShareSecretFormat.TryParse(
                _prefix,
                plaintext,
                out string version) ||
            !_peppers.TryGetPepper(version, out ReadOnlyMemory<byte> pepper))
        {
            return false;
        }

        byte[] digest = ShareSecretFormat.Digest(pepper.Span, plaintext!);
        try
        {
            pepperVersionId = version;
            digestHex = Convert.ToHexStringLower(digest);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}

internal static class ShareSecretFormat
{
    public const int SecretByteLength = 32;
    private const int EncodedSecretLength = 43;

    public static string Create(
        string prefix,
        string version,
        ReadOnlySpan<byte> secret) =>
        string.Concat(prefix, "_", version, "_", Encode(secret));

    public static bool TryParse(
        string prefix,
        string? plaintext,
        out string version)
    {
        version = string.Empty;
        if (plaintext is null ||
            plaintext.Length > 64 ||
            !plaintext.StartsWith(
                string.Concat(prefix, "_"),
                StringComparison.Ordinal))
        {
            return false;
        }

        int versionStart = prefix.Length + 1;
        int versionEnd = plaintext.IndexOf('_', versionStart);
        if (versionEnd < 0)
        {
            return false;
        }

        version = plaintext[versionStart..versionEnd];
        string encoded = plaintext[(versionEnd + 1)..];
        if (!IsValidVersion(version) ||
            encoded.Length != EncodedSecretLength ||
            encoded.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_')))
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[SecretByteLength];
        try
        {
            string padded = string.Concat(
                encoded.Replace('-', '+').Replace('_', '/'),
                "=");
            return Convert.TryFromBase64String(
                    padded,
                    decoded,
                    out int written) &&
                written == SecretByteLength &&
                string.Equals(Encode(decoded), encoded, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public static bool IsValidVersion(string? version) =>
        version is { Length: >= 2 and <= 8 } &&
        version[0] == 'v' &&
        version.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    public static byte[] Digest(
        ReadOnlySpan<byte> pepper,
        string plaintext)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return HMACSHA256.HashData(pepper, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Encode(ReadOnlySpan<byte> secret) =>
        Convert.ToBase64String(secret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
