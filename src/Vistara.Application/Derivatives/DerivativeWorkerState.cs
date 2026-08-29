using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Derivatives;

public sealed record DerivativeAcquireRequest
{
    public DerivativeAcquireRequest(
        Guid tenantId,
        Guid requestId,
        DerivativeJobPayloadV1 payload,
        string storageProvider,
        ImagePipelineFingerprint pipelineFingerprint,
        JobLease jobLease,
        DateTimeOffset nowUtc,
        TimeSpan ownershipDuration)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(requestId, nameof(requestId));
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageProvider);
        ArgumentNullException.ThrowIfNull(pipelineFingerprint);
        ArgumentNullException.ThrowIfNull(jobLease);
        if (jobLease.JobId.Value != requestId)
        {
            throw new ArgumentException(
                "The derivative request must use the leased job identity.",
                nameof(jobLease));
        }

        EnsureUtc(nowUtc, nameof(nowUtc));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            ownershipDuration,
            TimeSpan.Zero);
        TenantId = tenantId;
        RequestId = requestId;
        Payload = payload;
        StorageProvider = storageProvider.Trim();
        PipelineFingerprint = pipelineFingerprint;
        JobLease = jobLease;
        NowUtc = nowUtc;
        OwnershipDuration = ownershipDuration;
    }

    public Guid TenantId { get; }

    public Guid RequestId { get; }

    public DerivativeJobPayloadV1 Payload { get; }

    public string StorageProvider { get; }

    public ImagePipelineFingerprint PipelineFingerprint { get; }

    public JobLease JobLease { get; }

    public DateTimeOffset NowUtc { get; }

    public TimeSpan OwnershipDuration { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Derivative IDs must be UUIDv7 values.", parameterName);
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public readonly record struct DerivativeFence
{
    public DerivativeFence(
        Guid tenantId,
        Guid requestId,
        long version,
        DateTimeOffset expiresAtUtc,
        JobLease jobLease)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(requestId, nameof(requestId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(expiresAtUtc));
        }

        ArgumentNullException.ThrowIfNull(jobLease);
        if (jobLease.JobId.Value != requestId)
        {
            throw new ArgumentException(
                "The derivative fence must use the leased job identity.",
                nameof(jobLease));
        }

        TenantId = tenantId;
        RequestId = requestId;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
        JobLease = jobLease;
    }

    public Guid TenantId { get; }

    public Guid RequestId { get; }

    public long Version { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public JobLease JobLease { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The value must be a UUIDv7.", parameterName);
        }
    }
}

