using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vistara.Application.Gallery.Queries;

public enum AssetSort
{
    CapturedAt,
    ImportedAt,
    UpdatedAt,
    Title,
    SizeBytes,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

public sealed class AssetQueryCriteria
{
    private const int MaximumSearchLength = 500;
    private const int MaximumFilterValues = 20;

    private AssetQueryCriteria(
        int limit,
        string? search,
        IReadOnlyList<string> statuses,
        IReadOnlyList<string> contentTypes,
        Guid? albumId,
        IReadOnlyList<Guid> tagIds,
        bool? favorite,
        DateTimeOffset? capturedFrom,
        DateTimeOffset? capturedTo,
        DateTimeOffset? importedFrom,
        DateTimeOffset? importedTo,
        AssetSort sort,
        SortDirection direction)
    {
        Limit = limit;
        Search = search;
        Statuses = statuses;
        ContentTypes = contentTypes;
        AlbumId = albumId;
        TagIds = tagIds;
        Favorite = favorite;
        CapturedFrom = capturedFrom;
        CapturedTo = capturedTo;
        ImportedFrom = importedFrom;
        ImportedTo = importedTo;
        Sort = sort;
        Direction = direction;
        FilterHash = ComputeFilterHash();
    }

    public int Limit { get; }
    public string? Search { get; }
    public IReadOnlyList<string> Statuses { get; }
    public IReadOnlyList<string> ContentTypes { get; }
    public Guid? AlbumId { get; }
    public IReadOnlyList<Guid> TagIds { get; }
    public bool? Favorite { get; }
    public DateTimeOffset? CapturedFrom { get; }
    public DateTimeOffset? CapturedTo { get; }
    public DateTimeOffset? ImportedFrom { get; }
    public DateTimeOffset? ImportedTo { get; }
    public AssetSort Sort { get; }
    public SortDirection Direction { get; }
    public string FilterHash { get; }

    public static AssetQueryCriteria Create(
        int limit = 60,
        string? search = null,
        IReadOnlyList<string>? statuses = null,
        IReadOnlyList<string>? contentTypes = null,
        Guid? albumId = null,
        IReadOnlyList<Guid>? tagIds = null,
        bool? favorite = null,
        DateTimeOffset? capturedFrom = null,
        DateTimeOffset? capturedTo = null,
        DateTimeOffset? importedFrom = null,
        DateTimeOffset? importedTo = null,
        string sort = "capturedAt",
        string direction = "desc")
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        string? normalizedSearch = string.IsNullOrWhiteSpace(search)
            ? null
            : search.Trim();
        if (normalizedSearch?.Length > MaximumSearchLength)
        {
            throw new ArgumentOutOfRangeException(nameof(search));
        }

        IReadOnlyList<string> normalizedStatuses = NormalizeStrings(
            statuses,
            nameof(statuses),
            value => value is "Processing" or "Ready" or "Failed");
        IReadOnlyList<string> normalizedContentTypes = NormalizeStrings(
            contentTypes,
            nameof(contentTypes),
            value => value is "image/jpeg" or "image/png" or "image/webp");
        IReadOnlyList<Guid> normalizedTagIds = NormalizeIds(tagIds);
        ValidateUuid7(albumId, nameof(albumId));
        ValidateRange(capturedFrom, capturedTo, nameof(capturedFrom));
        ValidateRange(importedFrom, importedTo, nameof(importedFrom));

        AssetSort normalizedSort = sort switch
        {
            "capturedAt" => AssetSort.CapturedAt,
            "importedAt" => AssetSort.ImportedAt,
            "updatedAt" => AssetSort.UpdatedAt,
            "title" => AssetSort.Title,
            "sizeBytes" => AssetSort.SizeBytes,
            _ => throw new ArgumentException("The asset sort is unsupported.", nameof(sort)),
        };
        SortDirection normalizedDirection = direction switch
        {
            "asc" => SortDirection.Ascending,
            "desc" => SortDirection.Descending,
            _ => throw new ArgumentException(
                "The asset sort direction is unsupported.",
                nameof(direction)),
        };

        return new AssetQueryCriteria(
            limit,
            normalizedSearch,
            normalizedStatuses,
            normalizedContentTypes,
            albumId,
            normalizedTagIds,
            favorite,
            capturedFrom?.ToUniversalTime(),
            capturedTo?.ToUniversalTime(),
            importedFrom?.ToUniversalTime(),
            importedTo?.ToUniversalTime(),
            normalizedSort,
            normalizedDirection);
    }

