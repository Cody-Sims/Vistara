using System.Text.Json.Serialization;

namespace Vistara.Contracts.Capabilities;

/// <summary>
/// The versioned, non-secret runtime capability surface returned by
/// <c>GET /api/v1/capabilities</c>.
/// </summary>
public sealed record CapabilitiesResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("database")] DatabaseCapabilitiesResponse Database,
    [property: JsonPropertyName("storage")] StorageCapabilitiesResponse Storage,
    [property: JsonPropertyName("imaging")] ImagingCapabilitiesResponse Imaging,
    [property: JsonPropertyName("upload")] UploadCapabilitiesResponse Upload,
    [property: JsonPropertyName("search")] SearchCapabilitiesResponse Search,
    [property: JsonPropertyName("api")] ApiCapabilitiesResponse Api)
{
    /// <summary>The only capability schema version currently published.</summary>
    public const int CurrentSchemaVersion = 1;
}

public sealed record DatabaseCapabilitiesResponse(
    [property: JsonPropertyName("provider")] string Provider);

public sealed record StorageCapabilitiesResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("directUpload")] bool DirectUpload,
    [property: JsonPropertyName("multipartUpload")] bool MultipartUpload,
    [property: JsonPropertyName("rangeReads")] bool RangeReads,
    [property: JsonPropertyName("maxObjectBytes")] long MaxObjectBytes,
    [property: JsonPropertyName("maxMultipartParts")] int MaxMultipartParts,
    [property: JsonPropertyName("minMultipartPartBytes")] long MinMultipartPartBytes,
    [property: JsonPropertyName("maxMultipartPartBytes")] long MaxMultipartPartBytes);

public sealed record ImagingCapabilitiesResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("inputFormats")] IReadOnlyList<string> InputFormats,
    [property: JsonPropertyName("outputFormats")] IReadOnlyList<string> OutputFormats,
    [property: JsonPropertyName("maxEncodedBytes")] long MaxEncodedBytes,
    [property: JsonPropertyName("maxWidth")] int MaxWidth,
    [property: JsonPropertyName("maxHeight")] int MaxHeight,
    [property: JsonPropertyName("maxAggregatePixels")] long MaxAggregatePixels,
    [property: JsonPropertyName("maxFrames")] int MaxFrames,
    [property: JsonPropertyName("maxEstimatedDecodedBytes")] long MaxEstimatedDecodedBytes,
    [property: JsonPropertyName("processingDeadlineSeconds")] int ProcessingDeadlineSeconds,
    [property: JsonPropertyName("maxConcurrentTransforms")] int MaxConcurrentTransforms);

public sealed record UploadCapabilitiesResponse(
    [property: JsonPropertyName("maxBytes")] long MaxBytes,
    [property: JsonPropertyName("maxConcurrentUploads")] long MaxConcurrentUploads,
    [property: JsonPropertyName("concurrencyUnlimited")] bool ConcurrencyUnlimited,
    [property: JsonPropertyName("multipartThresholdBytes")] long MultipartThresholdBytes,
    [property: JsonPropertyName("proxyUpload")] bool ProxyUpload,
    [property: JsonPropertyName("directUpload")] bool DirectUpload,
    [property: JsonPropertyName("multipartUpload")] bool MultipartUpload);

public sealed record SearchCapabilitiesResponse(
    [property: JsonPropertyName("text")] bool Text,
    [property: JsonPropertyName("facets")] bool Facets,
    [property: JsonPropertyName("timeline")] bool Timeline,
    [property: JsonPropertyName("providerNativeFullText")] bool ProviderNativeFullText);

public sealed record ApiCapabilitiesResponse(
    [property: JsonPropertyName("defaultPageSize")] int DefaultPageSize,
    [property: JsonPropertyName("maxPageSize")] int MaxPageSize,
    [property: JsonPropertyName("maxProxyUploadBytes")] long MaxProxyUploadBytes);
