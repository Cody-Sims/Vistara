using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Pagination;

namespace Vistara.Contracts.Assets;

public static class AssetContractLimits
{
    public const int MaximumBatchSize = 200;
    public const int MaximumFacetValues = 100;
}

public sealed record AssetListQuery(
    [property: Range(1, CursorPageRequest.MaximumLimit)]
    [property: JsonPropertyName("limit")] int Limit = CursorPageRequest.DefaultLimit,
    [property: JsonPropertyName("cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedCursor? Cursor = null,
    [property: JsonPropertyName("search")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Search = null,
    [property: JsonPropertyName("statuses")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Statuses = null,
    [property: JsonPropertyName("contentTypes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ContentTypes = null,
    [property: JsonPropertyName("albumId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? AlbumId = null,
    [property: JsonPropertyName("tagIds")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Guid>? TagIds = null,
    [property: JsonPropertyName("favorite")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Favorite = null,
    [property: JsonPropertyName("capturedFrom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CapturedFrom = null,
    [property: JsonPropertyName("capturedTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CapturedTo = null,
    [property: JsonPropertyName("importedFrom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ImportedFrom = null,
    [property: JsonPropertyName("importedTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ImportedTo = null,
    [property: JsonPropertyName("sort")] string Sort = "capturedAt",
    [property: JsonPropertyName("direction")] string Direction = "desc");

public sealed record AssetSummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("revisionNumber")] long RevisionNumber,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("capturedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("importedAt")] DateTimeOffset ImportedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("favorite")] bool Favorite,
    [property: JsonPropertyName("tags")] IReadOnlyList<AssetTagReferenceResponse> Tags,
    [property: JsonPropertyName("renditions")]
    IReadOnlyList<AssetRenditionResponse> Renditions,
    [property: JsonPropertyName("version")] ResourceVersion Version);

public sealed record AssetDetailResponse(
    [property: JsonPropertyName("asset")] AssetSummaryResponse Asset,
    [property: JsonPropertyName("metadata")] AssetMetadataSummaryResponse Metadata,
    [property: JsonPropertyName("albums")]
    IReadOnlyList<AssetAlbumReferenceResponse> Albums);

public sealed record AssetMetadataSummaryResponse(
    [property: JsonPropertyName("capturedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("orientation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Orientation,
    [property: JsonPropertyName("cameraMake")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CameraMake,
    [property: JsonPropertyName("cameraModel")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CameraModel,
    [property: JsonPropertyName("lensModel")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LensModel,
    [property: JsonPropertyName("colorSpace")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ColorSpace,
    [property: JsonPropertyName("restrictedMetadataAvailable")]
    bool RestrictedMetadataAvailable);

public sealed record AssetMetadataResponse(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("revisionNumber")] long RevisionNumber,
    [property: JsonPropertyName("summary")] AssetMetadataSummaryResponse Summary,
    [property: JsonPropertyName("safeProperties")]
    IReadOnlyDictionary<string, string> SafeProperties);

public sealed record AssetRenditionResponse(
    [property: JsonPropertyName("kind")] string Kind,
    // A same-origin application path, never a provider URL or storage key.
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("contentType")] string ContentType);

public sealed record AssetTagReferenceResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Color);

public sealed record AssetAlbumReferenceResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record TimelineQuery(
    [property: Range(1, CursorPageRequest.MaximumLimit)]
    [property: JsonPropertyName("limit")] int Limit = CursorPageRequest.DefaultLimit,
    [property: JsonPropertyName("cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedCursor? Cursor = null,
    [property: JsonPropertyName("search")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Search = null,
    [property: JsonPropertyName("statuses")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Statuses = null,
    [property: JsonPropertyName("contentTypes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ContentTypes = null,
    [property: JsonPropertyName("albumId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? AlbumId = null,
    [property: JsonPropertyName("tagIds")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Guid>? TagIds = null,
    [property: JsonPropertyName("favorite")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Favorite = null,
    [property: JsonPropertyName("capturedFrom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CapturedFrom = null,
    [property: JsonPropertyName("capturedTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CapturedTo = null,
    [property: JsonPropertyName("importedFrom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ImportedFrom = null,
    [property: JsonPropertyName("importedTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ImportedTo = null,
    [property: JsonPropertyName("sort")] string Sort = "capturedAt",
    [property: JsonPropertyName("direction")] string Direction = "desc",
    [property: JsonPropertyName("groupBy")] string GroupBy = "day");

public sealed record TimelineGroupResponse(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("startsAt")] DateTimeOffset StartsAt,
    [property: JsonPropertyName("endsAt")] DateTimeOffset EndsAt,
    [property: JsonPropertyName("items")] IReadOnlyList<AssetSummaryResponse> Items);

public sealed record TimelinePageResponse(
    [property: JsonPropertyName("groups")] IReadOnlyList<TimelineGroupResponse> Groups,
    [property: JsonPropertyName("nextCursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedCursor? NextCursor);

public sealed record SearchFacetsResponse(
    [property: JsonPropertyName("groups")] IReadOnlyList<SearchFacetGroupResponse> Groups);

public sealed record SearchFacetGroupResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("values")] IReadOnlyList<SearchFacetValueResponse> Values,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record SearchFacetValueResponse(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("count")] long Count);
