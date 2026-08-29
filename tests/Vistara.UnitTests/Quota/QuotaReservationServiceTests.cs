using Vistara.Application.Common;
using Vistara.Application.Uploads.Quotas;
using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;

namespace Vistara.UnitTests.Quota;

public sealed class QuotaReservationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2034, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static readonly TenantId TenantId = new(Guid.CreateVersion7(Now));
    private static readonly QuotaLimits Limits = new(
        QuotaLimit.Limited(2),
        QuotaLimit.Limited(100),
        QuotaLimit.Limited(2),
        QuotaLimit.Limited(50),
        QuotaLimit.Limited(3),
        QuotaLimit.Unlimited);

    [Fact]
    public async Task Exact_limit_is_accepted_and_active_reservations_are_counted()
    {
        FakeAtomicQuotaStore store = new();
        QuotaReservationService service = new(store, new FakeClock(Now));

        Result<QuotaReservationResult> first = await service.ReserveAsync(
            Request("first", 40),
            Limits,
            expectedUsageVersion: 0,
            CancellationToken.None);
        Result<QuotaReservationResult> second = await service.ReserveAsync(
            Request("second", 60),
            Limits,
            expectedUsageVersion: 1,
            CancellationToken.None);
        Result<QuotaReservationResult> over = await service.ReserveAsync(
            Request("third", 1),
            Limits,
            expectedUsageVersion: 2,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal("quota.limit_exceeded", over.Error?.Code);
        Assert.Equal(100, store.Snapshot.ActiveReservations.Bytes);
    }

    [Fact]
    public async Task Atomic_compare_and_reserve_exposes_concurrent_version_conflicts()
    {
        FakeAtomicQuotaStore store = new();
        QuotaReservationService service = new(store, new FakeClock(Now));

        Task<Result<QuotaReservationResult>> first = service.ReserveAsync(
            Request("parallel-a", 60),
            Limits,
            0,
            CancellationToken.None).AsTask();
        Task<Result<QuotaReservationResult>> second = service.ReserveAsync(
            Request("parallel-b", 60),
            Limits,
            0,
            CancellationToken.None).AsTask();

        Result<QuotaReservationResult>[] results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(
            results,
            result => result.Error?.Code == "quota.usage_version_conflict");
        Assert.Equal(1, store.AtomicReserveCalls);
        Assert.Equal(60, store.Snapshot.ActiveReservations.Bytes);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_reuses_matching_request_and_rejects_mismatch()
    {
        FakeAtomicQuotaStore store = new();
        QuotaReservationService service = new(store, new FakeClock(Now));
        QuotaReservationRequest original = Request("same-key", 20);

        Result<QuotaReservationResult> created = await service.ReserveAsync(
            original,
            Limits,
            0,
            CancellationToken.None);
        Result<QuotaReservationResult> repeated = await service.ReserveAsync(
            original with { ReservationId = original.ReservationId },
            Limits,
            1,
            CancellationToken.None);
        Result<QuotaReservationResult> mismatch = await service.ReserveAsync(
            Request("same-key", 21),
            Limits,
            1,
            CancellationToken.None);

        Assert.True(created.TryGetValue(out QuotaReservationResult? createdValue));
        Assert.True(repeated.TryGetValue(out QuotaReservationResult? repeatedValue));
        Assert.False(createdValue.Reused);
        Assert.True(repeatedValue.Reused);
        Assert.Equal(createdValue.Reservation.Id, repeatedValue.Reservation.Id);
        Assert.Equal("quota.duplicate_idempotency_key", mismatch.Error?.Code);
    }

    [Fact]
    public async Task Expiry_release_and_consume_are_idempotent_and_free_active_usage()
    {
        FakeClock clock = new(Now);
        FakeAtomicQuotaStore store = new();
        QuotaReservationService service = new(store, clock);

        QuotaReservation consumed = await Reserve(service, store, "consume", 20);
        Result<QuotaReservation> consume = await service.ConsumeAsync(
            consumed.Id,
            consumed.Version,
            CancellationToken.None);
        Result<QuotaReservation> consumeAgain = await service.ConsumeAsync(
            consumed.Id,
            consumeValue(consume).Version,
            CancellationToken.None);

        QuotaReservation released = await Reserve(service, store, "release", 30);
        Result<QuotaReservation> release = await service.ReleaseAsync(
            released.Id,
            released.Version,
            CancellationToken.None);
        Result<QuotaReservation> releaseAgain = await service.ReleaseAsync(
            released.Id,
            consumeValue(release).Version,
            CancellationToken.None);

        QuotaReservation expiring = await Reserve(
            service,
            store,
            "expire",
            40,
            expiresAt: Now.AddMinutes(1));
        clock.UtcNow = Now.AddMinutes(1);
        Result<QuotaReservation> expire = await service.ExpireAsync(
            expiring.Id,
            expiring.Version,
            CancellationToken.None);
        Result<QuotaReservation> expireAgain = await service.ExpireAsync(
            expiring.Id,
            consumeValue(expire).Version,
            CancellationToken.None);

        Assert.Equal(QuotaReservationState.Consumed, consumeValue(consumeAgain).State);
        Assert.Equal(QuotaReservationState.Released, consumeValue(releaseAgain).State);
        Assert.Equal(QuotaReservationState.Expired, consumeValue(expireAgain).State);
        Assert.Equal(QuotaAmounts.Zero, store.Snapshot.ActiveReservations);

        static QuotaReservation consumeValue(Result<QuotaReservation> result)
        {
            Assert.True(result.TryGetValue(out QuotaReservation? value));
            return value;
        }
    }

    [Fact]
    public async Task Expired_reservations_do_not_block_new_reservations()
    {
        FakeClock clock = new(Now);
        FakeAtomicQuotaStore store = new();
        QuotaReservationService service = new(store, clock);

        await Reserve(service, store, "old", 100, Now.AddSeconds(1));
        clock.UtcNow = Now.AddSeconds(1);

        Result<QuotaReservationResult> replacement = await service.ReserveAsync(
            Request("replacement", 100, Now.AddMinutes(5)),
            Limits,
            store.Snapshot.Version,
            CancellationToken.None);

        Assert.True(replacement.IsSuccess);
        Assert.Equal(100, store.Snapshot.ActiveReservations.Bytes);
    }

    [Fact]
    public async Task Service_uses_injected_time_and_honors_cancellation_before_store_calls()
    {
        FakeAtomicQuotaStore store = new();
        QuotaReservationService service = new(store, new FakeClock(Now));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.ReserveAsync(
                Request("cancelled", 1),
                Limits,
                0,
                cancellation.Token));

        Assert.Equal(0, store.Calls);
    }

    private static async Task<QuotaReservation> Reserve(
        QuotaReservationService service,
        FakeAtomicQuotaStore store,
        string key,
        long bytes,
        DateTimeOffset? expiresAt = null)
    {
        Result<QuotaReservationResult> result = await service.ReserveAsync(
            Request(key, bytes, expiresAt),
            Limits,
            store.Snapshot.Version,
            CancellationToken.None);
        Assert.True(result.TryGetValue(out QuotaReservationResult? value));
        return value.Reservation;
    }

    private static QuotaReservationRequest Request(
        string key,
        long bytes,
        DateTimeOffset? expiresAt = null) =>
        new(
            TenantId,
            Guid.CreateVersion7(Now.AddMilliseconds(bytes + key.Length)),
            key,
            $"{key}:{bytes}",
            new QuotaAmounts(1, bytes, 1, 0, 0, 0),
            expiresAt ?? Now.AddMinutes(5));

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class FakeAtomicQuotaStore : IQuotaReservationStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, QuotaReservation> _reservations = [];
        private readonly Dictionary<string, (string Fingerprint, Guid ReservationId)> _keys =
            new(StringComparer.Ordinal);

        public int Calls { get; private set; }

        public int AtomicReserveCalls { get; private set; }

        public QuotaUsageSnapshot Snapshot { get; private set; } =
            new(QuotaAmounts.Zero, QuotaAmounts.Zero, 0);

        public ValueTask<QuotaStoreReserveResult> TryReserveAsync(
            AtomicQuotaReservation command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Calls++;
                if (_keys.TryGetValue(command.IdempotencyKey, out var existingKey))
                {
                    QuotaReservation existing = _reservations[existingKey.ReservationId];
                    return ValueTask.FromResult(
                        existingKey.Fingerprint == command.RequestFingerprint
                            ? QuotaStoreReserveResult.Existing(existing, Snapshot)
                            : QuotaStoreReserveResult.DuplicateKey(Snapshot));
                }

                if (command.ExpectedUsageVersion != Snapshot.Version)
                {
                    return ValueTask.FromResult(
                        QuotaStoreReserveResult.VersionConflict(Snapshot));
                }

                ExpireActive(command.NowUtc);
                AtomicReserveCalls++;
                QuotaAmounts total = Snapshot.Committed.AddChecked(
                    Snapshot.ActiveReservations);
                if (!command.Limits.Allows(total, command.Amount))
                {
                    return ValueTask.FromResult(
                        QuotaStoreReserveResult.LimitExceeded(Snapshot));
                }

                QuotaReservation reservation = QuotaReservation.Create(
                    command.ReservationId,
                    command.TenantId,
                    command.IdempotencyKey,
                    command.RequestFingerprint,
                    command.Amount,
                    command.NowUtc,
                    command.ExpiresAtUtc);
                _reservations.Add(reservation.Id, reservation);
                _keys.Add(
                    command.IdempotencyKey,
                    (command.RequestFingerprint, reservation.Id));
                Snapshot = new(
                    Snapshot.Committed,
                    Snapshot.ActiveReservations.AddChecked(command.Amount),
                    checked(Snapshot.Version + 1));
                return ValueTask.FromResult(
                    QuotaStoreReserveResult.Reserved(reservation, Snapshot));
            }
        }

        public ValueTask<QuotaStoreTransitionResult> TransitionAsync(
            AtomicQuotaTransition command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Calls++;
                if (!_reservations.TryGetValue(command.ReservationId, out QuotaReservation? current))
                {
                    return ValueTask.FromResult(QuotaStoreTransitionResult.NotFound());
                }

                if (current.State == command.TargetState)
                {
                    return ValueTask.FromResult(
                        QuotaStoreTransitionResult.Existing(current, Snapshot));
                }

                if (current.Version != command.ExpectedVersion)
                {
                    return ValueTask.FromResult(
                        QuotaStoreTransitionResult.VersionConflict(current, Snapshot));
                }

                if (current.State != QuotaReservationState.Reserved ||
                    (command.TargetState == QuotaReservationState.Expired &&
                     command.NowUtc < current.ExpiresAtUtc) ||
                    (command.TargetState == QuotaReservationState.Consumed &&
                     command.NowUtc >= current.ExpiresAtUtc))
                {
                    return ValueTask.FromResult(
                        QuotaStoreTransitionResult.InvalidState(current, Snapshot));
                }

                QuotaReservation changed = current.Transition(command.TargetState);
                _reservations[current.Id] = changed;
                Snapshot = new(
                    Snapshot.Committed,
                    Snapshot.ActiveReservations.SubtractChecked(current.Amount),
                    checked(Snapshot.Version + 1));
                return ValueTask.FromResult(
                    QuotaStoreTransitionResult.Transitioned(changed, Snapshot));
            }
        }

        private void ExpireActive(DateTimeOffset now)
        {
            foreach (QuotaReservation reservation in _reservations.Values.ToArray())
            {
                if (reservation.State != QuotaReservationState.Reserved ||
                    now < reservation.ExpiresAtUtc)
                {
                    continue;
                }

                _reservations[reservation.Id] =
                    reservation.Transition(QuotaReservationState.Expired);
                Snapshot = new(
                    Snapshot.Committed,
                    Snapshot.ActiveReservations.SubtractChecked(reservation.Amount),
                    checked(Snapshot.Version + 1));
            }
        }
    }
}
