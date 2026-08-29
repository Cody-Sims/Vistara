using System.Text.Json.Serialization;

namespace Vistara.Contracts.Media;

public static class MediaDeliveryHttpContract
{
    public const string DeliveryGrantAuthorizationScheme = "Vistara-Delivery";
    public const string RedactedCredential = "[REDACTED]";
    public const string PublicImmutableCacheControl =
        "public,max-age=31536000,immutable";
    public const string PrivateNoStoreCacheControl = "private,no-store";
    public const string NoStoreCacheControl = "no-store";
}

/// <summary>
/// Describes a derivative request accepted for asynchronous processing.
/// HTTP 202 responses and every media error response use <c>no-store</c>.
/// Successful public representations, including HTTP 304, use the immutable
/// public cache policy only after conditional and range selection succeeds.
/// </summary>
public sealed record MediaProcessingResponse(
    [property: JsonPropertyName("state")] string State);
