using Vistara.Application.Common.Auditing;
using Vistara.Application.Common.Events;
using Vistara.Domain.Assets;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Assets.Ingest;

public sealed record AuthoritativeBlobPromotion
{
    public AuthoritativeBlobPromotion(
        string storageProvider,
        string storageContainer,
        string objectKey,
        string? providerVersion,
        string? providerChecksum,
        Sha256Checksum sha256,
        long sizeBytes,
        MediaContentType contentType,
        MediaDescriptor media)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageContainer);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(media);
        if (media.DetectedContentType != contentType)
        {
            throw new ArgumentException(
                "Verified media and promoted content types must match.",
                nameof(media));
        }

        StorageProvider = storageProvider.Trim();
        StorageContainer = storageContainer.Trim();
        ObjectKey = objectKey.Trim();
        ProviderVersion = Normalize(providerVersion);
        ProviderChecksum = Normalize(providerChecksum);
        Sha256 = sha256;
        SizeBytes = sizeBytes;
        ContentType = contentType;
        Media = media;
    }

    public string StorageProvider { get; }

    public string StorageContainer { get; }

    public string ObjectKey { get; }

    public string? ProviderVersion { get; }

    public string? ProviderChecksum { get; }

    public Sha256Checksum Sha256 { get; }

    public long SizeBytes { get; }

    public MediaContentType ContentType { get; }

    public MediaDescriptor Media { get; }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AssetIngestCommand
{
    public AssetIngestCommand(
        Guid tenantId,
        Guid operationId,
        Guid uploadSessionId,
        long uploadVersion,
        Guid actorId,
        Guid reservationId,
        string title,
        AssetVisibility visibility,
        AuthoritativeBlobPromotion promotion)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(operationId, nameof(operationId));
        EnsureUuid7(uploadSessionId, nameof(uploadSessionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(uploadVersion);
        EnsureUuid7(actorId, nameof(actorId));
        EnsureUuid7(reservationId, nameof(reservationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!Enum.IsDefined(visibility))
        {
            throw new ArgumentOutOfRangeException(nameof(visibility));
        }

        ArgumentNullException.ThrowIfNull(promotion);
        TenantId = tenantId;
        OperationId = operationId;
        UploadSessionId = uploadSessionId;
        UploadVersion = uploadVersion;
        ActorId = actorId;
        ReservationId = reservationId;
        Title = title.Trim();
        Visibility = visibility;
        Promotion = promotion;
    }

    public Guid TenantId { get; }

    public Guid OperationId { get; }

    public Guid UploadSessionId { get; }

    public long UploadVersion { get; }

    public Guid ActorId { get; }

    public Guid ReservationId { get; }

    public string Title { get; }

    public AssetVisibility Visibility { get; }

    public AuthoritativeBlobPromotion Promotion { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The value must be a UUIDv7.", parameterName);
        }
    }
}

public sealed record AssetIngestBlobIdentity
{
    public AssetIngestBlobIdentity(
        Guid tenantId,
        string storageProvider,
        Sha256Checksum sha256,
        long sizeBytes)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("Tenant ID must be a UUIDv7.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(storageProvider);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        TenantId = tenantId;
        StorageProvider = storageProvider.Trim();
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    public Guid TenantId { get; }

    public string StorageProvider { get; }

    public Sha256Checksum Sha256 { get; }

    public long SizeBytes { get; }
}

public enum AssetIngestReservationState
{
    Reserved,
    Consumed,
    Released,
    Expired,
}

public sealed record AssetIngestReservation
{
    private AssetIngestReservation(
        Guid tenantId,
        Guid reservationId,
        AssetIngestReservationState state,
        long version,
        DateTimeOffset expiresAtUtc,
        Guid? consumedByOperationId,
        DateTimeOffset? consumedAtUtc)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
        State = state;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
        ConsumedByOperationId = consumedByOperationId;
        ConsumedAtUtc = consumedAtUtc;
    }

    public Guid TenantId { get; }

    public Guid ReservationId { get; }

    public AssetIngestReservationState State { get; }

    public long Version { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public Guid? ConsumedByOperationId { get; }

    public DateTimeOffset? ConsumedAtUtc { get; }

    public static AssetIngestReservation Reserved(
        Guid tenantId,
        Guid reservationId,
        long version,
        DateTimeOffset expiresAtUtc)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(reservationId, nameof(reservationId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        return new(
            tenantId,
            reservationId,
            AssetIngestReservationState.Reserved,
            version,
            expiresAtUtc,
            null,
            null);
    }

    public AssetIngestReservation Consume(
        Guid operationId,
        DateTimeOffset consumedAtUtc)
    {
        EnsureUuid7(operationId, nameof(operationId));
        EnsureUtc(consumedAtUtc, nameof(consumedAtUtc));
        if (State != AssetIngestReservationState.Reserved)
        {
            throw new InvalidOperationException("Only reserved quota can be consumed.");
        }

        if (consumedAtUtc >= ExpiresAtUtc)
        {
            throw new InvalidOperationException("Expired quota cannot be consumed.");
        }

        return new AssetIngestReservation(
            TenantId,
            ReservationId,
            AssetIngestReservationState.Consumed,
            checked(Version + 1),
            ExpiresAtUtc,
            operationId,
            consumedAtUtc);
    }

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
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public enum AssetIngestReservationConsumeStatus
{
    Consumed,
    NotFound,
    AlreadyConsumed,
    Expired,
    InvalidState,
    ConcurrencyConflict,
}

public sealed record AssetIngestReservationConsumeResult(
    AssetIngestReservationConsumeStatus Status,
    AssetIngestReservation? Reservation)
{
    public static AssetIngestReservationConsumeResult Consumed(
        AssetIngestReservation reservation) =>
        new(AssetIngestReservationConsumeStatus.Consumed, reservation);

    public static AssetIngestReservationConsumeResult NotFound() =>
        new(AssetIngestReservationConsumeStatus.NotFound, null);

    public static AssetIngestReservationConsumeResult AlreadyConsumed(
        AssetIngestReservation reservation) =>
        new(AssetIngestReservationConsumeStatus.AlreadyConsumed, reservation);

    public static AssetIngestReservationConsumeResult Expired(
        AssetIngestReservation reservation) =>
        new(AssetIngestReservationConsumeStatus.Expired, reservation);

    public static AssetIngestReservationConsumeResult InvalidState(
        AssetIngestReservation reservation) =>
        new(AssetIngestReservationConsumeStatus.InvalidState, reservation);

    public static AssetIngestReservationConsumeResult ConcurrencyConflict() =>
        new(AssetIngestReservationConsumeStatus.ConcurrencyConflict, null);
}

public sealed record AssetIngestActivation(
    Guid TenantId,
    Guid UploadSessionId,
    long ExpectedUploadVersion,
    Guid OperationId,
    Guid AssetId,
    Guid RevisionId,
    Guid BlobId,
    DateTimeOffset ActivatedAtUtc);

public sealed record AssetIngestReceipt(
    Guid TenantId,
    Guid OperationId,
    Guid UploadSessionId,
    Guid AssetId,
    Guid RevisionId,
    Guid BlobId,
    bool BlobReused,
    DateTimeOffset ActivatedAtUtc);

public enum AssetIngestDisposition
{
    Created,
    Replayed,
    Rejected,
    RetryableConflict,
}

public sealed record AssetIngestResult
{
    private AssetIngestResult(
        AssetIngestDisposition disposition,
        AssetIngestReceipt? receipt,
        ResultError? error)
    {
        Disposition = disposition;
        Receipt = receipt;
        Error = error;
    }

    public AssetIngestDisposition Disposition { get; }

    public AssetIngestReceipt? Receipt { get; }

    public ResultError? Error { get; }

    public bool IsRetryable => Disposition == AssetIngestDisposition.RetryableConflict;

    public static AssetIngestResult Created(AssetIngestReceipt receipt) =>
        new(AssetIngestDisposition.Created, receipt, null);

    public static AssetIngestResult Replayed(AssetIngestReceipt receipt) =>
        new(AssetIngestDisposition.Replayed, receipt, null);

    public static AssetIngestResult Rejected(ResultError error) =>
        new(AssetIngestDisposition.Rejected, null, error);

    public static AssetIngestResult RetryableConflict(ResultError error) =>
        new(AssetIngestDisposition.RetryableConflict, null, error);
}

public interface IAssetIngestUnitOfWork
{
    /// <summary>
    /// Commits only a created result. Replays and rejected results leave no mutations.
    /// A serialization or uniqueness race returns a retryable conflict.
    /// </summary>
    ValueTask<AssetIngestResult> ExecuteAsync(
        Guid tenantId,
        Guid operationId,
        Func<IAssetIngestTransaction, CancellationToken, ValueTask<AssetIngestResult>> action,
        CancellationToken cancellationToken);
}

public interface IAssetIngestTransaction
{
    ValueTask<AssetIngestReceipt?> FindOperationAsync(
        Guid tenantId,
        Guid operationId,
        CancellationToken cancellationToken);

    ValueTask<BlobObjectMetadata?> FindBlobAsync(
        AssetIngestBlobIdentity identity,
        CancellationToken cancellationToken);

    ValueTask AddBlobAsync(
        AssetIngestBlobIdentity identity,
        BlobObjectMetadata blob,
        CancellationToken cancellationToken);

    ValueTask AddAssetAsync(Asset asset, CancellationToken cancellationToken);

    ValueTask AddRevisionAsync(
        AssetRevision revision,
        CancellationToken cancellationToken);

    ValueTask<AssetIngestReservationConsumeResult> ConsumeReservationAsync(
        Guid tenantId,
        Guid reservationId,
        Guid operationId,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken);

    ValueTask AppendAuditAsync(
        AuditRecord record,
        CancellationToken cancellationToken);

    ValueTask AddJobAsync(DurableJob job, CancellationToken cancellationToken);

    ValueTask<EventSequence> ReserveEventSequenceAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    ValueTask AppendOutboxAsync(
        OutboxMessage message,
        CancellationToken cancellationToken);

    ValueTask MarkUploadActivatedAsync(
        AssetIngestActivation activation,
        CancellationToken cancellationToken);

    ValueTask RecordOperationAsync(
        Guid tenantId,
        Guid operationId,
        AssetIngestReceipt receipt,
        CancellationToken cancellationToken);
}
