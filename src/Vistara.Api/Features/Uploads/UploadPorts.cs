using Microsoft.AspNetCore.Http;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Idempotency;

namespace Vistara.Api.Features.Uploads;

public enum UploadAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
    Concealed,
}

public sealed record UploadAccess
{
    private UploadAccess(UploadAccessStatus status, Guid? tenantId, Guid? actorId)
    {
        Status = status;
        TenantId = tenantId;
        ActorId = actorId;
    }

    public UploadAccessStatus Status { get; }

    public Guid? TenantId { get; }

    public Guid? ActorId { get; }

    public static UploadAccess Authorized(Guid tenantId, Guid actorId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        return new UploadAccess(UploadAccessStatus.Authorized, tenantId, actorId);
    }

    public static UploadAccess Denied(UploadAccessStatus status)
    {
        if (status == UploadAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new UploadAccess(status, null, null);
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The identifier must be UUIDv7.", parameterName);
        }
    }
}

public interface IUploadAuthorizationPort
{
    ValueTask<UploadAccess> AuthorizeCreateAsync(
        HttpContext context,
        CancellationToken cancellationToken);

    ValueTask<UploadAccess> AuthorizeSessionAsync(
        HttpContext context,
        Guid uploadId,
        CancellationToken cancellationToken);
}

public sealed record UploadProviderPolicy
{
    public UploadProviderPolicy(
        BlobStoreCapabilities capabilities,
        long maximumUploadBytes,
        long multipartThresholdBytes,
        TimeSpan planLifetime)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUploadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(multipartThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            planLifetime,
            TimeSpan.FromMinutes(5));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            planLifetime,
            TimeSpan.FromMinutes(10));
        Capabilities = capabilities;
        MaximumUploadBytes = maximumUploadBytes;
        MultipartThresholdBytes = multipartThresholdBytes;
        PlanLifetime = planLifetime;
    }

    public BlobStoreCapabilities Capabilities { get; }

    public long MaximumUploadBytes { get; }

    public long MultipartThresholdBytes { get; }

    public TimeSpan PlanLifetime { get; }
}

public sealed record ReserveUploadRequest(
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
    IdempotencyKey IdempotencyKey,
    DateTimeOffset ExpiresAtUtc);

public sealed record UploadPartSnapshot(
    int PartNumber,
    long SizeBytes,
    string? Checksum);

public sealed record UploadSessionSnapshot(
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
    IReadOnlyList<UploadPartSnapshot> Parts)
{
    public override string ToString() =>
        $"UploadSessionSnapshot {{ UploadId = {UploadId:D}, State = {State}, sensitive values redacted }}";
}

public enum UploadReserveStatus
{
    Created,
    Replayed,
    IdempotencyConflict,
    QuotaExceeded,
    Unavailable,
}

public sealed record UploadReserveResult
{
    private UploadReserveResult(
        UploadReserveStatus status,
        UploadSessionSnapshot? session,
        TimeSpan? retryAfter)
    {
        Status = status;
        Session = session;
        RetryAfter = retryAfter;
    }

    public UploadReserveStatus Status { get; }

    public UploadSessionSnapshot? Session { get; }

    public TimeSpan? RetryAfter { get; }

    public static UploadReserveResult Created(UploadSessionSnapshot session) =>
        new(UploadReserveStatus.Created, session, null);

    public static UploadReserveResult Replayed(UploadSessionSnapshot session) =>
        new(UploadReserveStatus.Replayed, session, null);

    public static UploadReserveResult Conflict() =>
        new(UploadReserveStatus.IdempotencyConflict, null, null);

    public static UploadReserveResult QuotaExceeded(TimeSpan? retryAfter = null) =>
        new(UploadReserveStatus.QuotaExceeded, null, retryAfter);

    public static UploadReserveResult Unavailable() =>
        new(UploadReserveStatus.Unavailable, null, null);
}

public sealed record UploadSignedRequest
{
    public UploadSignedRequest(
        string method,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);
        Method = method;
        Url = url;
        Headers = headers;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string Method { get; }

    public Uri Url { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public override string ToString() => $"{Method} [signed upload target redacted]";
}

public sealed record UploadSignedPartRequest(
    int PartNumber,
    UploadSignedRequest Request,
    long MinBytes,
    long MaxBytes);

public sealed record UploadIssuance
{
    private UploadIssuance(
        UploadSessionSnapshot session,
        UploadSignedRequest? directRequest,
        IReadOnlyList<UploadSignedPartRequest> parts,
        int maxParts,
        long minPartBytes,
        long maxPartBytes)
    {
        Session = session;
        DirectRequest = directRequest;
        Parts = parts;
        MaxParts = maxParts;
        MinPartBytes = minPartBytes;
        MaxPartBytes = maxPartBytes;
    }

