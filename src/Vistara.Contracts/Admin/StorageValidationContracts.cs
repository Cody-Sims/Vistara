using System.Text.Json.Serialization;

namespace Vistara.Contracts.Admin;

/// <summary>
/// Answers whether this deployment can validate a candidate storage
/// configuration, and for which providers. The setup assistant reads this
/// before it offers to send a credential.
/// </summary>
public sealed record StorageValidationSupportResponse(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("providers")]
    IReadOnlyList<string> Providers);

/// <summary>
/// One capability check performed against a candidate storage configuration.
/// <c>detail</c> is drawn from a fixed catalogue and never contains provider
/// text or any part of a submitted credential.
/// </summary>
public sealed record StorageValidationCheckResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail);

/// <summary>
/// The only shape a validation answers with. The request is never echoed.
/// </summary>
public sealed record StorageValidationResponse(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("checks")]
    IReadOnlyList<StorageValidationCheckResponse> Checks,
    [property: JsonPropertyName("message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Message);
