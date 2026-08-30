using System.Text.Json.Serialization;

namespace Vistara.Contracts.Jobs;

/// <summary>
/// The redacted, tenant-safe view of a durable job returned by
/// <c>GET /api/v1/jobs/{id}</c>.
/// </summary>
public sealed record JobStatusResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("maxAttempts")] int MaxAttempts,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("availableAt")] DateTimeOffset AvailableAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("failure")] JobFailureResponse? Failure,
    [property: JsonPropertyName("version")] long Version);

public sealed record JobFailureResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("summary")] string Summary);
