namespace Vistara.Application.Sharing;

[Flags]
public enum ShareAccess
{
    None = 0,
    View = 1,
    DownloadRenditions = 2,
    DownloadOriginal = 4,
}

public enum ShareMetadataExposure
{
    None,
    Basic,
}

public enum ShareTargetType
{
    Album,
    Snapshot,
}

public enum ShareLifecycleStatus
{
    Active,
    Expired,
    Revoked,
}

public sealed record ShareActor
{
    public ShareActor(Guid tenantId, Guid actorId)
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
            throw new ArgumentException("Sharing identities must use UUIDv7.", parameterName);
        }
    }
}

/// <summary>
/// A share target: the asset and the optimistic concurrency version the caller
/// last observed. The version is the asset resource version carried by the
/// asset ETag, never the blob revision number.
/// </summary>
public sealed record ShareAssetReference
{
    public ShareAssetReference(Guid assetId, long version)
    {
        if (assetId == Guid.Empty || assetId.Version != 7)
        {
            throw new ArgumentException("The asset ID must use UUIDv7.", nameof(assetId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        AssetId = assetId;
        Version = version;
    }

    public Guid AssetId { get; }

    public long Version { get; }
}

public sealed record ShareRendition(
    string Kind,
    string Path,
    int Width,
    int Height,
    string ContentType,
    ShareAccess RequiredAccess,
    string? DeliveryIdentifier = null);

/// <summary>
/// A captured share member. <paramref name="RevisionId"/> and
/// <paramref name="RevisionNumber"/> identify the stored blob revision, while
/// <paramref name="AssetVersion"/> is the asset resource version that the
/// capture was checked against and that the share contract publishes.
/// </summary>
public sealed record ShareAssetSnapshot(
    Guid AssetId,
    Guid RevisionId,
    long RevisionNumber,
    long AssetVersion,
    string Title,
    string? Description,
    DateTimeOffset? CapturedAtUtc,
    int Width,
    int Height,
    IReadOnlyList<ShareRendition> Renditions);

public sealed record ShareCreateCommand(
    string Name,
    ShareTargetType TargetType,
    Guid? AlbumId,
    IReadOnlyList<ShareAssetReference> SnapshotAssets,
    ShareAccess Permissions,
    ShareMetadataExposure MetadataExposure,
    DateTimeOffset? ExpiresAtUtc,
    string? Password);

public sealed record ShareUpdateCommand(
    string? Name,
    ShareAccess? Permissions,
    ShareMetadataExposure? MetadataExposure,
    DateTimeOffset? ExpiresAtUtc,
    bool SetExpiry);

public sealed record ShareRecord(
    Guid Id,
    Guid TenantId,
    Guid CreatedByActorId,
    string Name,
    ShareTargetType TargetType,
    Guid? AlbumId,
    IReadOnlyList<ShareAssetSnapshot> Assets,
    ShareAccess Permissions,
    ShareMetadataExposure MetadataExposure,
    string PepperVersionId,
    string TokenDigestHex,
    string? PasswordHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    Guid? RevokedByActorId,
    long Version,
    string RequestHash)
{
    public bool PasswordProtected => PasswordHash is not null;

    public ShareLifecycleStatus StatusAt(DateTimeOffset nowUtc) =>
        RevokedAtUtc.HasValue
            ? ShareLifecycleStatus.Revoked
            : ExpiresAtUtc.HasValue && nowUtc >= ExpiresAtUtc.Value
                ? ShareLifecycleStatus.Expired
                : ShareLifecycleStatus.Active;

    public override string ToString() =>
        $"ShareRecord {{ Id = {Id}, TenantId = {TenantId}, Name = {Name}, " +
        $"TokenDigestHex = [REDACTED], PasswordHash = [REDACTED], Version = {Version} }}";
}

public sealed record ShareSessionRecord(
    Guid Id,
    Guid TenantId,
    Guid ShareId,
    long ShareVersion,
    string PepperVersionId,
    string DigestHex,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() =>
        $"ShareSessionRecord {{ Id = {Id}, ShareId = {ShareId}, " +
        $"DigestHex = [REDACTED], ExpiresAtUtc = {ExpiresAtUtc} }}";
}

public sealed record SharePublicProjection(
    Guid ShareId,
    string Name,
    ShareLifecycleStatus Status,
    ShareAccess Permissions,
    ShareMetadataExposure MetadataExposure,
    bool PasswordRequired,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<ShareAssetSnapshot> Assets,
    string? NextCursor);

public sealed record SharePage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public enum SharePageStatus
{
    Available,
    InvalidCursor,
    InvalidQuery,
}

public sealed record SharePageResult<T>(
    SharePageStatus Status,
    SharePage<T>? Page);

public enum ShareCursorKind
{
    Managed,
    PublicAssets,
}

public sealed record ShareCursorState(
    ShareCursorKind Kind,
    Guid TenantId,
    Guid? ShareId,
    long? ShareVersion,
    string? Status,
    DateTimeOffset? LastCreatedAtUtc,
    Guid? LastId,
    int Offset,
    DateTimeOffset ExpiresAtUtc);

public enum ShareCreateStatus
{
    Created,
    TokenAlreadyIssued,
    IdempotencyConflict,
    Invalid,
    NotFound,
}

public sealed record ShareCreateResult(
    ShareCreateStatus Status,
    ShareRecord? Share,
    string? PublicToken,
    string? ErrorCode = null);

public enum ShareReadStatus
{
    Found,
    NotFound,
}

public sealed record ShareReadResult(
    ShareReadStatus Status,
    ShareRecord? Share);

public enum ShareMutationStatus
{
    Updated,
    Unchanged,
    Replayed,
    IdempotencyConflict,
    NotFound,
    VersionConflict,
    Invalid,
}

public sealed record ShareMutationResult(
    ShareMutationStatus Status,
    ShareRecord? Share,
    string? ErrorCode = null);

public enum SharePublicStatus
{
    Available,
    NotFound,
    Gone,
    InvalidSession,
}

public sealed record SharePublicResult(
    SharePublicStatus Status,
    SharePublicProjection? Share,
    string? ErrorCode = null);

public enum ShareChallengeStatus
{
    Authenticated,
    InvalidPassword,
    NotFound,
    Gone,
    RateLimited,
}

public sealed record ShareChallengeResult(
    ShareChallengeStatus Status,
    string? SessionToken,
    DateTimeOffset? ExpiresAtUtc,
    TimeSpan? RetryAfter = null);

public sealed record ShareOptions
{
    public ShareOptions(
        TimeSpan sessionLifetime,
        TimeSpan challengeWindow,
        int challengeLimit)
    {
        if (sessionLifetime <= TimeSpan.Zero ||
            sessionLifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime));
        }

        if (challengeWindow <= TimeSpan.Zero ||
            challengeWindow > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(challengeWindow));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(challengeLimit);
        SessionLifetime = sessionLifetime;
        ChallengeWindow = challengeWindow;
        ChallengeLimit = challengeLimit;
    }

    public TimeSpan SessionLifetime { get; }

    public TimeSpan ChallengeWindow { get; }

    public int ChallengeLimit { get; }
}
