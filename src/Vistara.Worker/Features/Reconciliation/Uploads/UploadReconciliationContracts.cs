using Vistara.Application.Common.Storage;
using Vistara.Domain.Assets;

namespace Vistara.Worker.Features.Reconciliation.Uploads;

public enum UploadReconciliationSessionState
{
    Pending,
    Committing,
    Aborting,
    Expired,
    OutcomeUnknownCommit,
    OutcomeUnknownAbort,
    Aborted,
    Accepted,
    Quarantined,
}

public readonly record struct UploadReconciliationFence
{
    public UploadReconciliationFence(
        Guid tenantId,
        Guid uploadSessionId,
        long version,
        string leaseToken,
        DateTimeOffset leaseExpiresAtUtc)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(uploadSessionId, nameof(uploadSessionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        EnsureUtc(leaseExpiresAtUtc, nameof(leaseExpiresAtUtc));
        TenantId = tenantId;
        UploadSessionId = uploadSessionId;
        Version = version;
        LeaseToken = leaseToken.Trim();
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
    }

    public Guid TenantId { get; init; }

    public Guid UploadSessionId { get; init; }

    public long Version { get; init; }

    public string LeaseToken { get; init; }

    public DateTimeOffset LeaseExpiresAtUtc { get; init; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The value must be a UUIDv7.", parameterName);
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }
}

public sealed record UploadReconciliationCandidate
{
    public UploadReconciliationCandidate(
        UploadReconciliationFence fence,
        UploadReconciliationSessionState state,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset expiresAtUtc,
        BlobKey stagingKey,
        BlobVersion? expectedStagingVersion,
        BlobKey? canonicalKey,
        long expectedSizeBytes,
        Sha256Checksum expectedSha256,
        string? providerUploadId,
        bool reservationReleased,
        string continuationCursor,
        BlobMediaType? expectedContentType = null,
        MultipartSession? multipartSession = null,
        IReadOnlyList<UploadedPart>? completionParts = null)
    {
        EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        ArgumentNullException.ThrowIfNull(stagingKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSizeBytes);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationCursor);
        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Update time cannot precede creation.");
        }

        Fence = fence;
        State = state;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        StagingKey = stagingKey;
        ExpectedStagingVersion = expectedStagingVersion;
        CanonicalKey = canonicalKey;
        ExpectedSizeBytes = expectedSizeBytes;
        ExpectedSha256 = expectedSha256;
        ProviderUploadId =
            string.IsNullOrWhiteSpace(providerUploadId) ? null : providerUploadId.Trim();
        ReservationReleased = reservationReleased;
        ContinuationCursor = continuationCursor.Trim();
        ExpectedContentType = expectedContentType;
        MultipartSession = multipartSession;
        CompletionParts = completionParts ?? [];
    }

    public UploadReconciliationFence Fence { get; init; }

    public UploadReconciliationSessionState State { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public BlobKey StagingKey { get; init; }

    public BlobVersion? ExpectedStagingVersion { get; init; }

    public BlobKey? CanonicalKey { get; init; }

    public long ExpectedSizeBytes { get; init; }

    public Sha256Checksum ExpectedSha256 { get; init; }

    public string? ProviderUploadId { get; init; }

    public bool ReservationReleased { get; init; }

    public string ContinuationCursor { get; init; }

    public BlobMediaType? ExpectedContentType { get; init; }

    public MultipartSession? MultipartSession { get; init; }

    public IReadOnlyList<UploadedPart> CompletionParts { get; init; }

    public override string ToString() =>
        $"UploadReconciliationCandidate {{ State = {State}, sensitive values redacted }}";

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }
}

public sealed record UploadReconciliationOptions
{
    public TimeSpan MinimumObjectAge { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    public int MaximumSessionsPerRun { get; init; } = 100;

    public int MaximumStorageOperationsPerRun { get; init; } = 200;

    public int MaximumReportedActions { get; init; } = 100;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            MinimumObjectAge,
            TimeSpan.FromMinutes(1));
        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSessionsPerRun);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumStorageOperationsPerRun);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumReportedActions);
    }
}

public sealed record UploadReconciliationRunRequest
{
    public UploadReconciliationRunRequest(Guid runId, string? cursor, bool dryRun)
        : this(Guid.Empty, runId, cursor, dryRun, validateTenant: false)
    {
    }

    public UploadReconciliationRunRequest(
        Guid tenantId,
        Guid runId,
        string? cursor,
        bool dryRun)
        : this(tenantId, runId, cursor, dryRun, validateTenant: true)
    {
    }

    private UploadReconciliationRunRequest(
        Guid tenantId,
        Guid runId,
        string? cursor,
        bool dryRun,
        bool validateTenant)
    {
        if (validateTenant &&
            (tenantId == Guid.Empty || tenantId.Version != 7))
        {
            throw new ArgumentException(
                "Tenant ID must be UUIDv7.",
                nameof(tenantId));
        }

        if (runId == Guid.Empty || runId.Version != 7)
        {
            throw new ArgumentException("Run ID must be UUIDv7.", nameof(runId));
        }

        TenantId = tenantId;
        RunId = runId;
        Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
        DryRun = dryRun;
    }

