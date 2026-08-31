using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Features.Media;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.IntegrationTests.DerivativeWorker;
using Vistara.Storage.Local;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Runtime.Jobs;
using Xunit;
using Scenario =
    Vistara.IntegrationTests.DerivativeWorker.DerivativeWorkerTests.DerivativeScenario;

namespace Vistara.IntegrationTests.DerivativeConcurrency;

public sealed class DerivativeConcurrencyHardeningTests
{
    private static readonly ImageDecodeLimits DerivativeLimits = new(
        maxEncodedBytes: 1_024,
        maxWidth: 100,
        maxHeight: 100,
        maxAggregatePixels: 10_000,
        maxFrames: 1,
        maxEstimatedDecodedBytes: 40_000,
        processingDeadline: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Ten_identical_misses_converge_to_one_visible_create_and_identical_result()
    {
        Scenario scenario = Scenario.Valid();
        scenario.Imaging.PauseTransform = true;

        Task<JobHandlerResult> owner = scenario.RunAsync().AsTask();
        await scenario.Imaging.TransformStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<JobHandlerResult>[] competing = Enumerable.Range(0, 9)
            .Select(_ => scenario.RunAsync().AsTask())
            .ToArray();
        JobHandlerResult[] busy = await Task.WhenAll(competing)
            .WaitAsync(TimeSpan.FromSeconds(5));

        scenario.Imaging.AllowTransform.TrySetResult();
        JobHandlerResult completed = await owner.WaitAsync(TimeSpan.FromSeconds(5));
        JobHandlerResult[] retries = await Task.WhenAll(
                Enumerable.Range(0, 10)
                    .Select(_ => scenario.RunAsync().AsTask()))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(
            busy,
            result =>
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(JobFailureReason.LeaseExpired, result.Failure?.Reason);
            });
        Assert.True(completed.IsSuccess);
        Assert.All(retries, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, scenario.Imaging.TransformCalls);
        Assert.Equal(1, scenario.Storage.DestinationCreateCount);
        Assert.Equal(1, scenario.State.ReadyCommitCount);
        Assert.Equal(
            scenario.Imaging.OutputBytes,
            scenario.Storage.Get(scenario.DestinationKey).Bytes);
    }

