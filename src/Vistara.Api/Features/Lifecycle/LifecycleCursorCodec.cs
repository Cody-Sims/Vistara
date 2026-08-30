using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vistara.Api.Features.Lifecycle;

public sealed class LifecycleCursorCodec : ILifecycleCursorCodec
{
    private readonly byte[] _key;

    public LifecycleCursorCodec(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new ArgumentException(
                "Lifecycle cursor signing keys must contain at least 256 bits.",
                nameof(key));
        }

        _key = key.ToArray();
    }

    public string Encode(LifecycleCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.DeletedAtUtc.Offset != TimeSpan.Zero ||
            cursor.AssetId == Guid.Empty ||
            cursor.AssetId.Version != 7)
        {
            throw new ArgumentException("The lifecycle cursor is invalid.", nameof(cursor));
        }

        string payload = string.Join(
            '.',
            cursor.DeletedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            cursor.AssetId.ToString("N"),
            cursor.Descending ? "d" : "a");
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = HMACSHA256.HashData(_key, payloadBytes);
        return $"{Base64Url(payloadBytes)}.{Base64Url(signature)}";
    }

    public bool TryDecode(string value, out LifecycleCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096)
        {
            return false;
        }

        int separator = value.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator != value.LastIndexOf('.'))
        {
            return false;
        }

        try
        {
            byte[] payloadBytes = FromBase64Url(value[..separator]);
            byte[] suppliedSignature = FromBase64Url(value[(separator + 1)..]);
            byte[] expectedSignature = HMACSHA256.HashData(_key, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                    suppliedSignature,
                    expectedSignature))
            {
                return false;
            }

            string[] parts = Encoding.UTF8.GetString(payloadBytes).Split('.');
            if (parts.Length != 3 ||
                !long.TryParse(
                    parts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long utcTicks) ||
                !Guid.TryParseExact(parts[1], "N", out Guid assetId) ||
                assetId.Version != 7 ||
                parts[2] is not ("d" or "a"))
            {
                return false;
            }

            cursor = new LifecycleCursor(
                new DateTimeOffset(utcTicks, TimeSpan.Zero),
                assetId,
                parts[2] == "d");
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(
            checked(base64.Length + ((4 - (base64.Length % 4)) % 4)),
            '=');
        return Convert.FromBase64String(base64);
    }
}
