using System.Security.Cryptography;
using System.Text.Json;

namespace Vistara.Application.Gallery.Queries;

public sealed record AssetCursorState(
    string FilterHash,
    DateTimeOffset SnapshotAtUtc,
    AssetSort Sort,
    SortDirection Direction,
    int NullRank,
    DateTimeOffset? InstantValue,
    string? TextValue,
    long? NumberValue,
    Guid AssetId);

public enum AssetCursorReadStatus
{
    Valid,
    Invalid,
}

public sealed record AssetCursorReadResult(
    AssetCursorReadStatus Status,
    AssetCursorState? State);

public sealed class AssetCursorProtector
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly byte[] _key;

    public AssetCursorProtector(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException(
                "The asset cursor key must contain exactly 32 bytes.",
                nameof(key));
        }

        _key = key.ToArray();
    }

    public string Protect(AssetCursorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValidState(state))
        {
            throw new ArgumentException("The asset cursor state is invalid.", nameof(state));
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        try
        {
            using var cipher = new AesGcm(_key, TagSize);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag);
            byte[] envelope = new byte[1 + NonceSize + ciphertext.Length + TagSize];
            envelope[0] = FormatVersion;
            nonce.CopyTo(envelope, 1);
            ciphertext.CopyTo(envelope, 1 + NonceSize);
            tag.CopyTo(envelope, envelope.Length - TagSize);
            return Base64UrlEncode(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public AssetCursorReadResult Read(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 4_096)
        {
            return new AssetCursorReadResult(AssetCursorReadStatus.Invalid, null);
        }

        byte[] envelope;
        try
        {
            envelope = Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            return new AssetCursorReadResult(AssetCursorReadStatus.Invalid, null);
        }

        if (envelope.Length <= 1 + NonceSize + TagSize ||
            envelope[0] != FormatVersion)
        {
            return new AssetCursorReadResult(AssetCursorReadStatus.Invalid, null);
        }

        ReadOnlySpan<byte> nonce = envelope.AsSpan(1, NonceSize);
        ReadOnlySpan<byte> ciphertext = envelope.AsSpan(
            1 + NonceSize,
            envelope.Length - 1 - NonceSize - TagSize);
        ReadOnlySpan<byte> tag = envelope.AsSpan(envelope.Length - TagSize);
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(_key, TagSize);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext);
            AssetCursorState? state =
                JsonSerializer.Deserialize<AssetCursorState>(plaintext, JsonOptions);
            if (state is null || !IsValidState(state))
            {
                return new AssetCursorReadResult(AssetCursorReadStatus.Invalid, null);
            }

            return new AssetCursorReadResult(AssetCursorReadStatus.Valid, state);
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException)
        {
            return new AssetCursorReadResult(AssetCursorReadStatus.Invalid, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        int padding = normalized.Length % 4;
        if (padding == 1)
        {
            throw new FormatException("The cursor encoding is invalid.");
        }

        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private static bool IsValidState(AssetCursorState state) =>
        state.FilterHash.Length == 64 &&
        state.FilterHash.All(Uri.IsHexDigit) &&
        state.AssetId != Guid.Empty &&
        state.AssetId.Version == 7 &&
        Enum.IsDefined(state.Sort) &&
        Enum.IsDefined(state.Direction) &&
        state.NullRank is >= 0 and <= 1 &&
        (state.Sort switch
        {
            AssetSort.CapturedAt or AssetSort.ImportedAt or AssetSort.UpdatedAt =>
                state.InstantValue is not null &&
                state.TextValue is null &&
                state.NumberValue is null,
            AssetSort.Title =>
                state.InstantValue is null &&
                state.TextValue is not null &&
                state.NumberValue is null,
            AssetSort.SizeBytes =>
                state.InstantValue is null &&
                state.TextValue is null &&
                state.NumberValue is not null,
            _ => false,
        });
}