    public UploadSessionSnapshot Session { get; }

    public UploadSignedRequest? DirectRequest { get; }

    public IReadOnlyList<UploadSignedPartRequest> Parts { get; }

    public int MaxParts { get; }

    public long MinPartBytes { get; }

    public long MaxPartBytes { get; }

    public static UploadIssuance Proxy(UploadSessionSnapshot session) =>
        new(session, null, [], 0, 0, 0);

    public static UploadIssuance Direct(
        UploadSessionSnapshot session,
        UploadSignedRequest request) =>
        new(session, request, [], 0, 0, 0);

    public static UploadIssuance Multipart(
        UploadSessionSnapshot session,
        IReadOnlyList<UploadSignedPartRequest> parts,
        int maxParts,
        long minPartBytes,
        long maxPartBytes) =>
        new(session, null, parts, maxParts, minPartBytes, maxPartBytes);
}

public enum UploadWriteStatus
{
    Written,
    Replayed,
    VersionConflict,
    InvalidState,
    Expired,
    TooLarge,
    IntegrityMismatch,
    Unavailable,
}

public sealed record UploadWriteResult(
    UploadWriteStatus Status,
    UploadSessionSnapshot? Session)
{
    public static UploadWriteResult Written(UploadSessionSnapshot session) =>
        new(UploadWriteStatus.Written, session);

    public static UploadWriteResult Replayed(UploadSessionSnapshot session) =>
        new(UploadWriteStatus.Replayed, session);

    public static UploadWriteResult Failure(UploadWriteStatus status) =>
        new(status, null);
}

public enum UploadPartPlanStatus
{
    Created,
    VersionConflict,
    InvalidState,
    Expired,
    Unavailable,
}

public sealed record UploadPartPlanResult(
    UploadPartPlanStatus Status,
    IReadOnlyList<UploadSignedPartRequest> Parts)
{
    public static UploadPartPlanResult Created(
        IReadOnlyList<UploadSignedPartRequest> parts) =>
        new(UploadPartPlanStatus.Created, parts);

    public static UploadPartPlanResult Failure(UploadPartPlanStatus status) =>
        new(status, []);
}

public sealed record CommittedUploadPart(
    int PartNumber,
    string EntityTag,
    string Checksum,
    long SizeBytes);

public enum UploadCommitStatus
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

public sealed record UploadCommitResult(
    UploadCommitStatus Status,
    UploadSessionSnapshot? Session)
{
    public static UploadCommitResult Queued(UploadSessionSnapshot session) =>
        new(UploadCommitStatus.Queued, session);

    public static UploadCommitResult Replayed(UploadSessionSnapshot session) =>
        new(UploadCommitStatus.Replayed, session);

    public static UploadCommitResult Accepted(UploadSessionSnapshot session) =>
        new(UploadCommitStatus.AlreadyAccepted, session);

    public static UploadCommitResult Failure(UploadCommitStatus status) =>
        new(status, null);
}

public enum UploadAbortStatus
{
    Aborted,
    AlreadyAborted,
    VersionConflict,
    InvalidState,
    Expired,
    Unavailable,
}

public sealed record UploadAbortResult(
    UploadAbortStatus Status,
    UploadSessionSnapshot? Session)
{
    public static UploadAbortResult Aborted(UploadSessionSnapshot session) =>
        new(UploadAbortStatus.Aborted, session);

    public static UploadAbortResult AlreadyAborted(UploadSessionSnapshot session) =>
        new(UploadAbortStatus.AlreadyAborted, session);

    public static UploadAbortResult Failure(UploadAbortStatus status) =>
        new(status, null);
}

public interface IUploadApplicationPort
{
    ValueTask<UploadProviderPolicy> GetProviderPolicyAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    ValueTask<UploadReserveResult> ReserveAsync(
        ReserveUploadRequest request,
        CancellationToken cancellationToken);

    ValueTask<UploadIssuance> IssueAsync(
        UploadSessionSnapshot session,
        CancellationToken cancellationToken);

    ValueTask<UploadSessionSnapshot?> GetAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken);

    ValueTask<UploadWriteResult> WriteProxyAsync(
        UploadSessionSnapshot session,
        Stream content,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<UploadPartPlanResult> RefreshPartPlansAsync(
        UploadSessionSnapshot session,
        IReadOnlyList<int> partNumbers,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<UploadCommitResult> CommitAsync(
        UploadSessionSnapshot session,
        IReadOnlyList<CommittedUploadPart> parts,
        IdempotencyKey idempotencyKey,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<UploadAbortResult> AbortAsync(
        UploadSessionSnapshot session,
        long expectedVersion,
        CancellationToken cancellationToken);
}
