using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Assets;
using Vistara.Domain.Jobs;
using Vistara.Worker.Features.Ingest;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Ingest;

public sealed class IngestWorkerTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

    private static readonly ImageDecodeLimits Limits = new(
        maxEncodedBytes: 1_024,
        maxWidth: 100,
        maxHeight: 100,
        maxAggregatePixels: 10_000,
        maxFrames: 1,
        maxEstimatedDecodedBytes: 40_000,
        processingDeadline: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task IngestWorker_activates_only_after_verified_create_only_promotion()
    {
        IngestScenario scenario = IngestScenario.Valid();

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.IsActivated);
        Assert.True(scenario.State.ReservationConsumed);
        Assert.True(scenario.State.DerivativesEnqueued);
        Assert.True(scenario.State.OutboxEnqueued);
        Assert.True(scenario.State.StagingDeletionRecorded);
        Assert.False(scenario.Storage.Contains(scenario.Work.StagingKey));
        Assert.True(scenario.Storage.Contains(scenario.State.CanonicalKey));
        Assert.Equal(BlobRequestConditions.CreateOnly, scenario.Storage.LastCopyOptions!
            .EffectiveDestinationConditions);
        Assert.Equal(scenario.Work.ExpectedStagingVersion, scenario.Storage.LastCopyOptions!
            .EffectiveSourceConditions.IfMatch);
        Assert.Equal("png", scenario.State.Activation!.Verified.Media.DetectedFormat);
        Assert.Equal(
            "image/png",
            scenario.State.Activation.Verified.Media.ContentType.Value);
        Assert.Equal(17, scenario.State.Activation.Verified.Media.Width);
        Assert.Equal(11, scenario.State.Activation.Verified.Media.Height);
        Assert.Equal(1, scenario.State.Activation.Verified.Media.FrameCount);
    }

    [Fact]
    public async Task IngestWorker_missing_staging_object_rejects_without_public_asset()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Storage.Remove(scenario.Work.StagingKey);

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(IngestRejectionCode.ObjectMissing, scenario.State.Rejection?.Code);
        scenario.AssertRejectedWithoutLeaks();
    }

    [Theory]
    [InlineData(IngestMutation.Size, IngestRejectionCode.SizeMismatch)]
    [InlineData(IngestMutation.Checksum, IngestRejectionCode.ChecksumMismatch)]
    [InlineData(IngestMutation.ContentType, IngestRejectionCode.ContentTypeMismatch)]
    [InlineData(IngestMutation.Metadata, IngestRejectionCode.MetadataMismatch)]
    [InlineData(IngestMutation.Version, IngestRejectionCode.ObjectChanged)]
    public async Task IngestWorker_rejects_authoritative_object_mismatches(
        IngestMutation mutation,
        IngestRejectionCode expected)
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.MutateStaging(mutation);

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, scenario.State.Rejection?.Code);
        scenario.AssertRejectedWithoutLeaks();
    }

    [Theory]
    [InlineData(ImageProcessorErrorCode.MalformedImage, IngestRejectionCode.MalformedImage)]
    [InlineData(ImageProcessorErrorCode.UnsupportedFormat, IngestRejectionCode.UnsupportedImage)]
    [InlineData(ImageProcessorErrorCode.DecodeLimitExceeded, IngestRejectionCode.DecodeLimitExceeded)]
    public async Task IngestWorker_quarantines_corrupt_or_bomb_media(
        ImageProcessorErrorCode error,
        IngestRejectionCode expected)
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Imaging.Error = error;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, scenario.State.Rejection?.Code);
        scenario.AssertRejectedWithoutLeaks();
        Assert.True(scenario.Storage.Contains(scenario.Work.StagingKey));
    }

    [Fact]
    public async Task IngestWorker_processor_stream_constraint_retries_without_rejecting_upload()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Imaging.Error = ImageProcessorErrorCode.InputNotReplayable;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.ProcessingFailed, result.Failure?.Reason);
        Assert.False(scenario.State.IsRejected);
        Assert.False(scenario.State.IsActivated);
        Assert.False(scenario.State.ReservationReleased);
        Assert.False(scenario.State.ReservationConsumed);
        Assert.True(scenario.Storage.Contains(scenario.Work.StagingKey));
    }

    [Fact]
    public async Task IngestWorker_uses_transaction_port_duplicate_decision_without_copying()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.State.UseExactDuplicate = true;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.IsActivated);
        Assert.Equal(IngestPromotionMode.ExistingExactBlob, scenario.State.Activation?.Plan.Mode);
        Assert.Equal(0, scenario.Storage.CopyCalls);
        Assert.False(scenario.Storage.Contains(scenario.Work.StagingKey));
    }

    [Fact]
    public async Task IngestWorker_matching_destination_conflict_reconciles_and_activates()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Storage.CopyBehavior = FakeCopyBehavior.PreconditionFailedAfterMatchingObject;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.IsActivated);
        Assert.True(scenario.State.ReservationConsumed);
        Assert.False(scenario.Storage.Contains(scenario.Work.StagingKey));
    }

    [Fact]
    public async Task IngestWorker_conflicting_destination_rejects_without_orphaning_new_bytes()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Storage.CopyBehavior = FakeCopyBehavior.PreconditionFailedWithDifferentObject;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(IngestRejectionCode.CanonicalConflict, scenario.State.Rejection?.Code);
        Assert.True(scenario.State.IsRejected);
        Assert.True(scenario.State.ReservationReleased);
        Assert.False(scenario.State.IsActivated);
        Assert.False(scenario.State.ReservationConsumed);
        Assert.False(scenario.State.DerivativesEnqueued);
        Assert.False(scenario.State.OutboxEnqueued);
        Assert.True(scenario.Storage.Contains(scenario.Work.StagingKey));
    }

    [Fact]
    public async Task IngestWorker_copy_ambiguity_heads_and_hashes_destination_before_activation()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Storage.CopyBehavior = FakeCopyBehavior.OutcomeUnknownAfterCopy;

        JobHandlerResult result = await scenario.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(scenario.State.PromotionOutcomeUnknownRecorded);
        Assert.True(scenario.State.IsActivated);
        Assert.Contains(scenario.State.CanonicalKey, scenario.Storage.OpenedKeys);
    }

    [Fact]
    public async Task IngestWorker_cancellation_after_copy_is_recoverable_on_restart()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Checkpoints.CancelAt = IngestCheckpoint.PromotionStored;

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scenario.RunAsync());

        Assert.False(scenario.State.IsActivated);
        Assert.False(scenario.State.ReservationConsumed);
        Assert.True(scenario.Storage.Contains(scenario.State.CanonicalKey));

        scenario.Checkpoints.CancelAt = null;
        JobHandlerResult restarted = await scenario.RunAsync();

        Assert.True(restarted.IsSuccess);
        Assert.True(scenario.State.IsActivated);
        Assert.False(scenario.Storage.Contains(scenario.Work.StagingKey));
        Assert.True(scenario.Storage.Contains(scenario.State.CanonicalKey));
    }

    [Theory]
    [InlineData(IngestCheckpoint.UploadFenced)]
    [InlineData(IngestCheckpoint.PromotionPlanned)]
    [InlineData(IngestCheckpoint.PromotionStored)]
    [InlineData(IngestCheckpoint.ActivationCommitted)]
    [InlineData(IngestCheckpoint.StagingDeleted)]
    [InlineData(IngestCheckpoint.CleanupCommitted)]
    public async Task IngestWorker_restart_after_each_durable_boundary_converges(
        IngestCheckpoint checkpoint)
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Checkpoints.CancelAt = checkpoint;

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scenario.RunAsync());

        scenario.Checkpoints.CancelAt = null;
        JobHandlerResult restarted = await scenario.RunAsync();

        Assert.True(restarted.IsSuccess);
        Assert.True(scenario.State.IsActivated);
        Assert.True(scenario.State.ReservationConsumed);
        Assert.True(scenario.State.StagingDeletionRecorded);
        Assert.False(scenario.Storage.Contains(scenario.Work.StagingKey));
        Assert.True(scenario.Storage.Contains(scenario.State.CanonicalKey));
        Assert.Equal(1, scenario.State.ActivationCount);
    }

    [Fact]
    public async Task IngestWorker_reject_side_effect_is_recoverable()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.Storage.Remove(scenario.Work.StagingKey);
        scenario.Checkpoints.CancelAt = IngestCheckpoint.RejectionCommitted;

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scenario.RunAsync());

        scenario.Checkpoints.CancelAt = null;
        JobHandlerResult restarted = await scenario.RunAsync();

        Assert.True(restarted.IsSuccess);
        Assert.Equal(1, scenario.State.RejectionCount);
        scenario.AssertRejectedWithoutLeaks();
    }

    [Fact]
    public async Task IngestWorker_invalid_payload_returns_only_safe_job_failure()
    {
        IngestScenario scenario = IngestScenario.Valid();
        DurableJob job = IngestScenario.CreateJob(
            scenario.Work.Fence.TenantId,
            """{"uploadSessionId":"not-an-id","private":"do-not-leak"}""");

        JobHandlerResult result = await scenario.Handler.HandleAsync(
            job,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.ProcessingFailed, result.Failure?.Reason);
    }

    [Fact]
    public async Task IngestWorker_refuses_cross_tenant_fence_results()
    {
        IngestScenario scenario = IngestScenario.Valid();
        scenario.State.ReturnedWork = new IngestWorkItem(
            scenario.Work.Fence with { TenantId = Guid.CreateVersion7() },
            scenario.Work.ActorId,
            scenario.Work.ReservationId,
            scenario.Work.StagingKey,
            scenario.Work.ExpectedStagingVersion,
            scenario.Work.ExpectedSizeBytes,
            scenario.Work.ExpectedSha256,
            scenario.Work.DeclaredContentType,
            scenario.Work.StorageContainer,
            scenario.Work.RequiredMetadata);

        JobHandlerResult result = await scenario.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(JobFailureReason.ProcessingFailed, result.Failure?.Reason);
        Assert.Empty(scenario.Storage.OpenedKeys);
        Assert.False(scenario.State.IsActivated);
        Assert.False(scenario.State.IsRejected);
    }

    public enum IngestMutation
    {
        Size,
        Checksum,
        ContentType,
        Metadata,
        Version,
    }

    internal sealed class IngestScenario
    {
        private IngestScenario(
            IngestWorkItem work,
            FakeIngestStatePort state,
            FakeBlobStore storage,
            FakeImageProcessor imaging,
            CrashCheckpointObserver checkpoints)
        {
            Work = work;
            State = state;
            Storage = storage;
            Imaging = imaging;
            Checkpoints = checkpoints;
            Handler = new IngestJobHandler(
                new IngestService(
                    state,
                    storage,
                    imaging,
                    new FixedClock(UtcNow),
                    Limits,
                    checkpoints));
        }

        internal IngestWorkItem Work { get; }

        internal FakeIngestStatePort State { get; }

        internal FakeBlobStore Storage { get; }

        internal FakeImageProcessor Imaging { get; }

        internal CrashCheckpointObserver Checkpoints { get; }

        internal IngestJobHandler Handler { get; }

        internal static IngestScenario Valid()
        {
            Guid tenantId = Guid.CreateVersion7();
            Guid uploadId = Guid.CreateVersion7();
            Guid actorId = Guid.CreateVersion7();
            Guid reservationId = Guid.CreateVersion7();
            byte[] bytes = "trusted-streamed-png"u8.ToArray();
            var stagingKey = new BlobKey($"staging/aa/{tenantId:D}/{uploadId:D}");
            var stagingVersion = new BlobVersion("staging-v1");
            var expectedSha = new Sha256Checksum(
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var requiredMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vistara-tenant-id"] = tenantId.ToString("D"),
                ["vistara-upload-id"] = uploadId.ToString("D"),
            };
            var work = new IngestWorkItem(
                new IngestFence(tenantId, uploadId, Version: 3),
                actorId,
                reservationId,
                stagingKey,
                stagingVersion,
                bytes.LongLength,
                expectedSha,
                new MediaContentType("image/png"),
                "originals",
                requiredMetadata);
            var state = new FakeIngestStatePort(work);
            var storage = new FakeBlobStore();
            storage.Add(
                stagingKey,
                bytes,
                stagingVersion,
                new BlobMediaType("image/png"),
                requiredMetadata,
                expectedSha);
            var imaging = new FakeImageProcessor(bytes.LongLength);
            return new IngestScenario(
                work,
                state,
                storage,
                imaging,
                new CrashCheckpointObserver());
        }

        internal ValueTask<JobHandlerResult> RunAsync(CancellationToken cancellationToken = default)
        {
            DurableJob job = CreateJob(
                Work.Fence.TenantId,
                $$"""{"uploadSessionId":"{{Work.Fence.UploadSessionId:D}}"}""");
            return Handler.HandleAsync(job, cancellationToken);
        }

        internal static DurableJob CreateJob(Guid tenantId, string payload) =>
            DurableJob.Create(
                new JobId(Guid.CreateVersion7()),
                new JobTenantId(tenantId),
                IngestJobHandler.SupportedJobType,
                payload,
                payloadVersion: 1,
                new JobDedupeKey($"ingest-{Guid.CreateVersion7():D}"),
                priority: 0,
                maxAttempts: 10,
                UtcNow,
                UtcNow);

        internal void MutateStaging(IngestMutation mutation)
        {
            FakeBlobObject current = Storage.Get(Work.StagingKey);
            switch (mutation)
            {
                case IngestMutation.Size:
                    Storage.Replace(
                        Work.StagingKey,
                        current with { Bytes = [.. current.Bytes, 0x01] });
                    break;
                case IngestMutation.Checksum:
                    Storage.Replace(
                        Work.StagingKey,
                        current with
                        {
                            Checksums =
                            [
                                new BlobChecksum(
                                    BlobChecksumAlgorithm.Sha256,
                                    new string('0', 64)),
                            ],
                        });
                    break;
                case IngestMutation.ContentType:
                    Storage.Replace(
                        Work.StagingKey,
                        current with { ContentType = new BlobMediaType("image/jpeg") });
                    break;
                case IngestMutation.Metadata:
                    Storage.Replace(
                        Work.StagingKey,
                        current with { Metadata = BlobMetadata.Empty });
                    break;
                case IngestMutation.Version:
                    Storage.Replace(
                        Work.StagingKey,
                        current with { Version = new BlobVersion("replacement-v2") });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }

        internal void AssertRejectedWithoutLeaks()
        {
            Assert.True(State.IsRejected);
            Assert.True(State.ReservationReleased);
            Assert.False(State.IsActivated);
            Assert.False(State.ReservationConsumed);
            Assert.False(State.DerivativesEnqueued);
            Assert.False(State.OutboxEnqueued);
            Assert.False(Storage.Contains(State.CanonicalKey));
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
