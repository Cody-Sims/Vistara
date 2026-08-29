using System.Security.Cryptography;

namespace Vistara.Auth.ApiKeys;

public sealed class CryptographicApiKeyRandomSource : IApiKeyRandomSource
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

public sealed class FixedTimeApiKeyDigestComparer : IApiKeyDigestComparer
{
    public bool Equals(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length &&
        CryptographicOperations.FixedTimeEquals(expected, actual);
}

public sealed class ApiKeyPepperSet : IApiKeyPepperProvider
{
    private const int MinimumPepperLength = 32;
    private readonly Dictionary<string, byte[]> _peppers;

    public ApiKeyPepperSet(
        string currentVersionId,
        IReadOnlyDictionary<string, byte[]> peppers)
    {
        ArgumentNullException.ThrowIfNull(peppers);
        if (!ApiKeyFormat.IsValidVersionId(currentVersionId))
        {
            throw new ArgumentException(
                "The current API key pepper version identifier is invalid.",
                nameof(currentVersionId));
        }

        var copies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string versionId, byte[] pepper) in peppers)
        {
            if (!ApiKeyFormat.IsValidVersionId(versionId))
            {
                throw new ArgumentException(
                    "An API key pepper version identifier is invalid.",
                    nameof(peppers));
            }

            ArgumentNullException.ThrowIfNull(pepper);
            if (pepper.Length < MinimumPepperLength)
            {
                throw new ArgumentException(
                    $"API key peppers must contain at least {MinimumPepperLength} bytes.",
                    nameof(peppers));
            }

            if (!copies.TryAdd(versionId, pepper.ToArray()))
            {
                throw new ArgumentException(
                    "API key pepper version identifiers must be unique.",
                    nameof(peppers));
            }
        }

        if (!copies.ContainsKey(currentVersionId))
        {
            throw new ArgumentException(
                "The current API key pepper version has no configured secret.",
                nameof(peppers));
        }

        CurrentVersionId = currentVersionId;
        _peppers = copies;
    }

    public string CurrentVersionId { get; }

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

public static class ApiKeyFormat
{
    public const int SecretByteLength = 32;
    public const int MaximumPlaintextLength = 128;
    private const int EncodedSecretLength = 43;
    private const int KeyIdLength = 32;
    private const string ProductPrefix = "vst_";

    internal static string CreatePrefix(string versionId, Guid keyId) =>
        string.Concat(ProductPrefix, versionId, keyId.ToString("N"));

    internal static string EncodeSecret(ReadOnlySpan<byte> secret) =>
        Convert.ToBase64String(secret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static bool TryParse(
        string? plaintextKey,
        out ParsedApiKey parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(plaintextKey) ||
            plaintextKey.Length > MaximumPlaintextLength ||
            !plaintextKey.StartsWith(ProductPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int separator = plaintextKey.IndexOf('_', ProductPrefix.Length);
        if (separator <= ProductPrefix.Length ||
            plaintextKey.Length - separator - 1 != EncodedSecretLength)
        {
            return false;
        }

        string prefix = plaintextKey[..separator];
        string identifier = prefix[ProductPrefix.Length..];
        if (identifier.Length <= KeyIdLength)
        {
            return false;
        }

        string versionId = identifier[..^KeyIdLength];
        string keyIdText = identifier[^KeyIdLength..];
        if (!IsValidVersionId(versionId) ||
            !Guid.TryParseExact(keyIdText, "N", out Guid keyId) ||
            keyId.Version != 7)
        {
            return false;
        }

        string encodedSecret = plaintextKey[(separator + 1)..];
        if (!encodedSecret.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_'))
        {
            return false;
        }

        byte[] secret = new byte[SecretByteLength];
        string paddedSecret = string.Concat(encodedSecret, "=");
        if (!Convert.TryFromBase64String(
                paddedSecret.Replace('-', '+').Replace('_', '/'),
                secret,
                out int bytesWritten) ||
            bytesWritten != SecretByteLength ||
            !string.Equals(
                EncodeSecret(secret),
                encodedSecret,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(secret);
            return false;
        }

        parsed = new ParsedApiKey(
            new Vistara.Domain.Identity.ApiKeyId(keyId),
            versionId,
            prefix,
            secret);
        return true;
    }

    internal static bool IsValidVersionId(string? versionId) =>
        versionId is { Length: >= 2 and <= 8 } &&
        versionId[0] == 'v' &&
        versionId.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}

internal readonly record struct ParsedApiKey(
    Vistara.Domain.Identity.ApiKeyId KeyId,
    string VersionId,
    string Prefix,
    byte[] Secret);

internal static class ApiKeyDigest
{
    public static byte[] Compute(
        ReadOnlySpan<byte> pepper,
        ReadOnlySpan<byte> secret) =>
        HMACSHA256.HashData(pepper, secret);
}