public sealed record DerivativeWorkItem
{
    public DerivativeWorkItem(
        Guid requestId,
        DerivativeGenerationRequest generation,
        BlobKey sourceKey,
        BlobVersion sourceVersion,
        long sourceLength)
    {
        if (requestId == Guid.Empty || requestId.Version != 7)
        {
            throw new ArgumentException("Request ID must be a UUIDv7.", nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(sourceVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceLength);
        RequestId = requestId;
        Generation = generation;
        SourceKey = sourceKey;
        SourceVersion = sourceVersion;
        SourceLength = sourceLength;
    }

    public Guid RequestId { get; }

    public DerivativeGenerationRequest Generation { get; }

    public BlobKey SourceKey { get; }

    public BlobVersion SourceVersion { get; }

    public long SourceLength { get; }
}

public enum DerivativeAcquireDisposition
{
    Acquired,
    Ready,
    Completed,
    Busy,
    NotFound,
}

public sealed class DerivativeAcquireResult
{
    private DerivativeAcquireResult(
        DerivativeAcquireDisposition disposition,
        DerivativeFence? fence,
        DerivativeWorkItem? work,
        DerivativeStagedOutput? staged)
    {
        Disposition = disposition;
        Fence = fence;
        Work = work;
        Staged = staged;
    }

    public DerivativeAcquireDisposition Disposition { get; }

    public DerivativeFence? Fence { get; }

    public DerivativeWorkItem? Work { get; }

    public DerivativeStagedOutput? Staged { get; }

    public static DerivativeAcquireResult Acquired(
        DerivativeFence fence,
        DerivativeWorkItem work,
        DerivativeStagedOutput? staged) =>
        new(DerivativeAcquireDisposition.Acquired, fence, work, staged);

    public static DerivativeAcquireResult Ready(
        DerivativeFence fence,
        DerivativeWorkItem work,
        DerivativeStagedOutput? staged) =>
        new(DerivativeAcquireDisposition.Ready, fence, work, staged);

    public static DerivativeAcquireResult Completed() =>
        new(DerivativeAcquireDisposition.Completed, null, null, null);

    public static DerivativeAcquireResult Busy() =>
        new(DerivativeAcquireDisposition.Busy, null, null, null);

    public static DerivativeAcquireResult NotFound() =>
        new(DerivativeAcquireDisposition.NotFound, null, null, null);
}

public sealed record DerivativeStagedOutput(
    BlobIdentity Identity,
    long Bytes,
    ImageSha256 Sha256,
    BlobMediaType ContentType);

public sealed record DerivativeReadyOutput(
    DerivativeFence Fence,
    DerivativeGenerationResult Result,
    BlobHead Head,
    DateTimeOffset ReadyAtUtc);

public enum DerivativeFailureCode
{
    SourceRevisionChanged,
    MediaDecodeFailed,
    UnsafeProcessorOutput,
    DestinationIdentityConflict,
}

public sealed record DerivativeFailure(
    DerivativeFence Fence,
    DerivativeFailureCode Code,
    bool Retryable,
    DateTimeOffset FailedAtUtc);

public enum DerivativeStateWriteResult
{
    Applied,
    Stale,
}

public enum DerivativePublicationAttemptOutcome
{
    Published,
    OutcomeUnknown,
    Retry,
}

public enum DerivativePublicationOutcome
{
    Published,
    OutcomeUnknown,
    Retry,
    Stale,
}

public delegate ValueTask<DerivativePublicationAttemptOutcome>
    DerivativePublicationOperation(CancellationToken cancellationToken);

public interface IDerivativeStatePort
{
    ValueTask<DerivativeAcquireResult> AcquireAsync(
        DerivativeAcquireRequest request,
        CancellationToken cancellationToken);

    ValueTask<DerivativeStateWriteResult> RecordStagedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        CancellationToken cancellationToken);

    ValueTask<DerivativeStateWriteResult> RecordPublishOutcomeUnknownAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invokes publication only while ownership transfer is excluded by the durable store.
    /// </summary>
    ValueTask<DerivativePublicationOutcome> PublishIfOwnedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        DerivativePublicationOperation publish,
        CancellationToken cancellationToken);

    ValueTask<DerivativeStateWriteResult> MarkReadyAsync(
        DerivativeReadyOutput ready,
        CancellationToken cancellationToken);

    ValueTask<DerivativeStateWriteResult> MarkFailedAsync(
        DerivativeFailure failure,
        CancellationToken cancellationToken);

    ValueTask<DerivativeStateWriteResult> CompleteCleanupAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken);
}

public enum DerivativeCheckpoint
{
    OwnershipAcquired,
    SourceVerified,
    OutputTransformed,
    OutputStaged,
    DestinationPublished,
    DestinationVisible,
    ReadyCommitted,
    StagingDeleted,
    CleanupCommitted,
}

public interface IDerivativeCheckpointObserver
{
    ValueTask ReachedAsync(
        DerivativeCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

public sealed class NullDerivativeCheckpointObserver : IDerivativeCheckpointObserver
{
    public static NullDerivativeCheckpointObserver Instance { get; } = new();

    private NullDerivativeCheckpointObserver()
    {
    }

    public ValueTask ReachedAsync(
        DerivativeCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
