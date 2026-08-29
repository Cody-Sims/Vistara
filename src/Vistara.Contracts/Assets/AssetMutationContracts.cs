using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Vistara.Contracts.Concurrency;

namespace Vistara.Contracts.Assets;

public sealed record UpdateAssetRequest(
    [property: JsonPropertyName("title")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Title = null,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? Description = null,
    [property: JsonPropertyName("visibility")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Visibility = null,
    [property: JsonPropertyName("capturedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    DateTimeOffset? CapturedAt = null);

public sealed record VersionedAssetReference(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("version")] ResourceVersion Version);

public sealed class AssetBulkMutationRequest
{
    [JsonConstructor]
    public AssetBulkMutationRequest(
        IReadOnlyList<VersionedAssetReference> items,
        AssetBulkActionRequest action)
    {
        Items = AssetContractValidation.CopyTargets(items, nameof(items));
        ArgumentNullException.ThrowIfNull(action);
        Action = action;
    }

    [JsonPropertyName("items")]
    public IReadOnlyList<VersionedAssetReference> Items { get; }

    [JsonPropertyName("action")]
    public AssetBulkActionRequest Action { get; }
}

public sealed class AssetBulkActionRequest
{
    [JsonConstructor]
    public AssetBulkActionRequest(
        string kind,
        Guid? tagId = null,
        Guid? albumId = null,
        bool? favorite = null,
        UpdateAssetRequest? metadata = null,
        string? reason = null)
    {
        Kind = ContractGuards.RequiredText(kind, nameof(kind), 32);
        TagId = tagId;
        AlbumId = albumId;
        Favorite = favorite;
        Metadata = metadata;
        Reason = reason;

        bool valid = Kind switch
        {
            "addTag" or "removeTag" =>
                TagId is not null &&
                AlbumId is null &&
                Favorite is null &&
                Metadata is null &&
                Reason is null,
            "addToAlbum" or "removeFromAlbum" =>
                AlbumId is not null &&
                TagId is null &&
                Favorite is null &&
                Metadata is null &&
                Reason is null,
            "setFavorite" =>
                Favorite is not null &&
                TagId is null &&
                AlbumId is null &&
                Metadata is null &&
                Reason is null,
            "updateMetadata" =>
                Metadata is not null &&
                TagId is null &&
                AlbumId is null &&
                Favorite is null &&
                Reason is null,
            "trash" =>
                !string.IsNullOrWhiteSpace(Reason) &&
                TagId is null &&
                AlbumId is null &&
                Favorite is null &&
                Metadata is null,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException(
                "The bulk action kind and arguments do not form a supported action.");
        }
    }

    [JsonPropertyName("kind")]
    public string Kind { get; }

    [JsonPropertyName("tagId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TagId { get; }

    [JsonPropertyName("albumId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AlbumId { get; }

    [JsonPropertyName("favorite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Favorite { get; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UpdateAssetRequest? Metadata { get; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; }
}

public sealed record OperationJobResponse(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("submittedCount")] int SubmittedCount,
    [property: JsonPropertyName("submittedAt")] DateTimeOffset SubmittedAt);

public sealed record AssetMutationResultResponse(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ResourceVersion? Version,
    [property: JsonPropertyName("errorCode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode);

internal static class AssetContractValidation
{
    public static IReadOnlyList<VersionedAssetReference> CopyTargets(
        IReadOnlyList<VersionedAssetReference> items,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(items, parameterName);
        if (items.Count is < 1 or > AssetContractLimits.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                items.Count,
                $"A batch must contain between 1 and {AssetContractLimits.MaximumBatchSize} assets.");
        }

        if (items.Any(item => item.Id == Guid.Empty))
        {
            throw new ArgumentException("Asset identifiers cannot be empty.", parameterName);
        }

        if (items.Select(item => item.Id).Distinct().Count() != items.Count)
        {
            throw new ArgumentException("Asset identifiers must be unique.", parameterName);
        }

        return new ReadOnlyCollection<VersionedAssetReference>(items.ToArray());
    }
}
