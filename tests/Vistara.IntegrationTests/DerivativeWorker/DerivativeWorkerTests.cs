using System.Security.Cryptography;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.DerivativeWorker;

public sealed class DerivativeWorkerTests
{
    [Fact]
    public async Task DerivativeWorker_publishes_create_only_then_marks_ready_and_cleans_staging()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.IsReady);
        Assert.True(scenario.State.CleanupCompleted);
        Assert.Equal(1, scenario.Imaging.TransformCalls);
        Assert.Equal(BlobRequestConditions.CreateOnly, scenario.Storage.LastPutOptions!
            .Conditions);
        Assert.Equal(BlobRequestConditions.CreateOnly, scenario.Storage.LastCopyOptions!
            .EffectiveDestinationConditions);
        Assert.True(scenario.Storage.Contains(scenario.DestinationKey));
        Assert.DoesNotContain(
            scenario.Storage.Keys,
            key => key.Value.StartsWith("staging/derivatives/", StringComparison.Ordinal));
        Assert.DoesNotContain(
            scenario.Storage.Get(scenario.DestinationKey).Metadata.Keys,
            key => key.Contains("source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DerivativeWorker_replays_bounded_scratch_without_materializing_output_array()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, scenario.Scratch.CreateCount);
        Assert.Equal(2, scenario.Scratch.OpenReadCount);
        Assert.Equal(1, scenario.Scratch.DisposeCount);
    }

    [Fact]
    public async Task DerivativeWorker_rejects_output_larger_than_the_encoded_byte_limit()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Imaging.OutputBytes = new byte[2_048];

        JobHandlerResult result = await scenario.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.MediaDecodeFailed, result.Failure?.Reason);
        Assert.Equal(
            DerivativeFailureCode.MediaDecodeFailed,
            scenario.State.LastFailure?.Code);
        Assert.Equal(1, scenario.Scratch.DisposeCount);
        Assert.False(scenario.Storage.Contains(scenario.DestinationKey));
    }

    [Fact]
    public async Task Derivative_transform_gate_allows_only_one_transform()
    {
        using var gate = new DerivativeTransformGate(1);
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = gate.RunAsync(
            async cancellationToken =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return 1;
            },
            CancellationToken.None).AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int> second = gate.RunAsync(
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                secondEntered.TrySetResult();
                return ValueTask.FromResult(2);
            },
            CancellationToken.None).AsTask();

        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.TrySetResult();

        int[] results = await Task.WhenAll(first, second);
        Assert.Equal([1, 2], results);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DerivativeWorker_concurrent_identical_jobs_converge_on_one_output()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Imaging.PauseTransform = true;

        Task<JobHandlerResult> first = scenario.RunAsync().AsTask();
        await scenario.Imaging.TransformStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        JobHandlerResult competing = await scenario.RunAsync();
        scenario.Imaging.AllowTransform.TrySetResult();
        JobHandlerResult completed = await first;
        JobHandlerResult duplicate = await scenario.RunAsync();

        Assert.False(competing.IsSuccess);
        Assert.Equal(JobFailureReason.LeaseExpired, competing.Failure?.Reason);
        Assert.True(completed.IsSuccess);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(1, scenario.Imaging.TransformCalls);
        Assert.Equal(1, scenario.Storage.DestinationCreateCount);
        Assert.Equal(1, scenario.State.ReadyCommitCount);
    }

    [Fact]
    public async Task DerivativeWorker_duplicate_ready_job_skips_transform()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        Assert.True((await scenario.RunAsync()).IsSuccess);

        JobHandlerResult duplicate = await scenario.RunAsync();

        Assert.True(duplicate.IsSuccess);
        Assert.Equal(1, scenario.Imaging.TransformCalls);
        Assert.Equal(1, scenario.State.ReadyCommitCount);
    }

    [Fact]
    public async Task DerivativeWorker_existing_matching_destination_is_verified_without_transform()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.AddMatchingDestination();

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.IsReady);
        Assert.Equal(0, scenario.Imaging.TransformCalls);
        Assert.Contains(scenario.DestinationKey, scenario.Storage.OpenedKeys);
    }

    [Fact]
    public async Task DerivativeWorker_corrupt_existing_destination_never_becomes_ready()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.AddCorruptDestination();

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.False(scenario.State.IsReady);
        Assert.Equal(
            DerivativeFailureCode.DestinationIdentityConflict,
            scenario.State.LastFailure?.Code);
        Assert.Equal(0, scenario.Imaging.TransformCalls);
    }

    [Fact]
    public async Task DerivativeWorker_publish_collision_with_partial_destination_cleans_staging()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Storage.CopyBehavior =
            DerivativeCopyBehavior.PreconditionFailedWithCorruptDestination;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.False(scenario.State.IsReady);
        Assert.Equal(
            DerivativeFailureCode.DestinationIdentityConflict,
            scenario.State.LastFailure?.Code);
        Assert.DoesNotContain(
            scenario.Storage.Keys,
            key => key.Value.StartsWith("staging/derivatives/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DerivativeWorker_processor_failure_is_retryable_and_safe()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Imaging.Error = ImageProcessorErrorCode.MalformedImage;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.MediaDecodeFailed, result.Failure?.Reason);
        Assert.Equal(DerivativeFailureCode.MediaDecodeFailed, scenario.State.LastFailure?.Code);
        Assert.False(scenario.State.IsReady);
        Assert.False(scenario.Storage.Contains(scenario.DestinationKey));
    }

    [Fact]
    public async Task DerivativeWorker_publish_ambiguity_reconciles_visible_identity()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Storage.CopyBehavior = DerivativeCopyBehavior.OutcomeUnknownAfterCopy;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.PublishOutcomeUnknownRecorded);
        Assert.True(scenario.State.IsReady);
        Assert.Contains(scenario.DestinationKey, scenario.Storage.OpenedKeys);
    }

    [Fact]
    public async Task DerivativeWorker_source_revision_change_fails_without_transform()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.ReplaceSourceVersion();

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(
            DerivativeFailureCode.SourceRevisionChanged,
            scenario.State.LastFailure?.Code);
        Assert.Equal(0, scenario.Imaging.TransformCalls);
        Assert.False(scenario.Storage.Contains(scenario.DestinationKey));
    }

    [Fact]
    public async Task DerivativeWorker_stale_fence_cannot_commit_ready_but_restart_converges()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Checkpoints.ActionAt = DerivativeCheckpoint.DestinationVisible;
        scenario.Checkpoints.Action = scenario.State.ExpireAndStealFence;

        JobHandlerResult stale = await scenario.RunAsync();

        Assert.False(stale.IsSuccess);
        Assert.Equal(JobFailureReason.LeaseExpired, stale.Failure?.Reason);
        Assert.False(scenario.State.IsReady);
        Assert.True(scenario.Storage.Contains(scenario.DestinationKey));

        scenario.Checkpoints.ActionAt = null;
        scenario.Clock.Advance(DerivativeScenario.OwnershipDuration);
        JobHandlerResult recovered = await scenario.RunAsync();

        Assert.True(recovered.IsSuccess);
        Assert.True(scenario.State.IsReady);
        Assert.Equal(1, scenario.Imaging.TransformCalls);
        Assert.Equal(1, scenario.State.ReadyCommitCount);
    }

    [Theory]
    [InlineData(DerivativeCheckpoint.OwnershipAcquired)]
    [InlineData(DerivativeCheckpoint.SourceVerified)]
    [InlineData(DerivativeCheckpoint.OutputTransformed)]
    [InlineData(DerivativeCheckpoint.OutputStaged)]
    [InlineData(DerivativeCheckpoint.DestinationPublished)]
    [InlineData(DerivativeCheckpoint.DestinationVisible)]
    [InlineData(DerivativeCheckpoint.ReadyCommitted)]
    [InlineData(DerivativeCheckpoint.StagingDeleted)]
    [InlineData(DerivativeCheckpoint.CleanupCommitted)]
    public async Task DerivativeWorker_restart_after_every_side_effect_converges(
        DerivativeCheckpoint checkpoint)
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Checkpoints.CancelAt = checkpoint;

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scenario.RunAsync());

        scenario.Checkpoints.CancelAt = null;
        scenario.Clock.Advance(DerivativeScenario.OwnershipDuration);
        JobHandlerResult restarted = await scenario.RunAsync();

        Assert.True(restarted.IsSuccess);
        Assert.True(scenario.State.IsReady);
        Assert.True(scenario.State.CleanupCompleted);
        Assert.True(scenario.Storage.Contains(scenario.DestinationKey));
        Assert.DoesNotContain(
            scenario.Storage.Keys,
            key => key.Value.StartsWith("staging/derivatives/", StringComparison.Ordinal));
        Assert.Equal(1, scenario.State.ReadyCommitCount);
    }

    [Fact]
    public async Task DerivativeWorker_invalid_canonical_dedupe_is_rejected_before_io()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        DurableJob tampered = scenario.CreateJob(dedupeKey: "tampered");

        JobHandlerResult result = await scenario.Handler.HandleAsync(
            tampered,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.ProcessingFailed, result.Failure?.Reason);
        Assert.Empty(scenario.Storage.OpenedKeys);
        Assert.Equal(0, scenario.Imaging.TransformCalls);
    }

    [Fact]
    public async Task DerivativeWorker_pipeline_fingerprint_change_changes_identity_and_key()
    {
        DerivativeScenario original = DerivativeScenario.Valid("pipeline-a");
        DerivativeScenario changed = DerivativeScenario.Valid("pipeline-b");

        Assert.NotEqual(original.Generation.Identity, changed.Generation.Identity);
        Assert.NotEqual(original.DestinationKey, changed.DestinationKey);
        Assert.NotEqual(
            original.Generation.DedupeIdentity.Key,
            changed.Generation.DedupeIdentity.Key);

        Assert.True((await original.RunAsync()).IsSuccess);
        Assert.True((await changed.RunAsync()).IsSuccess);
    }

    [Fact]
    public async Task DerivativeWorker_rejects_output_that_retains_private_metadata()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        scenario.Imaging.LeakMetadata = true;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.MediaDecodeFailed, result.Failure?.Reason);
        Assert.Equal(DerivativeFailureCode.UnsafeProcessorOutput, scenario.State.LastFailure?.Code);
        Assert.False(scenario.Storage.Contains(scenario.DestinationKey));
    }

    [Fact]
    public async Task DerivativeWorker_invalid_payload_returns_only_typed_safe_failure()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();
        DurableJob job = scenario.CreateRawJob(
            """{"requestId":"not-a-guid","private":"do-not-leak"}""");

        JobHandlerResult result = await scenario.Handler.HandleAsync(
            job,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.ProcessingFailed, result.Failure?.Reason);
    }

    [Fact]
    public async Task DerivativeWorker_processes_the_asset_ingest_job_contract()
    {
        DerivativeScenario scenario = DerivativeScenario.Valid();

        JobHandlerResult result = await scenario.Handler.HandleAsync(
            scenario.CreateAssetIngestJob(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.IsReady);
        Assert.Equal(1, scenario.Imaging.TransformCalls);
    }

    internal sealed class DerivativeScenario
    {
        private static readonly ImageDecodeLimits Limits = new(
            maxEncodedBytes: 1_024,
            maxWidth: 100,
            maxHeight: 100,
            maxAggregatePixels: 10_000,
            maxFrames: 1,
            maxEstimatedDecodedBytes: 40_000,
            processingDeadline: TimeSpan.FromSeconds(5));

        private DerivativeScenario(
            Guid requestId,
            DerivativeGenerationRequest generation,
            FakeDerivativeStatePort state,
            DerivativeBlobStore storage,
            DerivativeImageProcessor imaging,
            TrackingDerivativeOutputScratchFactory scratch,
            DerivativeTransformGate transformGate,
            MutableClock clock,
            DerivativeCheckpointObserver checkpoints)
        {
            RequestId = requestId;
            Generation = generation;
            State = state;
            Storage = storage;
            Imaging = imaging;
            Scratch = scratch;
            TransformGate = transformGate;
            Clock = clock;
            Checkpoints = checkpoints;
            Handler = new DerivativeJobHandler(
                new DerivativeService(
                    state,
                    storage,
                    imaging,
                    clock,
                    Limits,
                    scratch,
                    transformGate,
                    OwnershipDuration,
                    checkpoints));
        }

        internal static TimeSpan OwnershipDuration { get; } = TimeSpan.FromMinutes(2);

        internal Guid RequestId { get; }

        internal DerivativeGenerationRequest Generation { get; }

        internal FakeDerivativeStatePort State { get; }

        internal DerivativeBlobStore Storage { get; }

        internal DerivativeImageProcessor Imaging { get; }

        internal TrackingDerivativeOutputScratchFactory Scratch { get; }

        internal DerivativeTransformGate TransformGate { get; }

        internal MutableClock Clock { get; }

        internal DerivativeCheckpointObserver Checkpoints { get; }

        internal DerivativeJobHandler Handler { get; }

        internal BlobKey DestinationKey => new(Generation.CacheKey.Value);

        internal static DerivativeScenario Valid(string pipeline = "fake-pipeline")
        {
            Guid tenantId = Guid.CreateVersion7();
            Guid assetId = Guid.CreateVersion7();
            Guid revisionId = Guid.CreateVersion7();
            Guid requestId = Guid.CreateVersion7();
            byte[] sourceBytes = "immutable-source-image"u8.ToArray();
            var sourceSha = new ImageSha256(
                Convert.ToHexStringLower(SHA256.HashData(sourceBytes)));
            var source = new DerivativeSourceIdentity(
                tenantId,
                assetId,
                revisionId,
                revisionNumber: 7,
                sourceSha);
            var output = new DerivativeOutputRequest(
                new DerivativePresetId("thumb", 1),
                new DerivativeDimensions(256, 256),
                [DerivativeFormat.WebP]);
            DerivativeResolutionResult resolution = DerivativePresetRegistry.Standard.Resolve(
                new DerivativeRequest(
                    source,
                    output,
                    new ImagePipelineFingerprint(pipeline)));
            DerivativeGenerationRequest generation = resolution.GenerationRequest!;
            var sourceKey = new BlobKey(
                $"originals/aa/{tenantId:N}/{assetId:N}/7/{revisionId:N}.png");
            var sourceVersion = new BlobVersion("source-v1");
            var work = new DerivativeWorkItem(
                requestId,
                generation,
                sourceKey,
                sourceVersion,
                sourceBytes.LongLength);
            var state = new FakeDerivativeStatePort(work);
            var storage = new DerivativeBlobStore();
            storage.AddSource(
                sourceKey,
                sourceBytes,
                sourceVersion,
                sourceSha);
            var imaging = new DerivativeImageProcessor(
                new ImagePipelineFingerprint(pipeline));
            var scratch = new TrackingDerivativeOutputScratchFactory();
            var transformGate = new DerivativeTransformGate(1);
            var clock = new MutableClock(
                new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero));
            return new DerivativeScenario(
                requestId,
                generation,
                state,
                storage,
                imaging,
                scratch,
                transformGate,
                clock,
                new DerivativeCheckpointObserver());
        }

        internal ValueTask<JobHandlerResult> RunAsync() =>
            Handler.HandleAsync(CreateJob(), CancellationToken.None);

        internal DurableJob CreateJob(string? dedupeKey = null)
        {
            DerivativeJobPayloadV1 payload =
                DerivativeJobContract.CreatePayload(Generation);
            return CreateRawJob(
                DerivativeJobContract.Serialize(payload),
                dedupeKey ?? DerivativeJobContract.CreateDedupeKey(payload).Value);
        }

        internal DurableJob CreateRawJob(
            string payload,
            string dedupeKey = "invalid-derivative-payload")
        {
            DurableJob job = DurableJob.Create(
                new JobId(RequestId),
                new JobTenantId(Generation.Source.TenantId),
                DerivativeJobHandler.SupportedJobType,
                payload,
                DerivativeJobContract.PayloadVersion,
                new JobDedupeKey(dedupeKey),
                priority: 0,
                maxAttempts: 5,
                Clock.UtcNow,
                Clock.UtcNow);
            var leased = job.TryLease(
                new JobLeaseOwner("derivative-test"),
                Clock.UtcNow,
                TimeSpan.FromMinutes(10));
            if (leased.IsFailure)
            {
                throw new InvalidOperationException(leased.Error?.Code);
            }

            return job;
        }

        internal DurableJob CreateAssetIngestJob() => CreateJob();

        internal void AddMatchingDestination() =>
            Storage.AddDerivative(
                DestinationKey,
                Imaging.OutputBytes,
                Generation,
                "destination-v1");

        internal void AddCorruptDestination()
        {
            Storage.AddDerivative(
                DestinationKey,
                Imaging.OutputBytes,
                Generation,
                "destination-v1");
            Storage.ReplaceBytesPreservingIdentity(
                DestinationKey,
                "partial-corrupt-output"u8.ToArray());
        }

        internal void ReplaceSourceVersion() =>
            Storage.ReplaceVersion(State.Work.SourceKey, new BlobVersion("source-v2"));
    }
}
