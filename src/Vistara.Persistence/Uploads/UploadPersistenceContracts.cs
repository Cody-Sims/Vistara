using Vistara.Application.Common.Storage;

namespace Vistara.Persistence.Uploads;

public sealed record PersistedUploadPart(
    int PartNumber,
    long SizeBytes,
    string? Checksum);

public sealed record PersistedUploadSession(
    Guid TenantId,
    Guid ActorId,
    Guid UploadId,
    string Strategy,
    string State,
    long ExpectedSizeBytes,
    string DeclaredContentType,
    string Sha256,
    string DisplayFileName,
    string StagingKey,
    DateTimeOffset ExpiresAtUtc,
    long Version,
    IReadOnlyList<PersistedUploadPart> Parts);

public sealed record PersistedUploadReserveCommand(
    Guid TenantId,
    Guid ActorId,
    Guid UploadId,
    string Strategy,
    string DisplayFileName,
    long ExpectedSizeBytes,
    string DeclaredContentType,
    string Sha256,
    string StagingKey,
    string RequestHash,
    string IdempotencyKey,
    DateTimeOffset ExpiresAtUtc);

public enum PersistedUploadReserveStatus
{
    Created,
    Replayed,
    IdempotencyConflict,
    QuotaExceeded,
    Unavailable,
}

public sealed record PersistedUploadReserveResult(
    PersistedUploadReserveStatus Status,
    PersistedUploadSession? Session);

public sealed record PersistedUploadIssuance(
    PersistedUploadSession Session,
    DirectUploadPlan? DirectPlan,
    MultipartSession? MultipartSession,
    IReadOnlyList<MultipartPartPlan> Parts);

public enum PersistedUploadWriteStatus
{
    Written,
    VersionConflict,
    InvalidState,
    Expired,
    TooLarge,
    IntegrityMismatch,
    Unavailable,
}

public sealed record PersistedUploadWriteResult(
    PersistedUploadWriteStatus Status,
    PersistedUploadSession? Session);

public enum PersistedUploadPartPlanStatus
{
    Created,
    VersionConflict,
    InvalidState,
    Expired,
    Unavailable,
}

public sealed record PersistedUploadPartPlanResult(
    PersistedUploadPartPlanStatus Status,
    IReadOnlyList<MultipartPartPlan> Parts);

public sealed record PersistedCommittedUploadPart(
    int PartNumber,
    string EntityTag,
    string? Checksum,
    long SizeBytes);

public enum PersistedUploadCommitStatus
{
    Queued,
    Replayed,
    AlreadyAccepted,
    IdempotencyConflict,
    VersionConflict,
    InvalidState,
    Expired,
    OutcomeUnknown,
    Unavailable,
}

public sealed record PersistedUploadCommitResult(
    PersistedUploadCommitStatus Status,
    PersistedUploadSession? Session);

public enum PersistedUploadAbortStatus
{
    Aborted,
    AlreadyAborted,
    VersionConflict,
    InvalidState,
    Expired,
    Unavailable,
}

public sealed record PersistedUploadAbortResult(
    PersistedUploadAbortStatus Status,
    PersistedUploadSession? Session);

public sealed class UploadPersistenceOptions
{
    public long MaximumUploadBytes { get; init; } = 50L * 1024 * 1024;

    public long MultipartThresholdBytes { get; init; } = 16L * 1024 * 1024;

    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan OutcomeReconciliationGrace { get; init; } =
        TimeSpan.FromHours(24);

    public string StorageContainer { get; init; } = "media";
}
