using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;

namespace Vistara.Application.Uploads.Quotas;

public sealed record QuotaReservationRequest(
    TenantId TenantId,
    Guid ReservationId,
    string IdempotencyKey,
    string RequestFingerprint,
    QuotaAmounts Amount,
    DateTimeOffset ExpiresAtUtc);

public sealed record QuotaReservationResult(
    QuotaReservation Reservation,
    QuotaUsageSnapshot Snapshot,
    bool Reused);

public sealed class QuotaReservationService(
    IQuotaReservationStore store,
    IClock clock)
{
    private static readonly ResultError LimitExceeded = ResultError.Unavailable(
        "quota.limit_exceeded",
        "The requested quota reservation exceeds a configured limit.");

    private static readonly ResultError UsageVersionConflict = ResultError.Conflict(
        "quota.usage_version_conflict",
        "Quota usage changed before the reservation could be created.");

    private static readonly ResultError DuplicateIdempotencyKey = ResultError.Conflict(
        "quota.duplicate_idempotency_key",
        "The idempotency key is already associated with another request.");

    private static readonly ResultError ReservationNotFound = ResultError.NotFound(
        "quota.reservation_not_found",
        "The quota reservation was not found.");

    private static readonly ResultError ReservationVersionConflict = ResultError.Conflict(
        "quota.reservation_version_conflict",
        "The quota reservation changed before the transition could be applied.");

    private static readonly ResultError InvalidReservationState = ResultError.Conflict(
        "quota.invalid_reservation_state",
        "The quota reservation cannot make the requested transition.");

    private readonly IQuotaReservationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async ValueTask<Result<QuotaReservationResult>> ReserveAsync(
        QuotaReservationRequest request,
        QuotaLimits limits,
        long expectedUsageVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedUsageVersion);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _clock.UtcNow;
        EnsureUtc(now, nameof(_clock));
        EnsureUtc(request.ExpiresAtUtc, nameof(request));
        if (request.ExpiresAtUtc <= now)
        {
            return Result.Failure<QuotaReservationResult>(InvalidReservationState);
        }

        QuotaStoreReserveResult result = await _store.TryReserveAsync(
            new AtomicQuotaReservation(
                request.TenantId,
                request.ReservationId,
                request.IdempotencyKey,
                request.RequestFingerprint,
                request.Amount,
                limits,
                expectedUsageVersion,
                now,
                request.ExpiresAtUtc),
            cancellationToken);

        return result.Status switch
        {
            QuotaStoreReserveStatus.Reserved => Success(result, reused: false),
            QuotaStoreReserveStatus.Existing => Success(result, reused: true),
            QuotaStoreReserveStatus.LimitExceeded =>
                Result.Failure<QuotaReservationResult>(LimitExceeded),
            QuotaStoreReserveStatus.VersionConflict =>
                Result.Failure<QuotaReservationResult>(UsageVersionConflict),
            QuotaStoreReserveStatus.DuplicateIdempotencyKey =>
                Result.Failure<QuotaReservationResult>(DuplicateIdempotencyKey),
            _ => throw new InvalidOperationException("Unknown quota store result."),
        };
    }

    public ValueTask<Result<QuotaReservation>> ConsumeAsync(
        Guid reservationId,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            reservationId,
            QuotaReservationState.Consumed,
            expectedVersion,
            cancellationToken);

    public ValueTask<Result<QuotaReservation>> ReleaseAsync(
        Guid reservationId,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            reservationId,
            QuotaReservationState.Released,
            expectedVersion,
            cancellationToken);

    public ValueTask<Result<QuotaReservation>> ExpireAsync(
        Guid reservationId,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            reservationId,
            QuotaReservationState.Expired,
            expectedVersion,
            cancellationToken);

    private async ValueTask<Result<QuotaReservation>> TransitionAsync(
        Guid reservationId,
        QuotaReservationState target,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        EnsureUtc(now, nameof(_clock));

        QuotaStoreTransitionResult result = await _store.TransitionAsync(
            new AtomicQuotaTransition(reservationId, target, expectedVersion, now),
            cancellationToken);

        return result.Status switch
        {
            QuotaStoreTransitionStatus.Transitioned or
            QuotaStoreTransitionStatus.Existing when result.Reservation is not null =>
                Result.Success(result.Reservation),
            QuotaStoreTransitionStatus.NotFound =>
                Result.Failure<QuotaReservation>(ReservationNotFound),
            QuotaStoreTransitionStatus.VersionConflict =>
                Result.Failure<QuotaReservation>(ReservationVersionConflict),
            QuotaStoreTransitionStatus.InvalidState =>
                Result.Failure<QuotaReservation>(InvalidReservationState),
            _ => throw new InvalidOperationException("Unknown quota store result."),
        };
    }

    private static Result<QuotaReservationResult> Success(
        QuotaStoreReserveResult result,
        bool reused)
    {
        if (result.Reservation is null)
        {
            throw new InvalidOperationException("The quota store omitted the reservation.");
        }

        return Result.Success(new QuotaReservationResult(
            result.Reservation,
            result.Snapshot,
            reused));
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamps must use UTC.", parameterName);
        }
    }
}
