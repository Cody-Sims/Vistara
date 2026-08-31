using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vistara.Api.Features;

/// <summary>
/// An opaque keyset cursor for tenant-scoped administrative listings. The
/// cursor binds the tenant and a hash of the normalized query, so replaying it
/// against another tenant or another query is detected and reported as a
/// conflict rather than silently returning the wrong page. It carries no
/// secret, only a position the caller could already reach.
/// </summary>
public readonly record struct AdminCursor(
    Guid TenantId,
    string QueryFingerprint,
    long Ticks,
    Guid Id)
{
    private const int MaximumEncodedLength = 512;

    public static string Fingerprint(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        string canonical = string.Join('\u001f', parts.Select(part => part ?? string.Empty));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    public string Encode()
    {
        string payload = string.Join(
            '|',
            TenantId.ToString("N"),
            QueryFingerprint,
            Ticks.ToString(CultureInfo.InvariantCulture),
            Id.ToString("N"));
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryDecode(
        string value,
        Guid tenantId,
        string queryFingerprint,
        out AdminCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEncodedLength)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return false;
        }

        string[] parts = Encoding.UTF8.GetString(decoded).Split('|');
        if (parts.Length != 4 ||
            !Guid.TryParseExact(parts[0], "N", out Guid cursorTenantId) ||
            cursorTenantId != tenantId ||
            !string.Equals(parts[1], queryFingerprint, StringComparison.Ordinal) ||
            !long.TryParse(
                parts[2],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long ticks) ||
            // A tick count outside the representable range would throw while
            // rebuilding the instant, so it is rejected as a bad cursor here.
            ticks < 0 ||
            ticks > DateTime.MaxValue.Ticks ||
            !Guid.TryParseExact(parts[3], "N", out Guid id))
        {
            return false;
        }

        cursor = new AdminCursor(cursorTenantId, queryFingerprint, ticks, id);
        return true;
    }
}

public static class AdminPaging
{
    public const int DefaultLimit = 60;

    public const int MaximumLimit = 200;

    /// <summary>Reads and clamps a page size, rejecting malformed values.</summary>
    public static bool TryReadLimit(string? raw, out int limit)
    {
        limit = DefaultLimit;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed < 1 ||
            parsed > MaximumLimit)
        {
            return false;
        }

        limit = parsed;
        return true;
    }
}
