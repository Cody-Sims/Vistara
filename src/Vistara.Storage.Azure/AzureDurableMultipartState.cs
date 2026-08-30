using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.Azure;

internal static class AzureDurableMultipartState
{
    internal const string Active = "active";
    internal const string Completed = "completed";
    internal const string Aborted = "aborted";
    internal const string ControlKeyPrefix = "vistara-internal/multipart/v1/";
    private const string StatePrefix = "azure-multipart:v1:";
    private const int MaximumControlBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static string IssuanceHash(string issuanceId)
    {
        ValidateIssuanceId(issuanceId);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(issuanceId)));
    }

    internal static string ControlKey(string issuanceHash) =>
        string.Concat(ControlKeyPrefix, issuanceHash);

    internal static bool IsControlKey(string key) =>
        key.StartsWith(ControlKeyPrefix, StringComparison.Ordinal);

    internal static AzureMultipartIdentity CreateIdentity(
        AzureBlobStoreOptions options,
        string issuanceHash,
        MultipartRequest request,
        string uploadId,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        var identity = new AzureMultipartIdentity(
            Version: 1,
            Provider: "azure",
            Account: options.AccountName,
            Container: options.ContainerName,
            Endpoint: options.ServiceUri.AbsoluteUri,
            EmulatorMode: options.EmulatorMode,
            IssuanceHash: issuanceHash,
            Key: request.Key.Value,
            UploadId: uploadId,
            Nonce: Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            ExpiresAtUtcTicks: expiresAtUtc.UtcDateTime.Ticks,
            RequestHash: RequestHash(
                request.Key,
                request.ContentLength,
                request.ContentType,
                request.Checksum,
                request.Conditions,
                request.PartPlanLifetime,
                request.Metadata),
            MaxParts: 50_000,
            MinPartBytes: 1,
            MaxPartBytes: 4_000_000_000);
        try
        {
            ValidateIdentity(identity);
            return identity;
        }
        catch (FormatException error)
        {
            throw InvalidState(error);
        }
    }

    internal static string Encode(AzureMultipartIdentity identity)
    {
        ValidateIdentity(identity);
        return string.Concat(
            StatePrefix,
            Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
                identity,
                JsonOptions)));
    }

    internal static AzureMultipartIdentity Decode(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > 8_192 ||
                !value.StartsWith(StatePrefix, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            byte[] bytes = Base64UrlDecode(value[StatePrefix.Length..]);
            AzureMultipartIdentity identity =
                JsonSerializer.Deserialize<AzureMultipartIdentity>(
                    bytes,
                    JsonOptions) ??
                throw new FormatException();
            ValidateIdentity(identity);
            return identity;
        }
        catch (Exception error) when (
            error is FormatException or JsonException or ArgumentException or
                OverflowException)
        {
            throw InvalidState(error);
        }
    }

    internal static byte[] EncodeMarker(AzureMultipartMarker marker)
    {
        ValidateMarker(marker);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions);
        if (bytes.Length > MaximumControlBytes)
        {
            throw InvalidState();
        }

        return bytes;
    }

    internal static AzureMultipartMarker DecodeMarker(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length is 0 or > MaximumControlBytes)
            {
                throw new FormatException();
            }

            AzureMultipartMarker marker =
                JsonSerializer.Deserialize<AzureMultipartMarker>(
                    bytes,
                    JsonOptions) ??
                throw new FormatException();
            ValidateMarker(marker);
            return marker;
        }
        catch (Exception error) when (
            error is FormatException or JsonException or ArgumentException or
                OverflowException)
        {
            throw InvalidState(error);
        }
    }

    internal static void ValidateConfiguration(
        AzureMultipartIdentity identity,
        AzureBlobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(identity.Provider, "azure", StringComparison.Ordinal) ||
            !string.Equals(
                identity.Account,
                options.AccountName,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.Container,
                options.ContainerName,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.Endpoint,
                options.ServiceUri.AbsoluteUri,
                StringComparison.Ordinal) ||
            identity.EmulatorMode != options.EmulatorMode)
        {
            throw InvalidState();
        }
    }

    internal static void ValidateRequest(
        AzureMultipartIdentity identity,
        MultipartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(
                identity.Key,
                request.Key.Value,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.RequestHash,
                RequestHash(
                    request.Key,
                    request.ContentLength,
                    request.ContentType,
                    request.Checksum,
                    request.Conditions,
                    request.PartPlanLifetime,
                    request.Metadata),
                StringComparison.Ordinal))
        {
            throw InvalidState();
        }
    }

    internal static void ValidateSession(
        AzureMultipartIdentity identity,
        MultipartSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(
                identity.Key,
                session.Key.Value,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.UploadId,
                session.UploadId,
                StringComparison.Ordinal) ||
            identity.ExpiresAtUtcTicks != session.ExpiresAtUtc.UtcDateTime.Ticks ||
            identity.MaxParts != session.MaxParts ||
            identity.MinPartBytes != session.MinPartBytes ||
            identity.MaxPartBytes != session.MaxPartBytes ||
            !string.Equals(
                identity.RequestHash,
                RequestHash(
                    session.Key,
                    session.ContentLength,
                    session.ContentType,
                    session.Checksum,
                    session.CompletionConditions,
                    session.PartPlanLifetime,
                    session.Metadata),
                StringComparison.Ordinal))
        {
            throw InvalidState();
        }
    }

    internal static void ValidateMarkerIdentity(
        AzureMultipartIdentity expected,
        AzureMultipartIdentity observed)
    {
        ValidateIdentity(observed);
        if (expected != observed)
        {
            throw InvalidState();
        }
    }

    internal static DateTimeOffset ExpiresAt(AzureMultipartIdentity identity) =>
        new(new DateTime(identity.ExpiresAtUtcTicks, DateTimeKind.Utc));

    private static string RequestHash(
        BlobKey key,
        long contentLength,
        BlobMediaType contentType,
        BlobChecksum? checksum,
        BlobRequestConditions conditions,
        TimeSpan partPlanLifetime,
        BlobMetadata metadata)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", key.Value);
            writer.WriteNumber("contentLength", contentLength);
            writer.WriteString("contentType", contentType.Value);
            writer.WriteString("checksumAlgorithm", checksum?.Algorithm.ToString());
            writer.WriteString("checksum", checksum?.Value);
            writer.WriteString("ifMatch", conditions.IfMatch?.Value);
            writer.WriteString(
                "ifEntityTagMatch",
                conditions.IfEntityTagMatch?.Value);
            writer.WriteBoolean("requireMissing", conditions.RequireMissing);
            writer.WriteNumber("partPlanLifetimeTicks", partPlanLifetime.Ticks);
            writer.WriteStartArray("metadata");
            foreach ((string name, string value) in metadata.AsReadOnly()
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static void ValidateMarker(AzureMultipartMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ValidateIdentity(marker.Identity);
        if (marker.Status is not (Active or Completed or Aborted))
        {
            throw new FormatException();
        }
    }

    private static void ValidateIdentity(AzureMultipartIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Version != 1 ||
            !string.Equals(identity.Provider, "azure", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.Account) ||
            string.IsNullOrWhiteSpace(identity.Container) ||
            string.IsNullOrWhiteSpace(identity.Endpoint) ||
            identity.Endpoint.Length > 2_048 ||
            identity.IssuanceHash is null ||
            identity.IssuanceHash.Length != 64 ||
            !identity.IssuanceHash.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(identity.Key) ||
            identity.Key.Length > 1_024 ||
            string.IsNullOrWhiteSpace(identity.UploadId) ||
            identity.UploadId.Length > 256 ||
            identity.UploadId.Any(char.IsControl) ||
            identity.Nonce is null ||
            identity.Nonce.Length != 32 ||
            !identity.Nonce.All(Uri.IsHexDigit) ||
            identity.ExpiresAtUtcTicks <= DateTime.UnixEpoch.Ticks ||
            identity.ExpiresAtUtcTicks > DateTime.MaxValue.Ticks ||
            identity.RequestHash is null ||
            identity.RequestHash.Length != 64 ||
            !identity.RequestHash.All(Uri.IsHexDigit) ||
            identity.MaxParts != 50_000 ||
            identity.MinPartBytes != 1 ||
            identity.MaxPartBytes != 4_000_000_000)
        {
            throw new FormatException();
        }

        _ = new BlobKey(identity.Key);
        _ = new Uri(identity.Endpoint, UriKind.Absolute);
    }

    private static void ValidateIssuanceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            value.Any(character =>
                character > 127 ||
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_' or '.' or ':')))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The multipart issuance identifier is invalid.");
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_')))
        {
            throw new FormatException();
        }

        string padded = value
            .Replace('-', '+')
            .Replace('_', '/');
        padded = padded.PadRight(
            checked(padded.Length + ((4 - (padded.Length % 4)) % 4)),
            '=');
        return Convert.FromBase64String(padded);
    }

    private static BlobStoreException InvalidState(Exception? error = null) =>
        new(
            BlobStoreErrorCode.InvalidRequest,
            "The durable Azure multipart provider state is invalid.",
            error);
}

internal sealed record AzureMultipartIdentity(
    int Version,
    string Provider,
    string Account,
    string Container,
    string Endpoint,
    bool EmulatorMode,
    string IssuanceHash,
    string Key,
    string UploadId,
    string Nonce,
    long ExpiresAtUtcTicks,
    string RequestHash,
    int MaxParts,
    long MinPartBytes,
    long MaxPartBytes)
{
    [JsonIgnore]
    public string MarkerKey =>
        AzureDurableMultipartState.ControlKey(IssuanceHash);
}

internal sealed record AzureMultipartMarker(
    AzureMultipartIdentity Identity,
    string Status);
