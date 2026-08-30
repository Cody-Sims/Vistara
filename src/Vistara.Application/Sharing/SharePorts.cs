namespace Vistara.Application.Sharing;

public sealed record ShareSecretMaterial(
    string Plaintext,
    string PepperVersionId,
    string DigestHex)
{
    public override string ToString() => ShareAuditEvent.RedactedSecret;
}

public interface IShareRandomSource
{
    void Fill(Span<byte> destination);
}

public interface IShareTokenProtector
{
    ShareSecretMaterial Issue();

    bool TryDigest(
        string? plaintext,
        out string pepperVersionId,
        out string digestHex);
}

public interface ISharePasswordHasher
{
    string Hash(string password);

    bool Verify(string encodedHash, string password);

    string Fingerprint(string password);
}

public interface IShareSessionProtector
{
    ShareSecretMaterial Issue();

    bool TryDigest(
        string? plaintext,
        out string pepperVersionId,
        out string digestHex);
}

public interface IShareCursorProtector
{
    string Protect(ShareCursorState cursor);

    bool TryUnprotect(
        string? protectedCursor,
        out ShareCursorState cursor);
}

public interface IShareAssetCatalog
{
    ValueTask<IReadOnlyList<ShareAssetSnapshot>?> CaptureSnapshotAsync(
        Guid tenantId,
        ShareTargetType targetType,
        Guid? albumId,
        IReadOnlyList<ShareAssetReference> assets,
        CancellationToken cancellationToken);
}

public sealed record ShareAddResult
{
    private ShareAddResult(
        ShareAddStatus status,
        ShareRecord? share)
    {
        Status = status;
        Share = share;
    }

    public ShareAddStatus Status { get; }

    public ShareRecord? Share { get; }

    public static ShareAddResult Created(ShareRecord share) =>
        new(ShareAddStatus.Created, share);

    public static ShareAddResult Replayed(ShareRecord share) =>
        new(ShareAddStatus.Replayed, share);

    public static ShareAddResult IdempotencyConflict() =>
        new(ShareAddStatus.IdempotencyConflict, null);
}

public enum ShareAddStatus
{
    Created,
    Replayed,
    IdempotencyConflict,
}

public sealed record ShareUpdateResult
{
    private ShareUpdateResult(
        ShareUpdateStatus status,
        ShareRecord? share)
    {
        Status = status;
        Share = share;
    }

    public ShareUpdateStatus Status { get; }

    public ShareRecord? Share { get; }

    public static ShareUpdateResult Updated(ShareRecord share) =>
        new(ShareUpdateStatus.Updated, share);

    public static ShareUpdateResult Replayed(ShareRecord share) =>
        new(ShareUpdateStatus.Replayed, share);

    public static ShareUpdateResult Unchanged(ShareRecord share) =>
        new(ShareUpdateStatus.Unchanged, share);

    public static ShareUpdateResult NotFound() =>
        new(ShareUpdateStatus.NotFound, null);

    public static ShareUpdateResult VersionConflict() =>
        new(ShareUpdateStatus.VersionConflict, null);

    public static ShareUpdateResult IdempotencyConflict() =>
        new(ShareUpdateStatus.IdempotencyConflict, null);
}

public enum ShareUpdateStatus
{
    Updated,
    Replayed,
    Unchanged,
    NotFound,
    VersionConflict,
    IdempotencyConflict,
}

public interface IShareStore
{
    ValueTask<ShareIdempotencyRecord?> FindIdempotencyAsync(
        string idempotencyKeyHash,
        CancellationToken cancellationToken);

    ValueTask<ShareAddResult> AddAsync(
        ShareRecord share,
        string idempotencyKeyHash,
        string requestHash,
        CancellationToken cancellationToken);

    ValueTask<ShareRecord?> FindAsync(
        Guid tenantId,
        Guid shareId,
        CancellationToken cancellationToken);

    ValueTask<ShareRecord?> FindByIdAsync(
        Guid shareId,
        CancellationToken cancellationToken);

    ValueTask<ShareRecord?> FindByTokenDigestAsync(
        string pepperVersionId,
        string digestHex,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ShareRecord>> ListAsync(
        Guid tenantId,
        int limit,
        string? status,
        DateTimeOffset nowUtc,
        DateTimeOffset? beforeCreatedAtUtc,
        Guid? beforeId,
        CancellationToken cancellationToken);

    ValueTask<ShareUpdateResult> UpdateAsync(
        ShareRecord updated,
        long expectedVersion,
        string idempotencyKeyHash,
        string requestHash,
        CancellationToken cancellationToken);

    ValueTask AddSessionAsync(
        ShareSessionRecord session,
        CancellationToken cancellationToken);

    ValueTask<ShareSessionRecord?> FindSessionAsync(
        string pepperVersionId,
        string digestHex,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}

public sealed record ShareIdempotencyRecord(
    Guid ShareId,
    string RequestHash);

public sealed record ShareRateLimitDecision(
    bool IsAllowed,
    TimeSpan? RetryAfter);

public interface IShareChallengeRateLimiter
{
    ValueTask<ShareRateLimitDecision> TryAcquireAsync(
        string keyHash,
        DateTimeOffset nowUtc,
        TimeSpan window,
        int limit,
        CancellationToken cancellationToken);
}

public enum ShareAuditAction
{
    Created,
    CreateRejected,
    Viewed,
    ViewRejected,
    Challenged,
    ChallengeRejected,
    Updated,
    Revoked,
}

public sealed record ShareAuditEvent(
    ShareAuditAction Action,
    Guid? TenantId,
    Guid? ShareId,
    Guid? ActorId,
    string? ReasonCode,
    DateTimeOffset OccurredAtUtc)
{
    public const string RedactedSecret = "[REDACTED]";

    public string PresentedSecret { get; } = RedactedSecret;
}

public interface IShareAuditSink
{
    ValueTask WriteAsync(
        ShareAuditEvent auditEvent,
        CancellationToken cancellationToken);
}

public sealed record SharePublicCredential(
    string? PublicToken,
    string? SessionToken)
{
    public override string ToString() => ShareAuditEvent.RedactedSecret;
}
