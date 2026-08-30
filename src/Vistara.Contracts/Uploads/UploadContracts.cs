using System.Text.Json.Serialization;

namespace Vistara.Contracts.Uploads;

public sealed record CreateUploadRequest(
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("sha256")] string? Sha256);

public sealed record RefreshUploadPartsRequest(
    [property: JsonPropertyName("partNumbers")] IReadOnlyList<int>? PartNumbers);

public sealed record CommitUploadRequest(
    [property: JsonPropertyName("parts")] IReadOnlyList<CompletedUploadPartRequest>? Parts);

public sealed record CompletedUploadPartRequest(
    [property: JsonPropertyName("partNumber")] int PartNumber,
    [property: JsonPropertyName("etag")] string? EntityTag,
    [property: JsonPropertyName("checksum")] string? Checksum,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

public sealed record UploadStatusResponse(
    [property: JsonPropertyName("uploadId")] Guid UploadId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("expectedSizeBytes")] long ExpectedSizeBytes,
    [property: JsonPropertyName("declaredContentType")] string DeclaredContentType,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("parts")] IReadOnlyList<UploadPartResponse> Parts,
    [property: JsonPropertyName("plan")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    UploadPlanResponse? Plan = null);

public sealed record UploadPartResponse(
    [property: JsonPropertyName("partNumber")] int PartNumber,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("checksum")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Checksum);

public sealed record UploadPlanResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("expiresAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("contentUrl")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContentUrl,
    [property: JsonPropertyName("request")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedUploadRequestResponse? Request,
    [property: JsonPropertyName("multipart")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    MultipartUploadPlanResponse? Multipart);

public sealed record SignedUploadRequestResponse(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("headers")]
    IReadOnlyDictionary<string, string> Headers);

public sealed record MultipartUploadPlanResponse(
    [property: JsonPropertyName("maxParts")] int MaxParts,
    [property: JsonPropertyName("minPartBytes")] long MinPartBytes,
    [property: JsonPropertyName("maxPartBytes")] long MaxPartBytes,
    [property: JsonPropertyName("parts")]
    IReadOnlyList<SignedUploadPartResponse> Parts);

public sealed record SignedUploadPartResponse(
    [property: JsonPropertyName("partNumber")] int PartNumber,
    [property: JsonPropertyName("request")] SignedUploadRequestResponse Request,
    [property: JsonPropertyName("minBytes")] long MinBytes,
    [property: JsonPropertyName("maxBytes")] long MaxBytes,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

public sealed record UploadPartPlanResponse(
    [property: JsonPropertyName("parts")]
    IReadOnlyList<SignedUploadPartResponse> Parts);
