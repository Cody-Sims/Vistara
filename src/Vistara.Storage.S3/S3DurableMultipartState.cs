using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.S3;

internal static class S3DurableMultipartState
{
    internal const string Active = "active";
    internal const string Completed = "completed";
    internal const string Aborted = "aborted";
    internal const string ControlKeyPrefix = "vistara-internal/multipart/v1/";
    private const string StatePrefix = "s3-multipart:v1:";
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

    internal static S3MultipartIdentity CreateIdentity(
        S3ValidatedOptions options,
        string issuanceHash,
        MultipartRequest request,
        string uploadId,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        var identity = new S3MultipartIdentity(
            Version: 1,
            Provider: options.Profile.Name,
            Container: options.BucketName,
            Region: options.Region,
            Endpoint: options.ServiceUrl?.AbsoluteUri ?? string.Empty,
            ForcePathStyle: options.ForcePathStyle,
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
            MaxParts: options.Profile.Capabilities.Limits.MaxMultipartParts,
            MinPartBytes: options.Profile.Capabilities.Limits.MinMultipartPartBytes,
            MaxPartBytes: options.Profile.Capabilities.Limits.MaxMultipartPartBytes);
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

    internal static string Encode(S3MultipartIdentity identity)
    {
        ValidateIdentity(identity);
        return string.Concat(
            StatePrefix,
            Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
                identity,
                JsonOptions)));
    }

    internal static S3MultipartIdentity Decode(string value)
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
            S3MultipartIdentity identity =
                JsonSerializer.Deserialize<S3MultipartIdentity>(
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

    internal static byte[] EncodeMarker(S3MultipartMarker marker)
    {
        ValidateMarker(marker);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions);
        if (bytes.Length > MaximumControlBytes)
        {
            throw InvalidState();
        }

        return bytes;
    }

    internal static S3MultipartMarker DecodeMarker(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length is 0 or > MaximumControlBytes)
            {
                throw new FormatException();
            }

            S3MultipartMarker marker =
                JsonSerializer.Deserialize<S3MultipartMarker>(
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
        S3MultipartIdentity identity,
        S3ValidatedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(
                identity.Provider,
                options.Profile.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.Container,
                options.BucketName,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.Region,
                options.Region,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.Endpoint,
                options.ServiceUrl?.AbsoluteUri ?? string.Empty,
                StringComparison.Ordinal) ||
            identity.ForcePathStyle != options.ForcePathStyle)
        {
            throw InvalidState();
        }
    }

    internal static void ValidateRequest(
        S3MultipartIdentity identity,
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
        S3MultipartIdentity identity,
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
        S3MultipartIdentity expected,
        S3MultipartIdentity observed)
    {
        ValidateIdentity(observed);
        if (expected != observed)
        {
            throw InvalidState();
        }
    }

    internal static DateTimeOffset ExpiresAt(S3MultipartIdentity identity) =>
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

    private static void ValidateMarker(S3MultipartMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ValidateIdentity(marker.Identity);
        if (marker.Status is not (Active or Completed or Aborted))
        {
            throw new FormatException();
        }
    }

    private static void ValidateIdentity(S3MultipartIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Version != 1 ||
            string.IsNullOrWhiteSpace(identity.Provider) ||
            string.IsNullOrWhiteSpace(identity.Container) ||
            string.IsNullOrWhiteSpace(identity.Region) ||
            identity.Endpoint is null ||
            identity.Endpoint.Length > 2_048 ||
            identity.IssuanceHash is null ||
            identity.IssuanceHash.Length != 64 ||
            !identity.IssuanceHash.All(Uri.IsHexDigit) ||
            !string.Equals(
                ControlKey(identity.IssuanceHash),
                identity.MarkerKey,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.Key) ||
            identity.Key.Length > 1_024 ||
            string.IsNullOrWhiteSpace(identity.UploadId) ||
            identity.UploadId.Length > 1_024 ||
            identity.UploadId.Any(char.IsControl) ||
            identity.Nonce is null ||
            identity.Nonce.Length != 32 ||
            !identity.Nonce.All(Uri.IsHexDigit) ||
            identity.ExpiresAtUtcTicks <= DateTime.UnixEpoch.Ticks ||
            identity.ExpiresAtUtcTicks > DateTime.MaxValue.Ticks ||
            identity.RequestHash is null ||
            identity.RequestHash.Length != 64 ||
            !identity.RequestHash.All(Uri.IsHexDigit) ||
            identity.MaxParts <= 0 ||
            identity.MinPartBytes <= 0 ||
            identity.MaxPartBytes < identity.MinPartBytes)
        {
            throw new FormatException();
        }

        _ = new BlobKey(identity.Key);
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
            "The durable S3 multipart provider state is invalid.",
            error);
}

internal sealed record S3MultipartIdentity(
    int Version,
    string Provider,
    string Container,
    string Region,
    string Endpoint,
    bool ForcePathStyle,
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
        S3DurableMultipartState.ControlKey(IssuanceHash);
}

internal sealed record S3MultipartMarker(
    S3MultipartIdentity Identity,
    string Status);
