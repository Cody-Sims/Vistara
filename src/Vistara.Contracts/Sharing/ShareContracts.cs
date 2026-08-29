using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Pagination;

namespace Vistara.Contracts.Sharing;

public static class ShareContractLimits
{
    public const int MaximumSnapshotAssets = AssetContractLimits.MaximumBatchSize;
}

public sealed record ShareListQuery(
    [property: Range(1, CursorPageRequest.MaximumLimit)]
    [property: JsonPropertyName("limit")] int Limit = CursorPageRequest.DefaultLimit,
    [property: JsonPropertyName("cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedCursor? Cursor = null,
    [property: JsonPropertyName("status")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Status = null);

public sealed record ShareSummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("target")] ShareTargetResponse Target,
    [property: JsonPropertyName("permissions")] SharePermissionsResponse Permissions,
    [property: JsonPropertyName("metadataExposure")] string MetadataExposure,
    [property: JsonPropertyName("passwordProtected")] bool PasswordProtected,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("revokedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("version")] ResourceVersion Version);

public sealed record ShareDetailResponse(
    [property: JsonPropertyName("share")] ShareSummaryResponse Share,
    [property: JsonPropertyName("snapshotAssets")]
    IReadOnlyList<VersionedAssetReference> SnapshotAssets);

public sealed record ShareTargetResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("albumId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? AlbumId,
    [property: JsonPropertyName("assetCount")] int AssetCount);

public sealed class SharePermissionsResponse
{
    [JsonConstructor]
    public SharePermissionsResponse(
        bool view,
        bool downloadRenditions,
        bool downloadOriginal)
    {
        if (!view)
        {
            throw new ArgumentException(
                "Every share must include view permission.",
                nameof(view));
        }

        View = view;
        DownloadRenditions = downloadRenditions;
        DownloadOriginal = downloadOriginal;
    }

    [JsonPropertyName("view")]
    public bool View { get; }

    [JsonPropertyName("downloadRenditions")]
    public bool DownloadRenditions { get; }

    [JsonPropertyName("downloadOriginal")]
    public bool DownloadOriginal { get; }
}

public sealed class CreateShareRequest
{
    [JsonConstructor]
    public CreateShareRequest(
        string name,
        string targetKind,
        Guid? albumId,
        IReadOnlyList<VersionedAssetReference>? snapshotAssets,
        SharePermissionsResponse permissions,
        string metadataExposure,
        DateTimeOffset? expiresAt = null,
        string? password = null)
    {
        Name = ContractGuards.RequiredText(name, nameof(name), 200);
        TargetKind = ContractGuards.RequiredText(targetKind, nameof(targetKind), 16);
        ArgumentNullException.ThrowIfNull(permissions);

        IReadOnlyList<VersionedAssetReference> copiedAssets =
            snapshotAssets is null
                ? Array.Empty<VersionedAssetReference>()
                : AssetContractValidation.CopyTargets(snapshotAssets, nameof(snapshotAssets));
        bool validTarget = TargetKind switch
        {
            "album" => albumId is not null && copiedAssets.Count == 0,
            "snapshot" => albumId is null && copiedAssets.Count > 0,
            _ => false,
        };
        if (!validTarget)
        {
            throw new ArgumentException(
                "The share target kind, album, and snapshot assets are inconsistent.",
                nameof(targetKind));
        }

        if (metadataExposure is not ("none" or "basic"))
        {
            throw new ArgumentException(
                "Metadata exposure must be 'none' or 'basic'.",
                nameof(metadataExposure));
        }

        if (expiresAt.HasValue && expiresAt.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The share expiry must use the UTC offset.",
                nameof(expiresAt));
        }

        AlbumId = albumId;
        SnapshotAssets = copiedAssets;
        Permissions = permissions;
        MetadataExposure = metadataExposure;
        ExpiresAt = expiresAt;
        Password = ContractGuards.OptionalText(password, nameof(password), 256);
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("targetKind")]
    public string TargetKind { get; }

    [JsonPropertyName("albumId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AlbumId { get; }

    [JsonPropertyName("snapshotAssets")]
    public IReadOnlyList<VersionedAssetReference> SnapshotAssets { get; }

    [JsonPropertyName("permissions")]
    public SharePermissionsResponse Permissions { get; }

    [JsonPropertyName("metadataExposure")]
    public string MetadataExposure { get; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; }

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; }
}

public sealed record CreatedShareResponse(
    [property: JsonPropertyName("share")] ShareDetailResponse Share,
    [property: JsonPropertyName("publicToken")] string PublicToken);

public sealed record UpdateShareRequest(
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name = null,
    [property: JsonPropertyName("permissions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SharePermissionsResponse? Permissions = null,
    [property: JsonPropertyName("metadataExposure")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? MetadataExposure = null,
    [property: JsonPropertyName("expiresAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    DateTimeOffset? ExpiresAt = null);

public sealed record PublicShareResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("permissions")] SharePermissionsResponse Permissions,
    [property: JsonPropertyName("metadataExposure")] string MetadataExposure,
    [property: JsonPropertyName("passwordRequired")] bool PasswordRequired,
    [property: JsonPropertyName("expiresAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("assets")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CursorPage<PublicSharedAssetResponse>? Assets);

public sealed record PublicSharedAssetResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description,
    [property: JsonPropertyName("capturedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("renditions")]
    IReadOnlyList<AssetRenditionResponse> Renditions);

public sealed record ShareChallengeRequest(
    [property: JsonPropertyName("password")] string Password);

public sealed record ShareChallengeResponse(
    [property: JsonPropertyName("authenticated")] bool Authenticated,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