    public Guid TenantId { get; }

    public Guid RunId { get; }

    public string? Cursor { get; }

    public bool DryRun { get; }
}

public sealed record UploadReconciliationScanRequest(
    string? Cursor,
    int MaximumSessions,
    DateTimeOffset UtcNow,
    TimeSpan LeaseDuration,
    bool DryRun,
    Guid TenantId = default);

public sealed record UploadReconciliationPage
{
    public UploadReconciliationPage(
        IReadOnlyList<UploadReconciliationCandidate> candidates,
        string? continuationCursor)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Candidates = candidates;
        ContinuationCursor = continuationCursor;
    }

    public IReadOnlyList<UploadReconciliationCandidate> Candidates { get; }

    public string? ContinuationCursor { get; }
}

public enum UploadReconciliationMutationStatus
{
    Applied,
    AlreadyApplied,
    Stale,
}

public sealed class UploadReconciliationMutationResult
{
    private UploadReconciliationMutationResult(
        UploadReconciliationMutationStatus status,
        UploadReconciliationCandidate? current,
        bool reservationReleased)
    {
        Status = status;
        Current = current;
        ReservationReleased = reservationReleased;
    }

    public UploadReconciliationMutationStatus Status { get; }

    public UploadReconciliationCandidate? Current { get; }

    public bool ReservationReleased { get; }

    public static UploadReconciliationMutationResult Applied(
        UploadReconciliationCandidate current,
        bool reservationReleased = false) =>
        new(
            UploadReconciliationMutationStatus.Applied,
            current ?? throw new ArgumentNullException(nameof(current)),
            reservationReleased);

    public static UploadReconciliationMutationResult AlreadyApplied(
        UploadReconciliationCandidate current) =>
        new(
            UploadReconciliationMutationStatus.AlreadyApplied,
            current ?? throw new ArgumentNullException(nameof(current)),
            false);

    public static UploadReconciliationMutationResult Stale() =>
        new(UploadReconciliationMutationStatus.Stale, null, false);
}

public enum ReconciliationQuarantineReason
{
    UnsafeStagingKey,
    OwnershipMismatch,
    CanonicalMismatch,
    CompletedMultipartMissingCanonical,
}

public interface IUploadReconciliationStatePort
{
    ValueTask<UploadReconciliationPage> ScanAsync(
        UploadReconciliationScanRequest request,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationCandidate?> RevalidateAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationMutationResult> ExpireAndReleaseAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationMutationResult> CompleteAbortAndReleaseAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationMutationResult> RecordAbortOutcomeUnknownAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationMutationResult> CompleteCommitAsync(
        UploadReconciliationFence fence,
        BlobIdentity stagingIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationMutationResult> CompleteCleanupAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationMutationResult> PreserveCanonicalAsync(
        UploadReconciliationFence fence,
        BlobIdentity canonicalIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask<UploadReconciliationMutationResult> QuarantineAsync(
        UploadReconciliationFence fence,
        ReconciliationQuarantineReason reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken);

    ValueTask SaveCheckpointAsync(
        Guid runId,
        string? cursor,
        CancellationToken cancellationToken);
}

public sealed record UploadReconciliationObjectHead
{
    public UploadReconciliationObjectHead(
        BlobIdentity identity,
        DateTimeOffset lastModifiedUtc,
        long contentLength,
        Sha256Checksum? sha256,
        Guid? ownerTenantId,
        Guid? ownerUploadSessionId,
        BlobMediaType? contentType = null,
        BlobEntityTag? entityTag = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (lastModifiedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(lastModifiedUtc));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        Identity = identity;
        LastModifiedUtc = lastModifiedUtc;
        ContentLength = contentLength;
        Sha256 = sha256;
        OwnerTenantId = ownerTenantId;
        OwnerUploadSessionId = ownerUploadSessionId;
        ContentType = contentType;
        EntityTag = entityTag;
    }

    public BlobIdentity Identity { get; init; }

    public DateTimeOffset LastModifiedUtc { get; init; }

    public long ContentLength { get; init; }

    public Sha256Checksum? Sha256 { get; init; }

    public Guid? OwnerTenantId { get; init; }

    public Guid? OwnerUploadSessionId { get; init; }

    public BlobMediaType? ContentType { get; init; }

    public BlobEntityTag? EntityTag { get; init; }
}

public enum UploadReconciliationHeadStatus
{
    Found,
    Missing,
    Retry,
}

public sealed class UploadReconciliationHeadResult
{
    private UploadReconciliationHeadResult(
        UploadReconciliationHeadStatus status,
        UploadReconciliationObjectHead? head)
    {
        Status = status;
        Head = head;
    }

    public UploadReconciliationHeadStatus Status { get; }

    public UploadReconciliationObjectHead? Head { get; }

    public static UploadReconciliationHeadResult Found(
        UploadReconciliationObjectHead head) =>
        new(
            UploadReconciliationHeadStatus.Found,
            head ?? throw new ArgumentNullException(nameof(head)));

    public static UploadReconciliationHeadResult Missing() =>
        new(UploadReconciliationHeadStatus.Missing, null);

    public static UploadReconciliationHeadResult Retry() =>
        new(UploadReconciliationHeadStatus.Retry, null);
}

public sealed record UploadReconciliationMultipart
{
    public UploadReconciliationMultipart(
        Guid tenantId,
        Guid uploadSessionId,
        string providerUploadId,
        BlobKey stagingKey,
        MultipartSession? session = null,
        IReadOnlyList<UploadedPart>? completionParts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUploadId);
        ArgumentNullException.ThrowIfNull(stagingKey);
        TenantId = tenantId;
        UploadSessionId = uploadSessionId;
        ProviderUploadId = providerUploadId.Trim();
        StagingKey = stagingKey;
        Session = session;
        CompletionParts = completionParts ?? [];
    }

    public Guid TenantId { get; }

    public Guid UploadSessionId { get; }

    public string ProviderUploadId { get; }

    public BlobKey StagingKey { get; }

    public MultipartSession? Session { get; }

    public IReadOnlyList<UploadedPart> CompletionParts { get; }
}

public enum ReconciliationMultipartState
{
    Active,
    Completed,
    Aborted,
    Missing,
    Unknown,
    Retry,
}

public enum ReconciliationProviderMutationOutcome
{
    Succeeded,
    Missing,
    Stale,
    OutcomeUnknown,
    Retry,
}

public interface IUploadReconciliationStoragePort
{
    ValueTask<UploadReconciliationHeadResult> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken);

    ValueTask<ReconciliationMultipartState> InspectMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken);

