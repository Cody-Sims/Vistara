using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistara.Application.Sharing;

namespace Vistara.Auth.Sharing;

public sealed class ShareCursorProtector(
    ISharePepperProvider peppers) : IShareCursorProtector
{
    private const string Prefix = "vsc";
    private const int MaximumCursorLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly ISharePepperProvider _peppers =
        peppers ?? throw new ArgumentNullException(nameof(peppers));

    public string Protect(ShareCursorState cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        string version = _peppers.CurrentVersionId;
        if (!_peppers.TryGetPepper(version, out ReadOnlyMemory<byte> pepper))
        {
            throw new InvalidOperationException(
                "The current share pepper is not configured.");
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions);
        byte[]? signature = null;
        try
        {
            string encodedPayload = Encode(payload);
            string unsigned = string.Concat(
                Prefix,
                "_",
                version,
                "_",
                encodedPayload);
            signature = HMACSHA256.HashData(
                pepper.Span,
                Encoding.ASCII.GetBytes(unsigned));
            return string.Concat(unsigned, ".", Encode(signature));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (signature is not null)
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
    }

    public bool TryUnprotect(
        string? protectedCursor,
        out ShareCursorState cursor)
    {
        cursor = default!;
        if (string.IsNullOrEmpty(protectedCursor) ||
            protectedCursor.Length > MaximumCursorLength ||
            !protectedCursor.StartsWith(
                string.Concat(Prefix, "_"),
                StringComparison.Ordinal))
        {
            return false;
        }

        int versionStart = Prefix.Length + 1;
        int versionEnd = protectedCursor.IndexOf('_', versionStart);
        int signatureStart = protectedCursor.LastIndexOf('.');
        if (versionEnd < 0 || signatureStart <= versionEnd)
        {
            return false;
        }

        string version = protectedCursor[versionStart..versionEnd];
        if (!ShareSecretFormat.IsValidVersion(version) ||
            !_peppers.TryGetPepper(version, out ReadOnlyMemory<byte> pepper))
        {
            return false;
        }

        string unsigned = protectedCursor[..signatureStart];
        byte[]? payload = Decode(
            protectedCursor[(versionEnd + 1)..signatureStart]);
        byte[]? presentedSignature = Decode(
            protectedCursor[(signatureStart + 1)..]);
        if (payload is null || presentedSignature is not { Length: 32 })
        {
            Zero(payload);
            Zero(presentedSignature);
            return false;
        }

        byte[] expectedSignature = HMACSHA256.HashData(
            pepper.Span,
            Encoding.ASCII.GetBytes(unsigned));
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedSignature,
                    presentedSignature))
            {
                return false;
            }

            ShareCursorState? parsed =
                JsonSerializer.Deserialize<ShareCursorState>(
                    payload,
                    JsonOptions);
            if (parsed is null ||
                parsed.TenantId == Guid.Empty ||
                parsed.TenantId.Version != 7 ||
                parsed.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                parsed.Offset < 0)
            {
                return false;
            }

            cursor = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            Zero(payload);
            Zero(presentedSignature);
            Zero(expectedSignature);
        }
    }

    private static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[]? Decode(string value)
    {
        if (value.Length == 0 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_')))
        {
            return null;
        }

        int padding = (4 - (value.Length % 4)) % 4;
        try
        {
            byte[] decoded = Convert.FromBase64String(
                string.Concat(
                    value.Replace('-', '+').Replace('_', '/'),
                    new string('=', padding)));
            if (!string.Equals(Encode(decoded), value, StringComparison.Ordinal))
            {
                Zero(decoded);
                return null;
            }

            return decoded;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
