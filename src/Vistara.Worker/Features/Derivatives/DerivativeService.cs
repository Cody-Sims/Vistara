using System.Buffers;
using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Derivatives;

public sealed class DerivativeService
{
    private const int HashBufferSize = 64 * 1024;
    private readonly IDerivativeStatePort _state;
    private readonly IBlobStore _blobStore;
    private readonly IImageProcessor _imageProcessor;
    private readonly IClock _clock;
    private readonly ImageDecodeLimits _limits;
    private readonly IDerivativeOutputScratchFactory _scratchFactory;
    private readonly DerivativeTransformGate _transformGate;
    private readonly TimeSpan _ownershipDuration;
    private readonly IDerivativeCheckpointObserver _checkpoints;

    public DerivativeService(
        IDerivativeStatePort state,
        IBlobStore blobStore,
        IImageProcessor imageProcessor,
        IClock clock,
        ImageDecodeLimits limits,
        IDerivativeOutputScratchFactory scratchFactory,
        DerivativeTransformGate transformGate,
        TimeSpan ownershipDuration,
        IDerivativeCheckpointObserver? checkpoints = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _scratchFactory = scratchFactory ??
            throw new ArgumentNullException(nameof(scratchFactory));
        _transformGate = transformGate ??
            throw new ArgumentNullException(nameof(transformGate));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            ownershipDuration,
            TimeSpan.Zero);
        _ownershipDuration = ownershipDuration;
        _checkpoints = checkpoints ?? NullDerivativeCheckpointObserver.Instance;
    }

    public async ValueTask<JobHandlerResult> ProcessAsync(
        DerivativeJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DerivativeAcquireResult acquired = await _state.AcquireAsync(
            new DerivativeAcquireRequest(
                request.TenantId,
                request.RequestId,
                request.Payload,
                _blobStore.Name,
                _imageProcessor.PipelineFingerprint,
                request.JobLease,
                _clock.UtcNow,
                _ownershipDuration),
            cancellationToken);
        switch (acquired.Disposition)
        {
            case DerivativeAcquireDisposition.Completed:
                return JobHandlerResult.Success();
            case DerivativeAcquireDisposition.Busy:
                return Failed(JobFailureReason.LeaseExpired);
            case DerivativeAcquireDisposition.NotFound:
                return Failed(JobFailureReason.ProcessingFailed);
            case DerivativeAcquireDisposition.Ready:
                return await CompleteReadyCleanupAsync(acquired, cancellationToken);
            case DerivativeAcquireDisposition.Acquired:
                break;
            default:
                throw new InvalidOperationException(
                    "The derivative acquire disposition is invalid.");
        }

        DerivativeFence fence = acquired.Fence
            ?? throw new InvalidOperationException("Acquired work lacks a fence.");
        DerivativeWorkItem work = acquired.Work
            ?? throw new InvalidOperationException("Acquired work lacks a work item.");
        DerivativeGenerationRequest generation = work.Generation;
        if (!MatchesAuthoritativeWork(request, generation, fence, work))
        {
            return Failed(JobFailureReason.ProcessingFailed);
        }

        await CheckpointAsync(DerivativeCheckpoint.OwnershipAcquired, cancellationToken);

        SourceValidation sourceValidation = await ValidateSourceAsync(
            work,
            cancellationToken);
        if (sourceValidation == SourceValidation.Changed)
        {
            return await FailPermanentlyAsync(
                fence,
                DerivativeFailureCode.SourceRevisionChanged,
                cancellationToken);
        }

        if (sourceValidation == SourceValidation.Retry)
        {
            return Failed(JobFailureReason.ProviderUnavailable);
        }

        await CheckpointAsync(DerivativeCheckpoint.SourceVerified, cancellationToken);

        BlobKey destinationKey = new(generation.CacheKey.Value);
        VisibilityResult existing = await VerifyVisibleAsync(
            destinationKey,
            generation,
            expectedSha256: null,
            cancellationToken);
        if (existing.Status == VisibilityStatus.Valid)
        {
            return await CommitReadyAndCleanupAsync(
                fence,
                generation,
                existing.Head!,
                existing.Sha256!,
                acquired.Staged,
                cancellationToken);
        }

        if (existing.Status == VisibilityStatus.Invalid)
        {
            return await FailPermanentlyAsync(
                fence,
                DerivativeFailureCode.DestinationIdentityConflict,
                cancellationToken);
        }

        if (existing.Status == VisibilityStatus.Retry)
        {
            return Failed(JobFailureReason.ProviderUnavailable);
        }

        DerivativeStagedOutput staged;
        if (acquired.Staged is not null)
        {
            VisibilityResult stagedVisibility = await VerifyStagedAsync(
                acquired.Staged,
                generation,
                cancellationToken);
            if (stagedVisibility.Status != VisibilityStatus.Valid)
            {
                return Failed(JobFailureReason.ProviderUnavailable);
            }

            staged = acquired.Staged;
        }
        else
        {
            StageResult stageResult = await TransformAndStageAsync(
                fence,
                work,
                generation,
                cancellationToken);
            if (stageResult.HandlerResult is not null)
            {
                return stageResult.HandlerResult;
            }

            staged = stageResult.Staged
                ?? throw new InvalidOperationException("Successful staging lacks output.");
        }

        DerivativePublicationOutcome publication =
            await _state.PublishIfOwnedAsync(
                fence,
                staged,
                publishCancellationToken => CopyAsync(
                    staged,
                    destinationKey,
                    generation,
                    publishCancellationToken),
                cancellationToken);
        if (publication == DerivativePublicationOutcome.Stale)
        {
            return Failed(JobFailureReason.LeaseExpired);
        }

        if (publication == DerivativePublicationOutcome.Retry)
        {
            return Failed(JobFailureReason.ProviderUnavailable);
        }

        await CheckpointAsync(DerivativeCheckpoint.DestinationPublished, cancellationToken);
        VisibilityResult visible = await VerifyVisibleAsync(
            destinationKey,
            generation,
            staged.Sha256,
            cancellationToken);
        if (visible.Status == VisibilityStatus.Missing ||
            visible.Status == VisibilityStatus.Retry)
        {
            return Failed(JobFailureReason.ProviderUnavailable);
        }

        if (visible.Status == VisibilityStatus.Invalid)
        {
            await DeleteStagingAsync(staged, cancellationToken);
            return await FailPermanentlyAsync(
                fence,
                DerivativeFailureCode.DestinationIdentityConflict,
                cancellationToken);
        }

        await CheckpointAsync(DerivativeCheckpoint.DestinationVisible, cancellationToken);
        return await CommitReadyAndCleanupAsync(
            fence,
            generation,
            visible.Head!,
            visible.Sha256!,
            staged,
            cancellationToken);
    }

    private bool MatchesAuthoritativeWork(
        DerivativeJobRequest request,
        DerivativeGenerationRequest generation,
        DerivativeFence fence,
        DerivativeWorkItem work) =>
        fence.TenantId == request.TenantId &&
        fence.RequestId == request.RequestId &&
        fence.JobLease.JobId == request.JobLease.JobId &&
        fence.JobLease.Owner == request.JobLease.Owner &&
        fence.JobLease.AcquiredAtUtc == request.JobLease.AcquiredAtUtc &&
        work.RequestId == request.RequestId &&
        generation.Source.TenantId == request.TenantId &&
        generation.Source.AssetId == request.Payload.AssetId &&
        generation.Source.RevisionId == request.Payload.RevisionId &&
        generation.Preset.Id ==
            new DerivativePresetId(
                request.Payload.Preset,
                DerivativeJobContract.PresetRevision) &&
        generation.PipelineFingerprint == _imageProcessor.PipelineFingerprint;

    private async ValueTask<SourceValidation> ValidateSourceAsync(
        DerivativeWorkItem work,
        CancellationToken cancellationToken)
    {
        try
        {
            BlobHead? head = await _blobStore.HeadAsync(
                work.SourceKey,
                cancellationToken);
            if (head is null ||
                head.Identity.Key != work.SourceKey ||
                head.Identity.Version != work.SourceVersion ||
                head.Properties.ContentLength != work.SourceLength)
            {
                return SourceValidation.Changed;
            }

            BlobChecksum? checksum = FindSha256(head);
            if (checksum is not null &&
                !string.Equals(
                    checksum.Value,
                    work.Generation.Source.SourceSha256.Value,
                    StringComparison.Ordinal))
            {
                return SourceValidation.Changed;
            }

            HashResult hash = await HashAsync(
                work.SourceKey,
                work.SourceVersion,
                cancellationToken);
            return hash.Bytes == work.SourceLength &&
                hash.Sha256 == work.Generation.Source.SourceSha256
                    ? SourceValidation.Valid
                    : SourceValidation.Changed;
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed)
        {
            return SourceValidation.Changed;
        }
        catch (BlobStoreException)
        {
            return SourceValidation.Retry;
        }
    }

    private async ValueTask<StageResult> TransformAndStageAsync(
        DerivativeFence fence,
        DerivativeWorkItem work,
        DerivativeGenerationRequest generation,
        CancellationToken cancellationToken)
    {
        ImageTransformResult transformed;
        await using IDerivativeOutputScratch scratch =
            await _scratchFactory.CreateAsync(
                _limits.MaxEncodedBytes,
                cancellationToken);
        try
        {
            transformed = await _transformGate.RunAsync(
                transformCancellationToken => _imageProcessor.TransformAsync(
                    new BlobImageSource(
                        _blobStore,
                        work.SourceKey,
                        work.SourceVersion,
                        work.SourceLength),
                    scratch.Destination,
                    generation.Recipe.ProcessorRecipe,
                    _limits,
                    transformCancellationToken),
                cancellationToken);
            await scratch.CompleteAsync(cancellationToken);
        }
        catch (ImageProcessorException)
        {
            return StageResult.Failed(
                await FailRetryableAsync(
                    fence,
                    DerivativeFailureCode.MediaDecodeFailed,
                    JobFailureReason.MediaDecodeFailed,
                    cancellationToken));
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed)
        {
            return StageResult.Failed(
                await FailPermanentlyAsync(
                    fence,
                    DerivativeFailureCode.SourceRevisionChanged,
                    cancellationToken));
        }
        catch (BlobStoreException)
        {
            return StageResult.Failed(Failed(JobFailureReason.ProviderUnavailable));
        }
        catch (IOException)
        {
            return StageResult.Failed(Failed(JobFailureReason.ProviderUnavailable));
        }

        HashResult scratchHash;
        try
        {
            scratchHash = await HashAsync(scratch, cancellationToken);
        }
        catch (IOException)
        {
            return StageResult.Failed(Failed(JobFailureReason.ProviderUnavailable));
        }

        if (!ValidateTransform(transformed, scratchHash, generation))
        {
            return StageResult.Failed(
                await FailRetryableAsync(
                    fence,
                    DerivativeFailureCode.UnsafeProcessorOutput,
                    JobFailureReason.MediaDecodeFailed,
                    cancellationToken));
        }

        await CheckpointAsync(DerivativeCheckpoint.OutputTransformed, cancellationToken);

        BlobKey stagingKey = CreateStagingKey(fence, generation);
        BlobMetadata metadata = CreateOutputMetadata(
            generation,
            transformed.Sha256);
        var options = new BlobWriteOptions(
            new BlobMediaType(generation.Output.ContentType),
            metadata,
            [new BlobChecksum(BlobChecksumAlgorithm.Sha256, transformed.Sha256.Value)],
            BlobRequestConditions.CreateOnly);
        BlobHead? stagedHead;
        try
        {
            BlobWriteResult written = await _blobStore.PutAsync(
                stagingKey,
                scratch,
                options,
                cancellationToken);
            stagedHead = written.Head;
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.PreconditionFailed or
                BlobStoreErrorCode.OutcomeUnknown)
        {
            stagedHead = await TryHeadAsync(stagingKey, cancellationToken);
        }
        catch (BlobStoreException)
        {
            return StageResult.Failed(Failed(JobFailureReason.ProviderUnavailable));
        }

        var staged = stagedHead is null
            ? null
            : new DerivativeStagedOutput(
                stagedHead.Identity,
                transformed.BytesWritten,
                transformed.Sha256,
                new BlobMediaType(generation.Output.ContentType));
        if (staged is null ||
            (await VerifyStagedAsync(staged, generation, cancellationToken)).Status !=
            VisibilityStatus.Valid)
        {
            return StageResult.Failed(Failed(JobFailureReason.ProviderUnavailable));
        }

        DerivativeStateWriteResult recorded = await _state.RecordStagedAsync(
            fence,
            staged,
            cancellationToken);
        if (recorded == DerivativeStateWriteResult.Stale)
        {
            await DeleteStagingAsync(staged, cancellationToken);
            return StageResult.Failed(Failed(JobFailureReason.LeaseExpired));
        }

        await CheckpointAsync(DerivativeCheckpoint.OutputStaged, cancellationToken);
        return StageResult.Success(staged);
    }

    private async ValueTask<DerivativePublicationAttemptOutcome> CopyAsync(
        DerivativeStagedOutput staged,
        BlobKey destinationKey,
        DerivativeGenerationRequest generation,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _blobStore.CopyAsync(
                staged.Identity.Key,
                destinationKey,
                new BlobCopyOptions(
                    SourceConditions: new BlobRequestConditions(
                        ifMatch: staged.Identity.Version),
                    DestinationConditions: BlobRequestConditions.CreateOnly,
                    ReplacementMetadata: CreateOutputMetadata(
                        generation,
                        staged.Sha256)),
                cancellationToken);
            return DerivativePublicationAttemptOutcome.Published;
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.OutcomeUnknown)
        {
            return DerivativePublicationAttemptOutcome.OutcomeUnknown;
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.PreconditionFailed)
        {
            BlobHead? destination = await TryHeadAsync(destinationKey, cancellationToken);
            if (destination is not null)
            {
                return DerivativePublicationAttemptOutcome.Published;
            }

            return DerivativePublicationAttemptOutcome.Retry;
        }
        catch (BlobStoreException)
        {
            return DerivativePublicationAttemptOutcome.Retry;
        }
    }

    private async ValueTask<JobHandlerResult> CommitReadyAndCleanupAsync(
        DerivativeFence fence,
        DerivativeGenerationRequest generation,
        BlobHead head,
        ImageSha256 sha256,
        DerivativeStagedOutput? staged,
        CancellationToken cancellationToken)
    {
        var result = new DerivativeGenerationResult(
            generation.Identity,
            generation.CacheKey,
            generation.Output,
            head.Properties.ContentLength,
            sha256);
        DerivativeStateWriteResult committed = await _state.MarkReadyAsync(
            new DerivativeReadyOutput(
                fence,
                result,
                head,
                _clock.UtcNow),
            cancellationToken);
        if (committed == DerivativeStateWriteResult.Stale)
        {
            return Failed(JobFailureReason.LeaseExpired);
        }

        await CheckpointAsync(DerivativeCheckpoint.ReadyCommitted, cancellationToken);
        if (staged is not null)
        {
            await DeleteStagingAsync(staged, cancellationToken);
        }

        await CheckpointAsync(DerivativeCheckpoint.StagingDeleted, cancellationToken);
        DerivativeStateWriteResult cleanup = await _state.CompleteCleanupAsync(
            fence,
            cancellationToken);
        if (cleanup == DerivativeStateWriteResult.Stale)
        {
            return Failed(JobFailureReason.LeaseExpired);
        }

        await CheckpointAsync(DerivativeCheckpoint.CleanupCommitted, cancellationToken);
        return JobHandlerResult.Success();
    }

    private async ValueTask<JobHandlerResult> CompleteReadyCleanupAsync(
        DerivativeAcquireResult acquired,
        CancellationToken cancellationToken)
    {
        DerivativeFence fence = acquired.Fence
            ?? throw new InvalidOperationException("Ready work lacks a fence.");
        if (acquired.Staged is not null)
        {
            await DeleteStagingAsync(acquired.Staged, cancellationToken);
        }

        await CheckpointAsync(DerivativeCheckpoint.StagingDeleted, cancellationToken);
        DerivativeStateWriteResult cleanup = await _state.CompleteCleanupAsync(
            fence,
            cancellationToken);
        if (cleanup == DerivativeStateWriteResult.Stale)
        {
            return Failed(JobFailureReason.LeaseExpired);
        }

        await CheckpointAsync(DerivativeCheckpoint.CleanupCommitted, cancellationToken);
        return JobHandlerResult.Success();
    }

    private async ValueTask<JobHandlerResult> FailPermanentlyAsync(
        DerivativeFence fence,
        DerivativeFailureCode code,
        CancellationToken cancellationToken)
    {
        DerivativeStateWriteResult failed = await _state.MarkFailedAsync(
            new DerivativeFailure(
                fence,
                code,
                Retryable: false,
                _clock.UtcNow),
            cancellationToken);
        return failed == DerivativeStateWriteResult.Stale
            ? Failed(JobFailureReason.LeaseExpired)
            : JobHandlerResult.Success();
    }

    private async ValueTask<JobHandlerResult> FailRetryableAsync(
        DerivativeFence fence,
        DerivativeFailureCode code,
        JobFailureReason reason,
        CancellationToken cancellationToken)
    {
        DerivativeStateWriteResult failed = await _state.MarkFailedAsync(
            new DerivativeFailure(
                fence,
                code,
                Retryable: true,
                _clock.UtcNow),
            cancellationToken);
        return failed == DerivativeStateWriteResult.Stale
            ? Failed(JobFailureReason.LeaseExpired)
            : Failed(reason);
    }

    private async ValueTask<VisibilityResult> VerifyStagedAsync(
        DerivativeStagedOutput staged,
        DerivativeGenerationRequest generation,
        CancellationToken cancellationToken)
    {
        VisibilityResult result = await VerifyVisibleAsync(
            staged.Identity.Key,
            generation,
            staged.Sha256,
            cancellationToken);
        return result.Status == VisibilityStatus.Valid &&
            result.Head!.Identity.Version == staged.Identity.Version &&
            result.Head.Properties.ContentLength == staged.Bytes &&
            result.Head.Properties.ContentType == staged.ContentType
                ? result
                : result.Status == VisibilityStatus.Retry
                    ? result
                    : VisibilityResult.Invalid();
    }

    private async ValueTask<VisibilityResult> VerifyVisibleAsync(
        BlobKey key,
        DerivativeGenerationRequest generation,
        ImageSha256? expectedSha256,
        CancellationToken cancellationToken)
    {
        BlobHead? head;
        try
        {
            head = await _blobStore.HeadAsync(key, cancellationToken);
        }
        catch (BlobStoreException)
        {
            return VisibilityResult.RetryLater();
        }

        if (head is null)
        {
            return VisibilityResult.Missing();
        }

        if (head.Identity.Key != key ||
            head.Properties.ContentLength <= 0 ||
            !string.Equals(
                head.Properties.ContentType.Value,
                generation.Output.ContentType,
                StringComparison.Ordinal) ||
            !HasIdentityMetadata(head.Properties.Metadata, generation, out string? metadataSha))
        {
            return VisibilityResult.Invalid();
        }

        var metadataChecksum = new ImageSha256(metadataSha);
        if (expectedSha256 is not null && metadataChecksum != expectedSha256)
        {
            return VisibilityResult.Invalid();
        }

        BlobChecksum? providerChecksum = FindSha256(head);
        if (providerChecksum is not null &&
            !string.Equals(
                providerChecksum.Value,
                metadataChecksum.Value,
                StringComparison.Ordinal))
        {
            return VisibilityResult.Invalid();
        }

        try
        {
            HashResult hash = await HashAsync(
                key,
                head.Identity.Version,
                cancellationToken);
            return hash.Bytes == head.Properties.ContentLength &&
                hash.Sha256 == metadataChecksum
                    ? VisibilityResult.Valid(head, hash.Sha256)
                    : VisibilityResult.Invalid();
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed)
        {
            return VisibilityResult.RetryLater();
        }
        catch (BlobStoreException)
        {
            return VisibilityResult.RetryLater();
        }
    }

    private async ValueTask<HashResult> HashAsync(
        BlobKey key,
        BlobVersion version,
        CancellationToken cancellationToken)
    {
        await using BlobReadHandle handle = await _blobStore.OpenReadAsync(
            key,
            new BlobReadOptions(
                Conditions: new BlobRequestConditions(ifMatch: version)),
            cancellationToken);
        if (handle.Head.Identity.Key != key ||
            handle.Head.Identity.Version != version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "The object changed while it was opened.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        long bytes = 0;
        try
        {
            int read;
            while ((read = await handle.Content.ReadAsync(
                       buffer.AsMemory(0, HashBufferSize),
                       cancellationToken)) > 0)
            {
                bytes = checked(bytes + read);
                hash.AppendData(buffer, 0, read);
            }

            return new HashResult(
                bytes,
                new ImageSha256(Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async ValueTask<HashResult> HashAsync(
        IReplayableBlobContent content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.OpenReadAsync(cancellationToken);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        long bytes = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(
                       buffer.AsMemory(0, HashBufferSize),
                       cancellationToken)) > 0)
            {
                bytes = checked(bytes + read);
                hash.AppendData(buffer, 0, read);
            }

            return new HashResult(
                bytes,
                new ImageSha256(Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async ValueTask DeleteStagingAsync(
        DerivativeStagedOutput staged,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _blobStore.DeleteAsync(
                staged.Identity.Key,
                new BlobDeleteOptions(
                    new BlobRequestConditions(ifMatch: staged.Identity.Version)),
                cancellationToken);
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.NotFound)
        {
        }
    }

    private async ValueTask<BlobHead?> TryHeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _blobStore.HeadAsync(key, cancellationToken);
        }
        catch (BlobStoreException)
        {
            return null;
        }
    }

    private static bool ValidateTransform(
        ImageTransformResult transformed,
        HashResult scratch,
        DerivativeGenerationRequest generation)
    {
        ImagePrivacyMetadata privacy = transformed.Output.Privacy;
        return transformed.BytesWritten == scratch.Bytes &&
            transformed.Output.EncodedBytes == scratch.Bytes &&
            transformed.Output.Format == MapFormat(generation.Recipe.Format) &&
            string.Equals(
                transformed.Output.ContentType.Value,
                generation.Output.ContentType,
                StringComparison.Ordinal) &&
            transformed.Output.Width <= generation.Recipe.Dimensions.Width &&
            transformed.Output.Height <= generation.Recipe.Dimensions.Height &&
            transformed.Output.FrameCount == 1 &&
            !privacy.HasExif &&
            !privacy.HasGps &&
            !privacy.HasXmp &&
            !privacy.HasIptc &&
            !privacy.HasComments &&
            !privacy.HasEmbeddedThumbnail &&
            !privacy.HasEmbeddedFileName &&
            transformed.Sha256 == scratch.Sha256 &&
            string.Equals(
                transformed.RecipeFingerprint.Value,
                generation.Recipe.ProcessorRecipe.Fingerprint.Value,
                StringComparison.Ordinal) &&
            transformed.PipelineFingerprint == generation.PipelineFingerprint;
    }

    private static ImageFormat MapFormat(DerivativeFormat format) => format switch
    {
        DerivativeFormat.Jpeg => ImageFormat.Jpeg,
        DerivativeFormat.Png => ImageFormat.Png,
        DerivativeFormat.WebP => ImageFormat.WebP,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static BlobKey CreateStagingKey(
        DerivativeFence fence,
        DerivativeGenerationRequest generation) =>
        new(
            $"staging/derivatives/{fence.TenantId:N}/{fence.RequestId:N}/" +
            $"{fence.Version}/{generation.Identity.Value}.{generation.Output.FileExtension}");

    private static BlobMetadata CreateOutputMetadata(
        DerivativeGenerationRequest generation,
        ImageSha256 representationSha256) =>
        new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vistara-derivative-id"] = generation.Identity.Value,
                ["vistara-pipeline-fingerprint"] = generation.PipelineFingerprint.Value,
                ["vistara-recipe-sha256"] = generation.Recipe.Fingerprint,
                ["vistara-representation-sha256"] = representationSha256.Value,
            });

    private static bool HasIdentityMetadata(
        BlobMetadata metadata,
        DerivativeGenerationRequest generation,
        out string representationSha256)
    {
        representationSha256 = string.Empty;
        if (!metadata.TryGetValue("vistara-derivative-id", out string? identity) ||
            !string.Equals(identity, generation.Identity.Value, StringComparison.Ordinal) ||
            !metadata.TryGetValue(
                "vistara-pipeline-fingerprint",
                out string? pipeline) ||
            !string.Equals(
                pipeline,
                generation.PipelineFingerprint.Value,
                StringComparison.Ordinal) ||
            !metadata.TryGetValue("vistara-recipe-sha256", out string? recipe) ||
            !string.Equals(
                recipe,
                generation.Recipe.Fingerprint,
                StringComparison.Ordinal) ||
            !metadata.TryGetValue(
                "vistara-representation-sha256",
                out string? observedSha256) ||
            observedSha256 is null ||
            observedSha256.Length != 64 ||
            !observedSha256.All(Uri.IsHexDigit))
        {
            return false;
        }

        representationSha256 = observedSha256;
        return true;
    }

    private static BlobChecksum? FindSha256(BlobHead head) =>
        head.Properties.Checksums.SingleOrDefault(
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);

    private ValueTask CheckpointAsync(
        DerivativeCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        _checkpoints.ReachedAsync(checkpoint, cancellationToken);

    private static JobHandlerResult Failed(JobFailureReason reason) =>
        JobHandlerResult.Failed(new JobFailure(reason));

    private enum SourceValidation
    {
        Valid,
        Changed,
        Retry,
    }

    private enum VisibilityStatus
    {
        Missing,
        Valid,
        Invalid,
        Retry,
    }

    private sealed record HashResult(long Bytes, ImageSha256 Sha256);

    private sealed class VisibilityResult
    {
        private VisibilityResult(
            VisibilityStatus status,
            BlobHead? head,
            ImageSha256? sha256)
        {
            Status = status;
            Head = head;
            Sha256 = sha256;
        }

        internal VisibilityStatus Status { get; }

        internal BlobHead? Head { get; }

        internal ImageSha256? Sha256 { get; }

        internal static VisibilityResult Missing() =>
            new(VisibilityStatus.Missing, null, null);

        internal static VisibilityResult Valid(BlobHead head, ImageSha256 sha256) =>
            new(VisibilityStatus.Valid, head, sha256);

        internal static VisibilityResult Invalid() =>
            new(VisibilityStatus.Invalid, null, null);

        internal static VisibilityResult RetryLater() =>
            new(VisibilityStatus.Retry, null, null);
    }

    private sealed record StageResult(
        DerivativeStagedOutput? Staged,
        JobHandlerResult? HandlerResult)
    {
        internal static StageResult Success(DerivativeStagedOutput staged) =>
            new(staged, null);

        internal static StageResult Failed(JobHandlerResult result) =>
            new(null, result);
    }

    private sealed class BlobImageSource(
        IBlobStore blobStore,
        BlobKey key,
        BlobVersion version,
        long length) : IReplayableImageSource
    {
        public long? Length { get; } = length;

        public bool OpensSeekableStreams => false;

        public async ValueTask<Stream> OpenReadAsync(
            CancellationToken cancellationToken)
        {
            BlobReadHandle handle = await blobStore.OpenReadAsync(
                key,
                new BlobReadOptions(
                    Conditions: new BlobRequestConditions(ifMatch: version)),
                cancellationToken);
            if (handle.Head.Identity.Key != key ||
                handle.Head.Identity.Version != version)
            {
                await handle.DisposeAsync();
                throw new BlobStoreException(
                    BlobStoreErrorCode.PreconditionFailed,
                    "The source revision changed while it was opened.");
            }

            return new OwnedBlobReadStream(handle);
        }
    }

    private sealed class OwnedBlobReadStream(BlobReadHandle handle) : Stream
    {
        private bool _disposed;

        public override bool CanRead => handle.Content.CanRead;

        public override bool CanSeek => handle.Content.CanSeek;

        public override bool CanWrite => false;

        public override long Length => handle.Content.Length;

        public override long Position
        {
            get => handle.Content.Position;
            set => handle.Content.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            handle.Content.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            handle.Content.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            handle.Content.Seek(offset, origin);

        public override void Flush() => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await handle.DisposeAsync();
                _disposed = true;
            }

            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
