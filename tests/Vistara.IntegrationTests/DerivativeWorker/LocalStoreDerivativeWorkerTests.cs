using System.Security.Cryptography;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.IntegrationTests.DerivativeConcurrency;
using Vistara.Storage.Local;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.DerivativeWorker;

public sealed class LocalStoreDerivativeWorkerTests
{
    private static readonly ImageDecodeLimits Limits = new(
        maxEncodedBytes: 1_024,
        maxWidth: 100,
        maxHeight: 100,
        maxAggregatePixels: 10_000,
        maxFrames: 1,
        maxEstimatedDecodedBytes: 40_000,
        processingDeadline: TimeSpan.FromSeconds(5));

    private static readonly TimeSpan OwnershipDuration = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Derivative_worker_publishes_on_a_fresh_local_root_without_pre_created_shards()
    {
        string scratch = DerivativeScratchDirectory.Create();
        try
        {
            LocalStoreDerivativeScenario scenario =
                await LocalStoreDerivativeScenario.CreateAsync(scratch);
            BlobKey destinationKey = new(scenario.Generation.CacheKey.Value);

            Assert.Null(await scenario.Store.HeadAsync(
                destinationKey,
                CancellationToken.None));

            JobHandlerResult result = await scenario.RunAsync();

            Assert.True(result.IsSuccess);
            Assert.True(scenario.State.IsReady);
            Assert.True(scenario.State.CleanupCompleted);
            BlobHead? published = await scenario.Store.HeadAsync(
                destinationKey,
                CancellationToken.None);
            Assert.NotNull(published);
            Assert.Equal(
                scenario.Imaging.OutputBytes.LongLength,
                published.Properties.ContentLength);
            Assert.Equal("image/webp", published.Properties.ContentType.Value);
            Assert.Empty(await ListKeysAsync(scenario.Store, "staging/derivatives/"));
        }
        finally
        {
            DerivativeScratchDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Derivative_worker_repeats_successfully_on_a_fresh_local_root()
    {
        string scratch = DerivativeScratchDirectory.Create();
        try
        {
            LocalStoreDerivativeScenario scenario =
                await LocalStoreDerivativeScenario.CreateAsync(scratch);

            Assert.True((await scenario.RunAsync()).IsSuccess);
            JobHandlerResult replay = await scenario.RunAsync();

            Assert.True(replay.IsSuccess);
            Assert.Equal(1, scenario.Imaging.TransformCalls);
            Assert.Equal(1, scenario.State.ReadyCommitCount);
        }
        finally
        {
            DerivativeScratchDirectory.Delete(scratch);
        }
    }

    private static async Task<IReadOnlyList<string>> ListKeysAsync(
        LocalBlobStore store,
        string prefix)
    {
        List<string> keys = [];
        await foreach (BlobHead head in store.ListAsync(
                           new BlobListOptions(prefix),
                           CancellationToken.None))
        {
            keys.Add(head.Identity.Key.Value);
        }

        return keys;
    }

    private sealed class LocalStoreDerivativeScenario
    {
        private LocalStoreDerivativeScenario(
            Guid requestId,
            DerivativeGenerationRequest generation,
            LocalBlobStore store,
            FakeDerivativeStatePort state,
            DerivativeImageProcessor imaging,
            MutableClock clock)
        {
            RequestId = requestId;
            Generation = generation;
            Store = store;
            State = state;
            Imaging = imaging;
            Clock = clock;
            Handler = new DerivativeJobHandler(
                new DerivativeService(
                    state,
                    store,
                    imaging,
                    clock,
                    Limits,
                    new TrackingDerivativeOutputScratchFactory(),
                    new DerivativeTransformGate(1),
                    OwnershipDuration,
                    new DerivativeCheckpointObserver()));
        }

        internal Guid RequestId { get; }

        internal DerivativeGenerationRequest Generation { get; }

        internal LocalBlobStore Store { get; }

        internal FakeDerivativeStatePort State { get; }

        internal DerivativeImageProcessor Imaging { get; }

        internal MutableClock Clock { get; }

        internal DerivativeJobHandler Handler { get; }

        internal static async Task<LocalStoreDerivativeScenario> CreateAsync(
            string scratch)
        {
            const string pipeline = "fake-pipeline";
            Guid tenantId = Guid.CreateVersion7();
            Guid assetId = Guid.CreateVersion7();
            Guid revisionId = Guid.CreateVersion7();
            Guid requestId = Guid.CreateVersion7();
            byte[] sourceBytes = "immutable-source-image"u8.ToArray();
            string sourceSha256 = Convert.ToHexStringLower(
                SHA256.HashData(sourceBytes));
            var source = new DerivativeSourceIdentity(
                tenantId,
                assetId,
                revisionId,
                revisionNumber: 7,
                new ImageSha256(sourceSha256));
            var output = new DerivativeOutputRequest(
                new DerivativePresetId("thumb", 1),
                new DerivativeDimensions(256, 256),
                [DerivativeFormat.WebP]);
            DerivativeResolutionResult resolution =
                DerivativePresetRegistry.Standard.Resolve(
                    new DerivativeRequest(
                        source,
                        output,
                        new ImagePipelineFingerprint(pipeline)));
            DerivativeGenerationRequest generation = resolution.GenerationRequest!;
            var store = new LocalBlobStore(
                new LocalBlobStoreOptions(Path.Combine(scratch, "store")));
            var sourceKey = new BlobKey(
                $"originals/aa/{tenantId:N}/{assetId:N}/7/{revisionId:N}.png");
            BlobWriteResult seeded = await store.PutAsync(
                sourceKey,
                new ByteArrayBlobContent(sourceBytes),
                new BlobWriteOptions(
                    new BlobMediaType("image/png"),
                    checksums:
                    [
                        new BlobChecksum(
                            BlobChecksumAlgorithm.Sha256,
                            sourceSha256),
                    ],
                    conditions: BlobRequestConditions.CreateOnly),
                CancellationToken.None);
            var work = new DerivativeWorkItem(
                requestId,
                generation,
                sourceKey,
                seeded.Head.Identity.Version,
                sourceBytes.LongLength);
            return new LocalStoreDerivativeScenario(
                requestId,
                generation,
                store,
                new FakeDerivativeStatePort(work),
                new DerivativeImageProcessor(new ImagePipelineFingerprint(pipeline)),
                new MutableClock(
                    new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero)));
        }

        internal ValueTask<JobHandlerResult> RunAsync()
        {
            DerivativeJobPayloadV1 payload =
                DerivativeJobContract.CreatePayload(Generation);
            DurableJob job = DurableJob.Create(
                new JobId(RequestId),
                new JobTenantId(Generation.Source.TenantId),
                DerivativeJobHandler.SupportedJobType,
                DerivativeJobContract.Serialize(payload),
                DerivativeJobContract.PayloadVersion,
                DerivativeJobContract.CreateDedupeKey(payload),
                priority: 0,
                maxAttempts: 5,
                Clock.UtcNow,
                Clock.UtcNow);
            var leased = job.TryLease(
                new JobLeaseOwner("local-derivative-test"),
                Clock.UtcNow,
                TimeSpan.FromMinutes(10));
            if (leased.IsFailure)
            {
                throw new InvalidOperationException(leased.Error?.Code);
            }

            return Handler.HandleAsync(job, CancellationToken.None);
        }
    }

    private sealed class ByteArrayBlobContent(byte[] bytes) : IReplayableBlobContent
    {
        public long Length => bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(
                new MemoryStream(bytes, writable: false));
        }
    }
}
