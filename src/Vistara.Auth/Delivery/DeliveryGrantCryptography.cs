using System.Security.Cryptography;

namespace Vistara.Auth.Delivery;

public sealed class CryptographicDeliveryGrantRandomSource : IDeliveryGrantRandomSource
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

public sealed class FixedTimeDeliveryGrantDigestComparer : IDeliveryGrantDigestComparer
{
    public bool Equals(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length &&
        CryptographicOperations.FixedTimeEquals(expected, actual);
}

public sealed class DeliveryGrantPepperSet : IDeliveryGrantPepperProvider
{
    private const int MinimumPepperLength = 32;
    private readonly Dictionary<string, byte[]> _peppers;

    public DeliveryGrantPepperSet(
        string currentVersionId,
        IReadOnlyDictionary<string, byte[]> peppers)
    {
        ArgumentNullException.ThrowIfNull(peppers);
        if (!DeliveryGrantTokenFormat.IsValidPepperVersion(currentVersionId))
        {
            throw new ArgumentException(
                "The current delivery grant pepper version is invalid.",
                nameof(currentVersionId));
        }

        var copies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string versionId, byte[] pepper) in peppers)
        {
            if (!DeliveryGrantTokenFormat.IsValidPepperVersion(versionId))
            {
                throw new ArgumentException(
                    "A delivery grant pepper version is invalid.",
                    nameof(peppers));
            }

            ArgumentNullException.ThrowIfNull(pepper);
            if (pepper.Length < MinimumPepperLength)
            {
                throw new ArgumentException(
                    $"Delivery grant peppers must contain at least {MinimumPepperLength} bytes.",
                    nameof(peppers));
            }

            copies.Add(versionId, pepper.ToArray());
        }

        if (!copies.ContainsKey(currentVersionId))
        {
            throw new ArgumentException(
                "The current delivery grant pepper has no configured secret.",
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

internal static class DeliveryGrantTokenFormat
{
    public const int SecretByteLength = 32;
    private const int EncodedSecretLength = 43;
    private const string Prefix = "vdg_";

    public static string Create(
        string pepperVersionId,
        Guid grantId,
        long grantVersion,
        ReadOnlySpan<byte> secret) =>
        string.Concat(
            Prefix,
            pepperVersionId,
            "_",
            grantId.ToString("N"),
            "_",
            grantVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "_",
            EncodeSecret(secret));

    public static bool TryParse(
        string? plaintextToken,
        out ParsedDeliveryGrantToken parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(plaintextToken) ||
            plaintextToken.Length > DeliveryGrantTokenLimits.MaximumPlaintextLength ||
            !plaintextToken.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int pepperEnd = plaintextToken.IndexOf('_', Prefix.Length);
        int grantIdEnd = pepperEnd < 0
            ? -1
            : plaintextToken.IndexOf('_', pepperEnd + 1);
        int grantVersionEnd = grantIdEnd < 0
            ? -1
            : plaintextToken.IndexOf('_', grantIdEnd + 1);
        if (pepperEnd < 0 ||
            grantIdEnd < 0 ||
            grantVersionEnd < 0)
        {
            return false;
        }

        string pepperVersionId = plaintextToken[Prefix.Length..pepperEnd];
        string grantIdText = plaintextToken[(pepperEnd + 1)..grantIdEnd];
        string grantVersionText = plaintextToken[(grantIdEnd + 1)..grantVersionEnd];
        string encodedSecret = plaintextToken[(grantVersionEnd + 1)..];
        if (!IsValidPepperVersion(pepperVersionId) ||
            !Guid.TryParseExact(grantIdText, "N", out Guid grantId) ||
            grantId.Version != 7 ||
            !long.TryParse(
                grantVersionText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long grantVersion) ||
            grantVersion < 1 ||
            encodedSecret.Length != EncodedSecretLength ||
            encodedSecret.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_')))
        {
            return false;
        }

        byte[] secret = new byte[SecretByteLength];
        try
        {
            string padded = string.Concat(
                encodedSecret.Replace('-', '+').Replace('_', '/'),
                "=");
            if (!Convert.TryFromBase64String(
                    padded,
                    secret,
                    out int bytesWritten) ||
                bytesWritten != SecretByteLength ||
                !string.Equals(
                    EncodeSecret(secret),
                    encodedSecret,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        parsed = new ParsedDeliveryGrantToken(
            pepperVersionId,
            grantId,
            grantVersion);
        return true;
    }

    public static bool IsValidPepperVersion(string? versionId) =>
        versionId is { Length: >= 2 and <= 8 } &&
        versionId[0] == 'v' &&
        versionId.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string EncodeSecret(ReadOnlySpan<byte> secret) =>
        Convert.ToBase64String(secret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal readonly record struct ParsedDeliveryGrantToken(
    string PepperVersionId,
    Guid GrantId,
    long GrantVersion);

internal static class DeliveryGrantDigest
{
    public static byte[] Compute(
        ReadOnlySpan<byte> pepper,
        string plaintextToken)
    {
        byte[] plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintextToken);
        try
        {
            return HMACSHA256.HashData(pepper, plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}
