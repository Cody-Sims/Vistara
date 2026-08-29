using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Uploads.Quotas;
using Vistara.Domain.Tenancy;
using Xunit;

namespace Vistara.IntegrationTests.UploadPersistence;

public sealed class QuotaPersistenceTests
{
    [Fact]
    public async Task Reservation_replay_conflict_and_consumption_survive_new_scopes()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid reservationId =
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        using ServiceProvider provider =
            database.CreateApiProvider(tenantId, new TestBlobStore());
        QuotaLimits limits = new(
            QuotaLimit.Limited(2),
            QuotaLimit.Limited(100),
            QuotaLimit.Limited(2),
            QuotaLimit.Unlimited,
            QuotaLimit.Unlimited,
            QuotaLimit.Unlimited);
        var command = new AtomicQuotaReservation(
            new TenantId(tenantId),
            reservationId,
            "quota-key",
            "quota-request",
            new QuotaAmounts(1, 40, 1, 0, 0, 0),
            limits,
            ExpectedUsageVersion: 0,
            UploadPersistenceDatabase.Now,
            UploadPersistenceDatabase.Now.AddMinutes(5));

        QuotaStoreReserveResult created;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IQuotaReservationStore store =
                scope.ServiceProvider.GetRequiredService<IQuotaReservationStore>();
            created = await store.TryReserveAsync(command, CancellationToken.None);
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IQuotaReservationStore store =
                scope.ServiceProvider.GetRequiredService<IQuotaReservationStore>();
            QuotaStoreReserveResult replay = await store.TryReserveAsync(
                command with
                {
                    ReservationId = Guid.CreateVersion7(
                        UploadPersistenceDatabase.Now.AddMilliseconds(3)),
                    ExpectedUsageVersion = 1,
                },
                CancellationToken.None);
            QuotaStoreReserveResult conflict = await store.TryReserveAsync(
                command with
                {
                    ReservationId = Guid.CreateVersion7(
                        UploadPersistenceDatabase.Now.AddMilliseconds(4)),
                    RequestFingerprint = "different",
                    ExpectedUsageVersion = 1,
                },
                CancellationToken.None);
            QuotaStoreTransitionResult consumed = await store.TransitionAsync(
                new AtomicQuotaTransition(
                    reservationId,
                    QuotaReservationState.Consumed,
                    created.Reservation!.Version,
                    UploadPersistenceDatabase.Now.AddMinutes(1)),
                CancellationToken.None);

            Assert.Equal(QuotaStoreReserveStatus.Existing, replay.Status);
            Assert.Equal(
                QuotaStoreReserveStatus.DuplicateIdempotencyKey,
                conflict.Status);
            Assert.Equal(
                QuotaStoreTransitionStatus.Transitioned,
                consumed.Status);
            Assert.Equal(QuotaReservationState.Consumed, consumed.Reservation?.State);
        }

        await using AsyncServiceScope restarted = provider.CreateAsyncScope();
        QuotaStoreTransitionResult replayedTransition = await restarted.ServiceProvider
            .GetRequiredService<IQuotaReservationStore>()
            .TransitionAsync(
                new AtomicQuotaTransition(
                    reservationId,
                    QuotaReservationState.Consumed,
                    ExpectedVersion: 2,
                    UploadPersistenceDatabase.Now.AddMinutes(2)),
                CancellationToken.None);
        Assert.Equal(
            QuotaStoreTransitionStatus.Existing,
            replayedTransition.Status);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Vistara.Persistence.Uploads.QuotaUsageRow usage =
            await context.QuotaUsage.SingleAsync();
        Assert.Equal(40, usage.CommittedBytes);
        Assert.Equal(1, usage.CommittedObjects);
        Assert.Equal(0, usage.ReservedBytes);
    }

    [Fact]
    public async Task Concurrent_compare_and_reserve_allows_only_one_writer()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        using ServiceProvider provider =
            database.CreateApiProvider(tenantId, new TestBlobStore());
        QuotaLimits limits = new(
            QuotaLimit.Limited(1),
            QuotaLimit.Limited(100),
            QuotaLimit.Limited(1),
            QuotaLimit.Unlimited,
            QuotaLimit.Unlimited,
            QuotaLimit.Unlimited);

        Task<QuotaStoreReserveResult> first = ReserveAsync(
            provider,
            tenantId,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2)),
            "parallel-a",
            limits);
        Task<QuotaStoreReserveResult> second = ReserveAsync(
            provider,
            tenantId,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(3)),
            "parallel-b",
            limits);

        QuotaStoreReserveResult[] results = await Task.WhenAll(first, second);

        Assert.Single(
            results,
            result => result.Status == QuotaStoreReserveStatus.Reserved);
        Assert.Single(
            results,
            result => result.Status is
                QuotaStoreReserveStatus.VersionConflict or
                QuotaStoreReserveStatus.LimitExceeded);
        Assert.Equal(1, await database.CountAsync("quota_reservations"));
    }

    private static async Task<QuotaStoreReserveResult> ReserveAsync(
        ServiceProvider provider,
        Guid tenantId,
        Guid reservationId,
        string key,
        QuotaLimits limits)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IQuotaReservationStore store =
            scope.ServiceProvider.GetRequiredService<IQuotaReservationStore>();
        return await store.TryReserveAsync(
            new AtomicQuotaReservation(
                new TenantId(tenantId),
                reservationId,
                key,
                $"{key}:60",
                new QuotaAmounts(1, 60, 1, 0, 0, 0),
                limits,
                ExpectedUsageVersion: 0,
                UploadPersistenceDatabase.Now,
                UploadPersistenceDatabase.Now.AddMinutes(5)),
            CancellationToken.None);
    }
}
