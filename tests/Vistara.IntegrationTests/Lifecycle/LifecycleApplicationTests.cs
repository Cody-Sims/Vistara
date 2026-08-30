using Vistara.Application.Common;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.IntegrationTests.Lifecycle;

public sealed class LifecycleApplicationTests
{
    private static readonly DateTimeOffset Now =
        new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);
    private static readonly Guid TenantId =
        Guid.Parse("0199c111-1111-7111-8111-111111111111");
    private static readonly Guid ActorId =
        Guid.Parse("0199c222-2222-7222-8222-222222222222");
    private static readonly Guid AssetId =
        Guid.Parse("0199c333-3333-7333-8333-333333333333");

    [Fact]
    public async Task Trash_uses_the_default_thirty_day_recovery_window()
    {
        var store = new RecordingLifecycleStore();
        var service = new LifecycleService(
            store,
            new FixedClock(Now),
            new SequenceUuid7Generator());
        LifecycleActorContext actor = LifecycleActorContext.Human(
            TenantId,
            ActorId,
            LifecycleRights.Trash,
            Now);

        Result<IReadOnlyList<LifecycleAssetMutationResult>> result =
            await service.TrashAsync(
                actor,
                [new LifecycleAssetTarget(AssetId, 4)],
                "cleanup",
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(store.LastTrash);
        Assert.Equal(Now, store.LastTrash.DeletedAtUtc);
        Assert.Equal(Now.AddDays(30), store.LastTrash.PurgeAtUtc);
    }

    [Fact]
    public async Task Purge_confirmation_requires_recent_human_reauthentication()
    {
        var store = new RecordingLifecycleStore();
        var service = new LifecycleService(
            store,
            new FixedClock(Now),
            new SequenceUuid7Generator());
        LifecycleActorContext apiKey = LifecycleActorContext.ApiKey(
            TenantId,
            ActorId,
            LifecycleRights.Purge);
        LifecycleActorContext staleHuman = LifecycleActorContext.Human(
            TenantId,
            ActorId,
            LifecycleRights.Purge,
            Now.AddMinutes(-6));

        Result<LifecyclePurgeBatchSnapshot> apiKeyResult =
            await service.ConfirmPurgeAsync(
                apiKey,
                Guid.CreateVersion7(Now.AddMilliseconds(20)),
                expectedVersion: 2,
                new string('a', 64),
                "confirm-api-key",
                CancellationToken.None);
        Result<LifecyclePurgeBatchSnapshot> staleHumanResult =
            await service.ConfirmPurgeAsync(
                staleHuman,
                Guid.CreateVersion7(Now.AddMilliseconds(21)),
                expectedVersion: 2,
                new string('b', 64),
                "confirm-stale-human",
                CancellationToken.None);

        Assert.Equal(
            "lifecycle.reauthentication_required",
            apiKeyResult.Error?.Code);
        Assert.Equal(
            "lifecycle.reauthentication_required",
            staleHumanResult.Error?.Code);
        Assert.Equal(0, store.ConfirmCalls);
    }

    [Fact]
    public async Task Purge_confirmation_accepts_only_fresh_primary_credential_proof_for_the_actor()
    {
        var store = new RecordingLifecycleStore();
        var service = new LifecycleService(
            store,
            new FixedClock(Now),
            new SequenceUuid7Generator());
        LifecycleActorContext freshHuman = LifecycleActorContext.Human(
            TenantId,
            ActorId,
            LifecycleRights.Purge,
            new LifecycleReauthenticationContext(
                ActorId,
                Now.AddMinutes(-5),
                LifecycleAuthenticationStrength.PrimaryCredential));

        Result<LifecyclePurgeBatchSnapshot> result =
            await service.ConfirmPurgeAsync(
                freshHuman,
                Guid.CreateVersion7(Now.AddMilliseconds(22)),
                expectedVersion: 2,
                new string('c', 64),
                "confirm-fresh-human",
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.ConfirmCalls);
        Assert.Equal(ActorId, store.LastConfirm?.ActorId);
    }

    [Fact]
    public void Lifecycle_reauthentication_proof_must_belong_to_the_exact_actor()
    {
        Guid differentActor =
            Guid.Parse("0199c222-2222-7222-8222-222222222223");

        Assert.Throws<ArgumentException>(() =>
            LifecycleActorContext.Human(
                TenantId,
                ActorId,
                LifecycleRights.Purge,
                new LifecycleReauthenticationContext(
                    differentActor,
                    Now,
                    LifecycleAuthenticationStrength.PrimaryCredential)));
    }

    private sealed class RecordingLifecycleStore : ILifecycleStore
    {
        public LifecycleTrashCommand? LastTrash { get; private set; }

        public int ConfirmCalls { get; private set; }

        public LifecycleConfirmPurgeCommand? LastConfirm { get; private set; }

        public ValueTask<Result<LifecycleTrashPage>> ListTrashAsync(
            LifecycleTrashQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<LifecycleAssetMutationResult>>> TrashAsync(
            LifecycleTrashCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTrash = command;
            IReadOnlyList<LifecycleAssetMutationResult> items =
            [
                new(command.Targets[0].AssetId, "trashed", 5, null),
            ];
            return ValueTask.FromResult(Result.Success(items));
        }

        public ValueTask<Result<LifecyclePurgeBatchSnapshot>> ConfirmPurgeAsync(
            LifecycleConfirmPurgeCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmCalls++;
            LastConfirm = command;
            return ValueTask.FromResult(
                Result.Success(
                    new LifecyclePurgeBatchSnapshot(
                        command.BatchId,
                        "queued",
                        Now.AddMinutes(-10),
                        command.ConfirmedAtUtc,
                        null,
                        null,
                        1,
                        1,
                        0,
                        0,
                        [],
                        command.ExpectedVersion + 1,
                        false)));
        }

        public ValueTask<Result<LifecycleJobSubmission>> SubmitRestoreAsync(
            LifecycleRestoreCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecyclePurgeDryRunSnapshot>> CreatePurgeDryRunAsync(
            LifecycleCreatePurgeDryRunCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecyclePurgeBatchSnapshot>> GetPurgeBatchAsync(
            Guid tenantId,
            Guid actorId,
            Guid batchId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleHoldSnapshot>> PlaceHoldAsync(
            LifecyclePlaceHoldCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleHoldSnapshot>> ReleaseHoldAsync(
            LifecycleReleaseHoldCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class SequenceUuid7Generator : IUuid7Generator
    {
        private long _offset;

        public Guid NewId() => Guid.CreateVersion7(Now.AddMilliseconds(_offset++));
    }
}
