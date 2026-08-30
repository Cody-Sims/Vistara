namespace Vistara.Application.Gallery;

public sealed record CurationActor
{
    public CurationActor(Guid tenantId, Guid userId, bool canManageAll)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(userId, nameof(userId));
        TenantId = tenantId;
        UserId = userId;
        CanManageAll = canManageAll;
    }

    public Guid TenantId { get; }

    public Guid UserId { get; }

    public bool CanManageAll { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("A UUIDv7 identifier is required.", parameterName);
        }
    }
}

public enum CurationFailureKind
{
    Invalid,
    NotFound,
    Forbidden,
    Conflict,
    IdempotencyConflict,
    Unavailable,
}

public sealed record CurationFailure(
    CurationFailureKind Kind,
    string Code)
{
    public static CurationFailure Invalid(string code) =>
        new(CurationFailureKind.Invalid, code);

    public static CurationFailure NotFound(string code) =>
        new(CurationFailureKind.NotFound, code);

    public static CurationFailure Forbidden(string code) =>
        new(CurationFailureKind.Forbidden, code);

    public static CurationFailure Conflict(string code) =>
        new(CurationFailureKind.Conflict, code);

    public static CurationFailure IdempotencyConflict(string code) =>
        new(CurationFailureKind.IdempotencyConflict, code);

    public static CurationFailure Unavailable(string code) =>
        new(CurationFailureKind.Unavailable, code);
}

public sealed record CurationResult<T>
{
    private CurationResult(T? value, CurationFailure? failure)
    {
        Value = value;
        Error = failure;
    }

    public T? Value { get; }

    public CurationFailure? Error { get; }

    public bool IsSuccess => Error is null;

    internal static CurationResult<T> CreateSuccess(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    internal static CurationResult<T> CreateFailure(CurationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(default, failure);
    }
}

public static class CurationResult
{
    public static CurationResult<T> Success<T>(T value) =>
        CurationResult<T>.CreateSuccess(value);

    public static CurationResult<T> Failure<T>(CurationFailure failure) =>
        CurationResult<T>.CreateFailure(failure);
}

public readonly record struct OptionalField<T>(bool IsSpecified, T? Value);

public static class OptionalField
{
    public static OptionalField<T> Unspecified<T>() => new(false, default);

    public static OptionalField<T> Specified<T>(T? value) => new(true, value);
}

public sealed record VersionedAssetTarget(Guid AssetId, long Version);

public sealed record AlbumItemPosition(Guid AssetId, long Position);

public sealed record CuratedRenditionSnapshot(
    string Kind,
    string Path,
    int Width,
    int Height,
    string ContentType);

public sealed record CuratedTagReference(
    Guid Id,
    string Name,
    string? Color);

public sealed record CuratedAlbumReference(
    Guid Id,
    string Name);

public sealed record CuratedAssetSnapshot(
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
    IReadOnlyList<CuratedTagReference> Tags,
    IReadOnlyList<CuratedRenditionSnapshot> Renditions,
    long Version,
    IReadOnlyList<CuratedAlbumReference>? Albums = null);

public sealed record AlbumItemSnapshot(
    CuratedAssetSnapshot Asset,
    long Position,
    DateTimeOffset AddedAt);

public sealed record AlbumSnapshot(
    Guid Id,
    string Name,
    string? Description,
    CuratedRenditionSnapshot? Cover,
    int ItemCount,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyList<AlbumItemSnapshot> Items);

public sealed record TagSnapshot(
    Guid Id,
    string Name,
    string? Color,
    long AssetCount,
    long Version);

public sealed record BulkCurationTarget(Guid AssetId, long Version);

public sealed record BulkCurationAction(
    string Kind,
    Guid? TagId,
    Guid? AlbumId,
    bool? Favorite);

public sealed record BulkCurationRequest(
    IReadOnlyList<BulkCurationTarget> Items,
    BulkCurationAction Action);

public sealed record BulkCurationSubmission(
    Guid JobId,
    string State,
    int SubmittedCount,
    DateTimeOffset SubmittedAt);

public sealed record BulkCurationItemResult(
    Guid AssetId,
    string Status,
    long? Version,
    string? ErrorCode);

internal static class CurationValidation
{
    internal const int MaximumBatchSize = 200;

    internal static bool IsUuid7(Guid value) =>
        value != Guid.Empty && value.Version == 7;

    internal static bool IsUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero;

    internal static bool IsValidIdempotencyKey(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 255 &&
        value.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));

    internal static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