    [Fact]
    public async Task Deterministic_key_and_bytes_do_not_change_across_retries()
    {
        Scenario scenario = Scenario.Valid();
        Assert.True((await scenario.RunAsync()).IsSuccess);
        BlobKey key = scenario.DestinationKey;
        byte[] originalBytes = scenario.Storage.Get(key).Bytes.ToArray();
        BlobVersion originalVersion = scenario.Storage.Get(key).Version;

        JobHandlerResult[] retries = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => scenario.RunAsync().AsTask()));

        Assert.All(retries, result => Assert.True(result.IsSuccess));
        Assert.Equal(key, scenario.DestinationKey);
        Assert.Equal(originalBytes, scenario.Storage.Get(key).Bytes);
        Assert.Equal(originalVersion, scenario.Storage.Get(key).Version);
        Assert.Equal(1, scenario.Storage.DestinationCreateCount);
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
    public async Task Cancellation_at_every_transition_exposes_no_partial_result_and_recovers(
        DerivativeCheckpoint checkpoint)
    {
        Scenario scenario = Scenario.Valid();
        scenario.Checkpoints.CancelAt = checkpoint;

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scenario.RunAsync());

        if (scenario.Storage.Contains(scenario.DestinationKey))
        {
            Assert.Equal(
                scenario.Imaging.OutputBytes,
                scenario.Storage.Get(scenario.DestinationKey).Bytes);
        }

        DeliveryObservation interrupted = await DeliverRangeAsync(scenario);
        if (scenario.State.IsReady)
        {
            Assert.Equal(HttpStatusCode.PartialContent, interrupted.StatusCode);
            Assert.Equal(scenario.Imaging.OutputBytes[..8], interrupted.Body);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Accepted, interrupted.StatusCode);
            Assert.DoesNotContain(
                scenario.Imaging.OutputBytes,
                interrupted.Body.AsSpan());
        }

        scenario.Checkpoints.CancelAt = null;
        scenario.Clock.Advance(Scenario.OwnershipDuration);
        JobHandlerResult recovered = await scenario.RunAsync();

        Assert.True(recovered.IsSuccess);
        DeliveryObservation delivered = await DeliverRangeAsync(scenario);
        Assert.Equal(HttpStatusCode.PartialContent, delivered.StatusCode);
        Assert.Equal(scenario.Imaging.OutputBytes[..8], delivered.Body);
        Assert.Equal(
            scenario.Imaging.OutputBytes,
            scenario.Storage.Get(scenario.DestinationKey).Bytes);
        Assert.DoesNotContain(
            scenario.Storage.Keys,
            key => key.Value.StartsWith("staging/derivatives/", StringComparison.Ordinal));
        Assert.Equal(1, scenario.State.ReadyCommitCount);
    }

    [Fact]
    public async Task Stale_fence_before_publication_cannot_make_destination_visible()
    {
        Scenario scenario = Scenario.Valid();
        scenario.Checkpoints.ActionAt = DerivativeCheckpoint.OutputStaged;
        scenario.Checkpoints.Action = scenario.State.ExpireAndStealFence;

        JobHandlerResult stale = await scenario.RunAsync();

        Assert.False(stale.IsSuccess);
        Assert.Equal(JobFailureReason.LeaseExpired, stale.Failure?.Reason);
        Assert.False(scenario.State.IsReady);
        Assert.False(scenario.Storage.Contains(scenario.DestinationKey));
        Assert.Equal(0, scenario.Storage.DestinationCreateCount);
    }

    [Fact]
    public async Task Theft_inside_publication_authorization_never_invokes_canonical_copy()
    {
        Scenario scenario = Scenario.Valid();
        var state = new StealingPublicationStatePort(scenario.State);
        var handler = new DerivativeJobHandler(
            new DerivativeService(
                state,
                scenario.Storage,
                scenario.Imaging,
                scenario.Clock,
                DerivativeLimits,
                scenario.Scratch,
                scenario.TransformGate,
                Scenario.OwnershipDuration,
                scenario.Checkpoints));

        JobHandlerResult stale = await handler.HandleAsync(
            scenario.CreateJob(),
            CancellationToken.None);

        Assert.False(stale.IsSuccess);
        Assert.Equal(JobFailureReason.LeaseExpired, stale.Failure?.Reason);
        Assert.True(state.AuthorizationApplied);
        Assert.False(state.PublicationOperationInvoked);
        Assert.False(scenario.State.IsReady);
        Assert.False(scenario.Storage.Contains(scenario.DestinationKey));
        Assert.Equal(0, scenario.Storage.DestinationCreateCount);
    }

    [Fact]
    public async Task Range_request_while_generation_is_processing_receives_no_staging_bytes()
    {
        Scenario scenario = Scenario.Valid();
        scenario.Imaging.PauseTransform = true;
        Task<JobHandlerResult> generation = scenario.RunAsync().AsTask();
        await scenario.Imaging.TransformStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        DeliveryObservation duringGeneration = await DeliverRangeAsync(scenario);

        Assert.Equal(HttpStatusCode.Accepted, duringGeneration.StatusCode);
        Assert.DoesNotContain(
            scenario.Imaging.OutputBytes,
            duringGeneration.Body.AsSpan());

        scenario.Imaging.AllowTransform.TrySetResult();
        Assert.True((await generation.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        DeliveryObservation afterPublication = await DeliverRangeAsync(scenario);
        Assert.Equal(HttpStatusCode.PartialContent, afterPublication.StatusCode);
        Assert.Equal(scenario.Imaging.OutputBytes[..8], afterPublication.Body);
    }

    [Fact]
    public async Task Local_create_only_publication_cannot_overwrite_existing_bytes()
    {
        string scratch = DerivativeScratchDirectory.Create();
        try
        {
            var store = new LocalBlobStore(
                new LocalBlobStoreOptions(Path.Combine(scratch, "store")));
            var staging = new BlobKey("staging/derivatives/tenant/request/1/candidate.webp");
            var destination = new BlobKey(
                "derivatives/v1/aa/source/recipe.webp");
            await PutAsync(store, staging, "candidate-bytes");
            BlobWriteResult original = await PutAsync(store, destination, "original-bytes");

            BlobStoreException conflict = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.CopyAsync(
                    staging,
                    destination,
                    new BlobCopyOptions(
                        SourceConditions: new BlobRequestConditions(
                            ifMatch: (await store.HeadAsync(
                                staging,
                                CancellationToken.None))!.Identity.Version),
                        DestinationConditions: BlobRequestConditions.CreateOnly),
                    CancellationToken.None));

            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, conflict.Code);
            Assert.Equal("original-bytes", await ReadTextAsync(store, destination));
            Assert.Equal(
                original.Head.Identity.Version,
                (await store.HeadAsync(destination, CancellationToken.None))!.Identity.Version);
        }
        finally
        {
            DerivativeScratchDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Concurrent_range_read_cannot_observe_local_staging_bytes()
    {
        string scratch = DerivativeScratchDirectory.Create();
        try
        {
            var store = new LocalBlobStore(
                new LocalBlobStoreOptions(Path.Combine(scratch, "store")));
            var key = new BlobKey("derivatives/v1/aa/source/recipe.webp");
            byte[] bytes = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();
            var content = new GatedBlobContent(bytes);

            Task<BlobWriteResult> publication = store.PutAsync(
                    key,
                    content,
                    new BlobWriteOptions(
                        new BlobMediaType("image/webp"),
                        checksums:
                        [
                            new BlobChecksum(
                                BlobChecksumAlgorithm.Sha256,
                                Convert.ToHexStringLower(SHA256.HashData(bytes))),
                        ],
                        conditions: BlobRequestConditions.CreateOnly),
                    CancellationToken.None)
                .AsTask();
            await content.FirstChunkWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(await store.HeadAsync(key, CancellationToken.None));
            BlobStoreException missing = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.OpenReadAsync(
                    key,
                    new BlobReadOptions(new BlobRange(17, 41)),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.NotFound, missing.Code);
            Assert.Empty(await ListAsync(store, "derivatives/"));
            Assert.Single(
                Directory.EnumerateFiles(scratch, "*.staging", SearchOption.AllDirectories));

            content.AllowCompletion.TrySetResult();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await using BlobReadHandle range = await store.OpenReadAsync(
                key,
                new BlobReadOptions(new BlobRange(17, 41)),
                CancellationToken.None);
            byte[] observed = new byte[41];
            await range.Content.ReadExactlyAsync(observed);

            Assert.Equal(bytes.AsSpan(17, 41).ToArray(), observed);
            Assert.Empty(
                Directory.EnumerateFiles(scratch, "*.staging", SearchOption.AllDirectories));
        }
        finally
        {
            DerivativeScratchDirectory.Delete(scratch);
        }
    }

    private static async Task<DeliveryObservation> DeliverRangeAsync(
        Scenario scenario)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Range = "bytes=0-7";
        context.Response.Body = new MemoryStream();
        await MediaDeliveryEndpoint.PublicDerivativeAsync(
            context,
            "v1",
            new string('a', 64),
            new string('b', 64),
            "webp",
            new ScenarioMediaApplicationPort(scenario),
            CancellationToken.None);
        return new DeliveryObservation(
            (HttpStatusCode)context.Response.StatusCode,
            ((MemoryStream)context.Response.Body).ToArray());
    }

    private static async ValueTask<BlobWriteResult> PutAsync(
        LocalBlobStore store,
        BlobKey key,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return await store.PutAsync(
            key,
            new ByteArrayBlobContent(bytes),
            new BlobWriteOptions(
                new BlobMediaType("application/octet-stream"),
                checksums:
                [
                    new BlobChecksum(
                        BlobChecksumAlgorithm.Sha256,
                        Convert.ToHexStringLower(SHA256.HashData(bytes))),
                ],
                conditions: BlobRequestConditions.CreateOnly),
            CancellationToken.None);
    }

    private static async Task<string> ReadTextAsync(
        LocalBlobStore store,
        BlobKey key)
    {
        await using BlobReadHandle handle = await store.OpenReadAsync(
            key,
            BlobReadOptions.Full,
            CancellationToken.None);
        using var reader = new StreamReader(handle.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<BlobHead>> ListAsync(
        LocalBlobStore store,
        string prefix)
    {
        var results = new List<BlobHead>();
        await foreach (BlobHead head in store.ListAsync(
                           new BlobListOptions(prefix),
                           CancellationToken.None))
        {
            results.Add(head);
        }

        return results;
    }

    private sealed record DeliveryObservation(
        HttpStatusCode StatusCode,
        byte[] Body);

    private sealed class StealingPublicationStatePort(
        FakeDerivativeStatePort inner) : IDerivativeStatePort
    {
        internal bool AuthorizationApplied { get; private set; }

        internal bool PublicationOperationInvoked { get; private set; }

        public ValueTask<DerivativeAcquireResult> AcquireAsync(
            DerivativeAcquireRequest request,
            CancellationToken cancellationToken) =>
            inner.AcquireAsync(request, cancellationToken);

        public ValueTask<DerivativeStateWriteResult> RecordStagedAsync(
            DerivativeFence fence,
            DerivativeStagedOutput staged,
            CancellationToken cancellationToken) =>
            inner.RecordStagedAsync(fence, staged, cancellationToken);

        public ValueTask<DerivativeStateWriteResult> RecordPublishOutcomeUnknownAsync(
            DerivativeFence fence,
            CancellationToken cancellationToken) =>
            inner.RecordPublishOutcomeUnknownAsync(fence, cancellationToken);

        public async ValueTask<DerivativePublicationOutcome> PublishIfOwnedAsync(
            DerivativeFence fence,
            DerivativeStagedOutput staged,
            DerivativePublicationOperation publish,
            CancellationToken cancellationToken)
        {
            DerivativeStateWriteResult authorized = await inner.RecordStagedAsync(
                fence,
                staged,
                cancellationToken);
            if (authorized == DerivativeStateWriteResult.Stale)
            {
                return DerivativePublicationOutcome.Stale;
            }

            AuthorizationApplied = true;
            inner.ExpireAndStealFence();
            DerivativeStateWriteResult stillOwned = await inner.RecordStagedAsync(
                fence,
                staged,
                cancellationToken);
            if (stillOwned == DerivativeStateWriteResult.Stale)
            {
                return DerivativePublicationOutcome.Stale;
            }

            PublicationOperationInvoked = true;
            DerivativePublicationAttemptOutcome attempt =
                await publish(cancellationToken);
            return attempt switch
            {
                DerivativePublicationAttemptOutcome.Published =>
                    DerivativePublicationOutcome.Published,
                DerivativePublicationAttemptOutcome.OutcomeUnknown =>
                    DerivativePublicationOutcome.OutcomeUnknown,
                DerivativePublicationAttemptOutcome.Retry =>
                    DerivativePublicationOutcome.Retry,
                _ => throw new InvalidOperationException(
                    "The derivative publication outcome is invalid."),
            };
        }

        public ValueTask<DerivativeStateWriteResult> MarkReadyAsync(
            DerivativeReadyOutput ready,
            CancellationToken cancellationToken) =>
            inner.MarkReadyAsync(ready, cancellationToken);

        public ValueTask<DerivativeStateWriteResult> MarkFailedAsync(
            DerivativeFailure failure,
            CancellationToken cancellationToken) =>
            inner.MarkFailedAsync(failure, cancellationToken);

        public ValueTask<DerivativeStateWriteResult> CompleteCleanupAsync(
            DerivativeFence fence,
            CancellationToken cancellationToken) =>
            inner.CompleteCleanupAsync(fence, cancellationToken);
    }

    private sealed class ScenarioMediaApplicationPort(Scenario scenario)
        : IMediaDeliveryApplicationPort
    {
        public ValueTask<MediaDeliveryResult> ResolvePublicDerivativeAsync(
            MediaDerivativeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!scenario.State.IsReady)
            {
                return ValueTask.FromResult(MediaDeliveryResult.Queued());
            }

            byte[] bytes = scenario.Storage.Get(scenario.DestinationKey).Bytes;
            return ValueTask.FromResult(
                MediaDeliveryResult.Ready(
                    new MediaRepresentation(
                        bytes.LongLength,
                        "image/webp",
                        Convert.ToHexStringLower(SHA256.HashData(bytes)),
                        new ByteArrayMediaSource(bytes))));
        }

        public ValueTask<MediaDeliveryResult> ResolvePrivateDerivativeAsync(
            MediaTenantScope scope,
            MediaDerivativeRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MediaDeliveryResult> ResolveAssetRenditionAsync(
            MediaRenditionScope scope,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MediaDeliveryResult> ResolveOriginalAsync(
            MediaAssetScope scope,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ByteArrayMediaSource(byte[] bytes) : IMediaContentSource
    {
        public ValueTask<MediaReadHandle> OpenReadAsync(
            MediaByteRange? range,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = checked((int)(range?.Offset ?? 0));
            int length = checked((int)(range?.Length ?? bytes.LongLength));
            return ValueTask.FromResult(
                new MediaReadHandle(
                    new MemoryStream(
                        bytes.AsSpan(offset, length).ToArray(),
                        writable: false)));
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

    private sealed class GatedBlobContent(byte[] bytes) : IReplayableBlobContent
    {
        public long Length => bytes.LongLength;

        public TaskCompletionSource FirstChunkWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(
                new GatedReadStream(
                    bytes,
                    FirstChunkWritten,
                    AllowCompletion));
        }
    }

    private sealed class GatedReadStream(
        byte[] bytes,
        TaskCompletionSource firstChunkWritten,
        TaskCompletionSource allowCompletion) : Stream
    {
        private const int FirstChunkLength = 4096;
        private int _position;
        private bool _waiting;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.LongLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= bytes.Length)
            {
                return 0;
            }

            if (_position > 0 && !_waiting)
            {
                _waiting = true;
                await allowCompletion.Task.WaitAsync(cancellationToken);
            }

            int maximum = _position == 0 ? FirstChunkLength : buffer.Length;
            int count = Math.Min(
                Math.Min(buffer.Length, maximum),
                bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            if (_position == FirstChunkLength)
            {
                firstChunkWritten.TrySetResult();
            }

            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

internal static class DerivativeScratchDirectory
{
    public static string Create()
    {
        string repositoryRoot = FindRepositoryRoot();
        string scratchRoot = Path.Combine(
            repositoryRoot,
            "tests",
            "Vistara.IntegrationTests",
            "DerivativeConcurrency",
            ".scratch");
        string path = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        DirectoryInfo? root = Directory.GetParent(path);
        if (root is not null && root.Exists && !root.EnumerateFileSystemInfos().Any())
        {
            root.Delete();
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vistara.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The Vistara repository root was not found.");
    }
}