    ValueTask<ReconciliationProviderMutationOutcome> AbortMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken);

    ValueTask<ReconciliationProviderMutationOutcome> CompleteMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken);

    ValueTask<ReconciliationProviderMutationOutcome> DeleteStagingAsync(
        BlobIdentity identity,
        CancellationToken cancellationToken);
}

public enum ReconciliationActionKind
{
    ExpireSession,
    ReleaseReservation,
    InspectMultipart,
    AbortMultipart,
    CompleteMultipart,
    InspectStaging,
    DeleteStaging,
    InspectCanonical,
    PreserveCanonical,
    Quarantine,
}

public enum ReconciliationActionOutcome
{
    Planned,
    Applied,
    AlreadyApplied,
    Deferred,
    Stale,
    Refused,
}

public sealed record UploadReconciliationAction(
    ReconciliationActionKind Action,
    ReconciliationActionOutcome Outcome,
    string Resource)
{
    public static UploadReconciliationAction Redacted(
        ReconciliationActionKind action,
        ReconciliationActionOutcome outcome) =>
        new(action, outcome, "upload-session:[redacted]");
}

public sealed record UploadReconciliationCounts(
    int Scanned,
    int Revalidated,
    int ReservationsReleased,
    int SessionsExpired,
    int MultipartAborted,
    int MultipartCompleted,
    int StagingDeleted,
    int CanonicalPreserved,
    int Quarantined,
    int Deferred,
    int Stale,
    int StorageOperations);

public sealed record UploadReconciliationReport(
    bool DryRun,
    string? ContinuationCursor,
    UploadReconciliationCounts Counts,
    IReadOnlyList<UploadReconciliationAction> Actions);

public interface IUploadReconciliationObserver
{
    void Record(
        ReconciliationActionKind action,
        ReconciliationActionOutcome outcome);
}

public sealed class NullUploadReconciliationObserver : IUploadReconciliationObserver
{
    public static NullUploadReconciliationObserver Instance { get; } = new();

    private NullUploadReconciliationObserver()
    {
    }

    public void Record(
        ReconciliationActionKind action,
        ReconciliationActionOutcome outcome)
    {
    }
}

public enum ReconciliationCheckpoint
{
    CandidateRevalidated,
    MultipartInspected,
    MultipartAborted,
    ObjectInspected,
    Quarantined,
    SessionTransitioned,
    StagingDeleted,
    CursorSaved,
}

public interface IUploadReconciliationCheckpointObserver
{
    ValueTask ReachedAsync(
        ReconciliationCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

public sealed class NullUploadReconciliationCheckpointObserver
    : IUploadReconciliationCheckpointObserver
{
    public static NullUploadReconciliationCheckpointObserver Instance { get; } = new();

    private NullUploadReconciliationCheckpointObserver()
    {
    }

    public ValueTask ReachedAsync(
        ReconciliationCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
