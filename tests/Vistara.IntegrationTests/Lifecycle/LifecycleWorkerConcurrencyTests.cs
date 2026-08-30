using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Worker.Features.Lifecycle;
using Xunit;

namespace Vistara.IntegrationTests.Lifecycle;

public sealed class LifecycleWorkerConcurrencyTests
{
    private static readonly DateTimeOffset Now =
        new(2033, 4, 5, 6, 7, 8, TimeSpan.Zero);
    private static readonly Guid TenantId = LifecyclePersistenceTests.Id(200);
    private static readonly Guid BatchId = LifecyclePersistenceTests.Id(201);
    private static readonly Guid AssetId = LifecyclePersistenceTests.Id(202);

    [Fact]
    public async Task Concurrent_purge_workers_delete_one_provider_identity()
    {
        var store = new ConcurrentWorkerStore();
        var storage = new ConcurrentBlobStore();
        var first = new LifecyclePurgeService(
            store,
            storage,
            new FixedClock());
        var second = new LifecyclePurgeService(
            store,
            storage,
            new FixedClock());

        Vistara.Worker.Runtime.Jobs.JobHandlerResult[] results =
            await Task.WhenAll(
                first.ProcessAsync(TenantId, BatchId, CancellationToken.None).AsTask(),
                second.ProcessAsync(TenantId, BatchId, CancellationToken.None).AsTask());

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, storage.SuccessfulDeletes);
        Assert.True(storage.DeleteCalls is 1 or 2);
        Assert.Equal(1, store.TombstonesCreated);
    }

    private sealed class ConcurrentWorkerStore : ILifecycleWorkerStore
    {
        private readonly object _gate = new();
        private bool _actionRecorded;
        private bool _tombstoneCreated;

        public int TombstonesCreated
        {
            get
            {
                lock (_gate)
                {
                    return _tombstoneCreated ? 1 : 0;
                }
            }
        }

        public ValueTask<LifecyclePurgeBatchWork> StartPurgeBatchAsync(
            Guid tenantId,
            Guid batchId,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LifecyclePurgeBatchWork(
                LifecyclePurgeBatchWorkStatus.Ready,
                [AssetId]));

        public ValueTask<LifecyclePurgeAssetPreparation> PreparePurgeAssetAsync(
            Guid tenantId,
            Guid batchId,
            Guid assetId,
            string storageProvider,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken)
        {
            var fence = new LifecyclePurgeAssetFence(
                TenantId,
                BatchId,
                AssetId,
                1,
                2,
                new string('a', 64));
            var action = new LifecyclePurgeProviderAction(
                LifecyclePurgeProviderActionKind.Original,
                new BlobKey("originals/concurrent/image.jpg"),
                new BlobVersion("v1"),
                100);
            return ValueTask.FromResult(new LifecyclePurgeAssetPreparation(
                LifecyclePurgeAssetPreparationStatus.Ready,
                new LifecyclePurgeAssetWork(fence, [action]),
                null));
        }

        public ValueTask<LifecyclePurgeActionCheck> RecheckPurgeActionAsync(
            LifecyclePurgeAssetFence fence,
            LifecyclePurgeProviderAction action,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(new LifecyclePurgeActionCheck(
                    _actionRecorded
                        ? LifecyclePurgeActionCheckStatus.AlreadyDeleted
                        : LifecyclePurgeActionCheckStatus.Allowed,
                    null));
            }
        }

        public ValueTask<Result> RecordPurgeActionDeletedAsync(
            LifecyclePurgeAssetFence fence,
            LifecyclePurgeProviderAction action,
            DateTimeOffset deletedAtUtc,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _actionRecorded = true;
                return ValueTask.FromResult(Result.Success());
            }
        }

        public ValueTask<Result<long>> CompletePurgeAssetAsync(
            LifecyclePurgeAssetFence fence,
            DateTimeOffset purgedAtUtc,
            DateTimeOffset backupExpiresAtUtc,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _tombstoneCreated = true;
                return ValueTask.FromResult(Result.Success(100L));
            }
        }

        public ValueTask<Result> RecordPurgeItemResultAsync(
            Guid tenantId,
            Guid batchId,
            Guid assetId,
            LifecyclePurgeItemOutcome outcome,
            string errorCode,
            DateTimeOffset recordedAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> CompletePurgeBatchAsync(
            Guid tenantId,
            Guid batchId,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> RestoreAsync(
            LifecycleRestoreJobPayload payload,
            DateTimeOffset restoredAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ConcurrentBlobStore : IBlobStore
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _bothHeads =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _headCalls;
        private bool _exists = true;

        public string Name => "test";

        public BlobStoreCapabilities Capabilities { get; } = new()
        {
            SupportsConditionalDelete = true,
            ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
        };

        public int DeleteCalls { get; private set; }

        public int SuccessfulDeletes { get; private set; }

        public async ValueTask<BlobHead?> HeadAsync(
            BlobKey key,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _headCalls) <= 2 &&
                Volatile.Read(ref _headCalls) == 2)
            {
                _bothHeads.TrySetResult();
            }

            if (Volatile.Read(ref _headCalls) <= 2)
            {
                await _bothHeads.Task.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                return _exists ? Head(key) : null;
            }
        }

        public ValueTask<BlobDeleteResult> DeleteAsync(
            BlobKey key,
            BlobDeleteOptions options,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                DeleteCalls++;
                if (!_exists)
                {
                    return ValueTask.FromResult(new BlobDeleteResult(false, null));
                }

                _exists = false;
                SuccessfulDeletes++;
                return ValueTask.FromResult(new BlobDeleteResult(
                    true,
                    new BlobIdentity(key, new BlobVersion("v1"))));
            }
        }

        public ValueTask<BlobReadHandle> OpenReadAsync(
            BlobKey key,
            BlobReadOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobWriteResult> PutAsync(
            BlobKey key,
            IReplayableBlobContent content,
            BlobWriteOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobCopyResult> CopyAsync(
            BlobKey source,
            BlobKey destination,
            BlobCopyOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<BlobHead> ListAsync(
            BlobListOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
            DirectUploadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MultipartSession> BeginMultipartAsync(
            MultipartRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
            MultipartSession session,
            int partNumber,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MultipartCompletion> CompleteMultipartAsync(
            MultipartSession session,
            IReadOnlyList<UploadedPart> parts,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask AbortMultipartAsync(
            MultipartSession session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
            BlobKey key,
            ReadGrantOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static BlobHead Head(BlobKey key)
        {
            var version = new BlobVersion("v1");
            return new BlobHead(
                new BlobIdentity(key, version),
                new BlobProperties(
                    100,
                    new BlobMediaType("image/jpeg"),
                    Now,
                    version,
                    new BlobEntityTag("\"v1\""),
                    [],
                    BlobMetadata.Empty));
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
