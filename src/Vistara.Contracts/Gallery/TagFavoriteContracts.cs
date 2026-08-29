using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Pagination;

namespace Vistara.Contracts.Gallery;

public sealed record TagListQuery(
    [property: Range(1, CursorPageRequest.MaximumLimit)]
    [property: JsonPropertyName("limit")] int Limit = CursorPageRequest.DefaultLimit,
    [property: JsonPropertyName("cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedCursor? Cursor = null,
    [property: JsonPropertyName("search")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Search = null);

public sealed record TagResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Color,
    [property: JsonPropertyName("assetCount")] long AssetCount,
    [property: JsonPropertyName("version")] ResourceVersion Version);

public sealed record CreateTagRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Color = null);

public sealed record UpdateTagRequest(
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name = null,
    [property: JsonPropertyName("color")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? Color = null);
