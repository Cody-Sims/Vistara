using System.Text.Json.Serialization;

namespace Vistara.Contracts.Admin;

/// <summary>
/// Ephemeral connection details offered by the setup assistant. Nothing here
/// is persisted, logged, or echoed; the request exists only for the duration
/// of one validation.
/// </summary>
public sealed record ValidateStorageRequest(
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("filesystem")] FilesystemCandidate? Filesystem,
    [property: JsonPropertyName("azure")] AzureCandidate? Azure,
    [property: JsonPropertyName("s3")] S3Candidate? S3);

public sealed record FilesystemCandidate(
    [property: JsonPropertyName("rootPath")] string? RootPath);

public sealed record AzureCandidate(
    [property: JsonPropertyName("accountName")] string? AccountName,
    [property: JsonPropertyName("containerName")] string? ContainerName,
    [property: JsonPropertyName("serviceUri")] string? ServiceUri);

public sealed record S3Candidate(
    [property: JsonPropertyName("bucketName")] string? BucketName,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("serviceUrl")] string? ServiceUrl,
    [property: JsonPropertyName("forcePathStyle")] bool? ForcePathStyle);

/// <summary>
/// The only shape a validation answers with. It never carries provider text,
/// a resolved address, or any part of a submitted secret.
/// </summary>
public sealed record StorageValidationResponse(
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
