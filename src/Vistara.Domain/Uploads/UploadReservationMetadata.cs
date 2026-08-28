namespace Vistara.Domain.Uploads;

public enum UploadReservationState
{
    Reserved,
    Consumed,
    Released,
    Expired,
}

public sealed class UploadReservationMetadata
{
    private UploadReservationMetadata(
        Guid id,
        long reservedBytes,
        int reservedObjects,
        long reservedComputeUnits,
        DateTimeOffset expiresAtUtc,
        UploadReservationState state)
    {
        Id = id;
        ReservedBytes = reservedBytes;
        ReservedObjects = reservedObjects;
        ReservedComputeUnits = reservedComputeUnits;
        ExpiresAtUtc = expiresAtUtc;
        State = state;
    }

    public Guid Id { get; }

    public long ReservedBytes { get; }

    public int ReservedObjects { get; }

    public long ReservedComputeUnits { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public UploadReservationState State { get; }

    public static UploadReservationMetadata Create(
        Guid id,
        long reservedBytes,
        int reservedObjects,
        long reservedComputeUnits,
        DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || id.Version != 7)
        {
            throw new ArgumentException("Reservation ID must be UUIDv7.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedObjects);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedComputeUnits);
        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(expiresAtUtc));
        }

        return new UploadReservationMetadata(
            id,
            reservedBytes,
            reservedObjects,
            reservedComputeUnits,
            expiresAtUtc,
            UploadReservationState.Reserved);
    }

    internal UploadReservationMetadata WithState(UploadReservationState state) =>
        State == state
            ? this
            : new UploadReservationMetadata(
                Id,
                ReservedBytes,
                ReservedObjects,
                ReservedComputeUnits,
                ExpiresAtUtc,
                state);
}
