using Vistara.Domain.Tenancy;

namespace Vistara.Application.Uploads.Quotas;

public sealed record QuotaUsageSnapshot(
    QuotaAmounts Committed,
    QuotaAmounts ActiveReservations,
    long Version)
{
    public QuotaAmounts TotalChecked() => Committed.AddChecked(ActiveReservations);
}

public sealed record AtomicQuotaReservation(
    TenantId TenantId,
    Guid ReservationId,
    string IdempotencyKey,
    string RequestFingerprint,
    QuotaAmounts Amount,
    QuotaLimits Limits,
    long ExpectedUsageVersion,
    DateTimeOffset NowUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record AtomicQuotaTransition(
    Guid ReservationId,
    QuotaReservationState TargetState,
    long ExpectedVersion,
    DateTimeOffset NowUtc);

public enum QuotaStoreReserveStatus
{
    Reserved,
    Existing,
    LimitExceeded,
    VersionConflict,
    DuplicateIdempotencyKey,
}

public sealed record QuotaStoreReserveResult(
    QuotaStoreReserveStatus Status,
    QuotaReservation? Reservation,
    QuotaUsageSnapshot Snapshot)
{
    public static QuotaStoreReserveResult Reserved(
        QuotaReservation reservation,
        QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreReserveStatus.Reserved, reservation, snapshot);

    public static QuotaStoreReserveResult Existing(
        QuotaReservation reservation,
        QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreReserveStatus.Existing, reservation, snapshot);

    public static QuotaStoreReserveResult LimitExceeded(QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreReserveStatus.LimitExceeded, null, snapshot);

    public static QuotaStoreReserveResult VersionConflict(QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreReserveStatus.VersionConflict, null, snapshot);

    public static QuotaStoreReserveResult DuplicateKey(QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreReserveStatus.DuplicateIdempotencyKey, null, snapshot);
}

public enum QuotaStoreTransitionStatus
{
    Transitioned,
    Existing,
    NotFound,
    VersionConflict,
    InvalidState,
}

public sealed record QuotaStoreTransitionResult(
    QuotaStoreTransitionStatus Status,
    QuotaReservation? Reservation,
    QuotaUsageSnapshot? Snapshot)
{
    public static QuotaStoreTransitionResult Transitioned(
        QuotaReservation reservation,
        QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreTransitionStatus.Transitioned, reservation, snapshot);

    public static QuotaStoreTransitionResult Existing(
        QuotaReservation reservation,
        QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreTransitionStatus.Existing, reservation, snapshot);

    public static QuotaStoreTransitionResult NotFound() =>
        new(QuotaStoreTransitionStatus.NotFound, null, null);

    public static QuotaStoreTransitionResult VersionConflict(
        QuotaReservation reservation,
        QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreTransitionStatus.VersionConflict, reservation, snapshot);

    public static QuotaStoreTransitionResult InvalidState(
        QuotaReservation reservation,
        QuotaUsageSnapshot snapshot) =>
        new(QuotaStoreTransitionStatus.InvalidState, reservation, snapshot);
}

public interface IQuotaReservationStore
{
    ValueTask<QuotaStoreReserveResult> TryReserveAsync(
        AtomicQuotaReservation command,
        CancellationToken cancellationToken);

    ValueTask<QuotaStoreTransitionResult> TransitionAsync(
        AtomicQuotaTransition command,
        CancellationToken cancellationToken);
}