    private static string[] NormalizeStrings(
        IReadOnlyList<string>? values,
        string parameterName,
        Func<string, bool> allow)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count > MaximumFilterValues)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        string[] normalized = values
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(value => !allow(value)))
        {
            throw new ArgumentException("A filter value is unsupported.", parameterName);
        }

        return normalized;
    }

    private static Guid[] NormalizeIds(IReadOnlyList<Guid>? values)
    {
        if (values is null)
        {
            return [];
        }

        if (values.Count > MaximumFilterValues)
        {
            throw new ArgumentOutOfRangeException(nameof(values));
        }

        Guid[] normalized = values.Distinct().Order().ToArray();
        if (normalized.Any(value => value == Guid.Empty || value.Version != 7))
        {
            throw new ArgumentException("Filter IDs must be UUIDv7.", nameof(values));
        }

        return normalized;
    }

    private static void ValidateUuid7(Guid? value, string parameterName)
    {
        if (value is not null && (value == Guid.Empty || value.Value.Version != 7))
        {
            throw new ArgumentException("The identifier must be UUIDv7.", parameterName);
        }
    }

    private static void ValidateRange(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string parameterName)
    {
        if (from > to)
        {
            throw new ArgumentException("The date range is invalid.", parameterName);
        }
    }

    private string ComputeFilterHash()
    {
        var canonical = new StringBuilder(512);
        Append(canonical, Search);
        Append(canonical, string.Join(',', Statuses));
        Append(canonical, string.Join(',', ContentTypes));
        Append(canonical, AlbumId?.ToString("D"));
        Append(canonical, string.Join(',', TagIds.Select(id => id.ToString("D"))));
        Append(canonical, Favorite?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, CapturedFrom?.ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, CapturedTo?.ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, ImportedFrom?.ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, ImportedTo?.ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, Sort.ToString());
        Append(canonical, Direction.ToString());
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder builder, string? value) =>
        builder.Append(value?.Length ?? -1)
            .Append(':')
            .Append(value)
            .Append('|');
}

public sealed record AssetQueryScope
{
    public AssetQueryScope(Guid tenantId, Guid actorId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        TenantId = tenantId;
        ActorId = actorId;
    }

    public Guid TenantId { get; }
    public Guid ActorId { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The identifier must be UUIDv7.", parameterName);
        }
    }
}

public sealed record AssetTag(Guid Id, string Name, string? Color);

public sealed record AssetAlbum(Guid Id, string Name);

public sealed record AssetDeliverySource(
    string Kind,
    string Path,
    int Width,
    int Height,
    string ContentType);

public sealed record AssetQueryItem(
    Guid Id,
    string Title,
    string? Description,
    string Status,
    string Visibility,
    long RevisionNumber,
    string ContentType,
    string Format,
    int Width,
    int Height,
    long SizeBytes,
    DateTimeOffset? CapturedAt,
    DateTimeOffset ImportedAt,
    DateTimeOffset UpdatedAt,
    bool Favorite,
    IReadOnlyList<AssetTag> Tags,
    IReadOnlyList<AssetDeliverySource> Renditions,
    long Version);

public sealed record AssetMetadata(
    Guid AssetId,
    long RevisionNumber,
    DateTimeOffset? CapturedAt,
    int? Orientation,
    string? CameraMake,
    string? CameraModel,
    string? LensModel,
    string? ColorSpace,
    bool RestrictedMetadataAvailable,
    IReadOnlyDictionary<string, string> SafeProperties);

public sealed record AssetDetail(
    AssetQueryItem Asset,
    AssetMetadata Metadata,
    IReadOnlyList<AssetAlbum> Albums);

public sealed record AssetFacetValue(string Value, string Label, long Count);

public sealed record AssetFacetGroup(
    string Name,
    IReadOnlyList<AssetFacetValue> Values,
    bool Truncated);

public sealed record AssetQueryKey(
    int NullRank,
    DateTimeOffset? InstantValue,
    string? TextValue,
    long? NumberValue,
    Guid AssetId);

public sealed record AssetQueryWindow(
    DateTimeOffset SnapshotAtUtc,
    AssetQueryKey? Continuation);

public sealed record AssetQuerySlice(
    IReadOnlyList<AssetQueryItem> Items,
    AssetQueryKey? NextKey,
    bool HasMore);

public sealed record AssetQueryPage(
    IReadOnlyList<AssetQueryItem> Items,
    string? NextCursor);

public sealed record AssetMetadataPatch(
    bool HasTitle,
    string? Title,
    bool HasDescription,
    string? Description,
    bool HasVisibility,
    string? Visibility,
    bool HasCapturedAt,
    DateTimeOffset? CapturedAt);

public enum AssetUpdateStoreStatus
{
    Updated,
    Replayed,
    NotFound,
    VersionConflict,
    ValidationFailed,
}

public sealed record AssetUpdateStoreResult(
    AssetUpdateStoreStatus Status,
    AssetDetail? Detail)
{
    public static AssetUpdateStoreResult Updated(AssetDetail detail) =>
        new(AssetUpdateStoreStatus.Updated, detail);

    public static AssetUpdateStoreResult Replayed(AssetDetail detail) =>
        new(AssetUpdateStoreStatus.Replayed, detail);

    public static AssetUpdateStoreResult NotFound() =>
        new(AssetUpdateStoreStatus.NotFound, null);

    public static AssetUpdateStoreResult VersionConflict() =>
        new(AssetUpdateStoreStatus.VersionConflict, null);

    public static AssetUpdateStoreResult ValidationFailed() =>
        new(AssetUpdateStoreStatus.ValidationFailed, null);
}

public interface IAssetQueryStore
{
    ValueTask<AssetQuerySlice> QueryAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        AssetQueryWindow window,
        CancellationToken cancellationToken);

    ValueTask<AssetDetail?> GetAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken);

    ValueTask<AssetMetadata?> GetMetadataAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AssetFacetGroup>> GetFacetsAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        DateTimeOffset snapshotAtUtc,
        CancellationToken cancellationToken);

    ValueTask<AssetUpdateStoreResult> UpdateAsync(
        AssetQueryScope scope,
        Guid assetId,
        long expectedVersion,
        string idempotencyKey,
        AssetMetadataPatch patch,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);
}
