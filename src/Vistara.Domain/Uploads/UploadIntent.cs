using Vistara.Domain.Assets;

namespace Vistara.Domain.Uploads;

public sealed class UploadIdempotencyMetadata
{
    public UploadIdempotencyMetadata(
        string key,
        Sha256Checksum requestHash,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(requestHash);
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (key.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                "Idempotency keys cannot exceed 200 characters.");
        }

        Key = key.Trim();
        RequestHash = requestHash;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string Key { get; }

    public Sha256Checksum RequestHash { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public sealed class UploadIntent
{
    public UploadIntent(
        Guid tenantId,
        Guid actorId,
        UploadStrategy strategy,
        UploadIntegrityExpectation integrity,
        UploadIdempotencyMetadata idempotency,
        UploadReservationMetadata reservation)
    {
        EnsureId(tenantId, nameof(tenantId));
        EnsureId(actorId, nameof(actorId));
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(strategy),
                "The upload strategy is invalid.");
        }

        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.ReservedBytes < integrity.ExpectedSizeBytes)
        {
            throw new ArgumentException(
                "The upload reservation does not cover the expected object size.",
                nameof(reservation));
        }

        TenantId = tenantId;
        ActorId = actorId;
        Strategy = strategy;
        Integrity = integrity;
        Idempotency = idempotency;
        Reservation = reservation;
    }

    public Guid TenantId { get; }

    public Guid ActorId { get; }

    public UploadStrategy Strategy { get; }

    public UploadIntegrityExpectation Integrity { get; }

    public UploadIdempotencyMetadata Idempotency { get; }

    public UploadReservationMetadata Reservation { get; }

    private static void EnsureId(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("IDs must be non-empty UUIDv7 values.", parameterName);
        }
    }
}
