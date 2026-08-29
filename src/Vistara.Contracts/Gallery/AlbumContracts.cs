using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Pagination;

namespace Vistara.Contracts.Gallery;

public sealed record AlbumListQuery(
    [property: Range(1, CursorPageRequest.MaximumLimit)]
    [property: JsonPropertyName("limit")] int Limit = CursorPageRequest.DefaultLimit,
    [property: JsonPropertyName("cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedCursor? Cursor = null);

public sealed record AlbumSummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description,
    [property: JsonPropertyName("cover")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AssetRenditionResponse? Cover,
    [property: JsonPropertyName("itemCount")] int ItemCount,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("version")] ResourceVersion Version);

public sealed record AlbumDetailResponse(
    [property: JsonPropertyName("album")] AlbumSummaryResponse Album,
    [property: JsonPropertyName("items")] CursorPage<AlbumItemResponse> Items);

public sealed record AlbumItemResponse(
    [property: JsonPropertyName("asset")] AssetSummaryResponse Asset,
    [property: JsonPropertyName("position")] long Position,
    [property: JsonPropertyName("addedAt")] DateTimeOffset AddedAt);

public sealed record CreateAlbumRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description = null);

public sealed record UpdateAlbumRequest(
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name = null,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? Description = null,
    [property: JsonPropertyName("coverAssetId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    Guid? CoverAssetId = null);

public sealed class AddAlbumItemsRequest
{
    [JsonConstructor]
    public AddAlbumItemsRequest(IReadOnlyList<VersionedAssetReference> items)
    {
        Items = AssetContractValidation.CopyTargets(items, nameof(items));
    }

    [JsonPropertyName("items")]
    public IReadOnlyList<VersionedAssetReference> Items { get; }
}

public sealed class RemoveAlbumItemsRequest
{
    [JsonConstructor]
    public RemoveAlbumItemsRequest(IReadOnlyList<VersionedAssetReference> items)
    {
        Items = AssetContractValidation.CopyTargets(items, nameof(items));
    }

    [JsonPropertyName("items")]
    public IReadOnlyList<VersionedAssetReference> Items { get; }
}

public sealed class ReorderAlbumItemsRequest
{
    [JsonConstructor]
    public ReorderAlbumItemsRequest(IReadOnlyList<AlbumItemPositionRequest> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > AssetContractLimits.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(items),
                items.Count,
                $"An order update must contain between 1 and {AssetContractLimits.MaximumBatchSize} items.");
        }

        if (items.Any(item => item.AssetId == Guid.Empty) ||
            items.Select(item => item.AssetId).Distinct().Count() != items.Count ||
            items.Select(item => item.Position).Distinct().Count() != items.Count)
        {
            throw new ArgumentException(
                "Album item identifiers and positions must be non-empty and unique.",
                nameof(items));
        }

        Items = new ReadOnlyCollection<AlbumItemPositionRequest>(items.ToArray());
    }

    [JsonPropertyName("items")]
    public IReadOnlyList<AlbumItemPositionRequest> Items { get; }
}

public sealed record AlbumItemPositionRequest(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("position")] long Position);
