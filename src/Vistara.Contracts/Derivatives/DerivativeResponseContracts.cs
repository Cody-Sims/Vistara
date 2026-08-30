using System.Text.Json.Serialization;

namespace Vistara.Contracts.Derivatives;

public sealed record DerivativePresetCatalogResponse(
    [property: JsonPropertyName("presets")]
    IReadOnlyList<DerivativePresetContract> Presets);

public sealed record DerivativePresetContract(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("activeRevision")] int ActiveRevision,
    [property: JsonPropertyName("revisions")]
    IReadOnlyList<DerivativePresetRevisionContract> Revisions);

public sealed record DerivativePresetRevisionContract(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("active")] bool IsActive,
    [property: JsonPropertyName("parameters")]
    DerivativeParameterBoundsContract Parameters);

public sealed record DerivativeParameterBoundsContract(
    [property: JsonPropertyName("minimumWidth")] int? MinimumWidth,
    [property: JsonPropertyName("maximumWidth")] int? MaximumWidth,
    [property: JsonPropertyName("minimumHeight")] int? MinimumHeight,
    [property: JsonPropertyName("maximumHeight")] int? MaximumHeight,
    [property: JsonPropertyName("minimumQuality")] int? MinimumQuality,
    [property: JsonPropertyName("maximumQuality")] int? MaximumQuality,
    [property: JsonPropertyName("fits")] IReadOnlyList<string> Fits,
    [property: JsonPropertyName("formats")] IReadOnlyList<string> Formats);

public sealed record DerivativeCollectionResponse(
    [property: JsonPropertyName("items")]
    IReadOnlyList<DerivativeStatusResponse> Items);

public sealed record DerivativeStatusResponse(
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("parameters")]
    DerivativeParametersResponse Parameters,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("representation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DerivativeRepresentationResponse? Representation,
    [property: JsonPropertyName("failureCode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FailureCode);

public sealed record DerivativeParametersResponse(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("fit")] string Fit,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("quality")] int Quality,
    [property: JsonPropertyName("focalPoint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DerivativeFocalPointResponse? FocalPoint,
    [property: JsonPropertyName("crop")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DerivativeCropRectangleResponse? Crop);

public sealed record DerivativeFocalPointResponse(
    [property: JsonPropertyName("x")] decimal X,
    [property: JsonPropertyName("y")] decimal Y);

public sealed record DerivativeCropRectangleResponse(
    [property: JsonPropertyName("x")] decimal X,
    [property: JsonPropertyName("y")] decimal Y,
    [property: JsonPropertyName("width")] decimal Width,
    [property: JsonPropertyName("height")] decimal Height);

public sealed record DerivativeRepresentationResponse(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("etag")] string EntityTag);
