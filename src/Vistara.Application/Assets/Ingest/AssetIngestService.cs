using System.Globalization;
using System.Text.Json;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Common.Events;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Domain.Assets;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Assets.Ingest;

public sealed class AssetIngestService(
    IAssetIngestUnitOfWork unitOfWork,
    IUuid7Generator idGenerator,
    IClock clock,
    DerivativePresetRegistry derivativePresets,
    IImageProcessor derivativeImageProcessor)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly ResultError ReservationNotFound = ResultError.NotFound(
        "asset_ingest.reservation_not_found",
        "The upload quota reservation was not found.");

    private static readonly ResultError ReservationAlreadyConsumed = ResultError.Conflict(
        "asset_ingest.reservation_already_consumed",
        "The upload quota reservation has already been consumed.");

    private static readonly ResultError ReservationExpired = ResultError.Conflict(
        "asset_ingest.reservation_expired",
        "The upload quota reservation has expired.");

    private static readonly ResultError ReservationInvalidState = ResultError.Conflict(
        "asset_ingest.reservation_invalid_state",
        "The upload quota reservation cannot be consumed.");

    private static readonly ResultError ConcurrencyConflict = ResultError.Conflict(
        "asset_ingest.concurrency_conflict",
        "The ingest transaction conflicted with another writer and can be retried.");

    private readonly IAssetIngestUnitOfWork _unitOfWork =
        unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly DerivativePresetRegistry _derivativePresets =
        derivativePresets ??
        throw new ArgumentNullException(nameof(derivativePresets));
    private readonly IImageProcessor _derivativeImageProcessor =
        derivativeImageProcessor ??
        throw new ArgumentNullException(nameof(derivativeImageProcessor));

    public ValueTask<AssetIngestResult> IngestAsync(
        AssetIngestCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return _unitOfWork.ExecuteAsync(
            command.TenantId,
            command.OperationId,
            (transaction, token) => IngestInTransactionAsync(command, transaction, token),
            cancellationToken);
    }

    private async ValueTask<AssetIngestResult> IngestInTransactionAsync(
        AssetIngestCommand command,
        IAssetIngestTransaction transaction,
        CancellationToken cancellationToken)
    {
        AssetIngestReceipt? replay = await transaction.FindOperationAsync(
            command.TenantId,
            command.OperationId,
            cancellationToken);
        if (replay is not null)
        {
            return AssetIngestResult.Replayed(replay);
        }

        DateTimeOffset now = _clock.UtcNow;
        EnsureUtc(now);
        var identity = new AssetIngestBlobIdentity(
            command.TenantId,
            command.Promotion.StorageProvider,
            command.Promotion.Sha256,
            command.Promotion.SizeBytes);
        BlobObjectMetadata? blob = await transaction.FindBlobAsync(
            identity,
            cancellationToken);
        bool blobReused = blob is not null;
        if (blob is null)
        {
            blob = new BlobObjectMetadata(
                _idGenerator.NewId(),
                command.TenantId,
                command.Promotion.StorageProvider,
                command.Promotion.StorageContainer,
                command.Promotion.ObjectKey,
                command.Promotion.ProviderVersion,
                command.Promotion.Sha256,
                command.Promotion.ProviderChecksum,
                command.Promotion.SizeBytes,
                command.Promotion.ContentType,
                now);
            await transaction.AddBlobAsync(identity, blob, cancellationToken);
        }

        Guid assetId = _idGenerator.NewId();
        var asset = Asset.Create(
            assetId,
            command.TenantId,
            command.ActorId,
            command.Title,
            command.Visibility,
            now);
        var revision = new AssetRevision(
            _idGenerator.NewId(),
            command.TenantId,
            assetId,
            revisionNumber: 1,
            blob,
            command.Promotion.Media,
            now);
        Result addRevision = asset.AddRevision(revision, now);
        if (addRevision.IsFailure)
        {
            return AssetIngestResult.Rejected(addRevision.Error!);
        }

        await transaction.AddAssetAsync(asset, cancellationToken);
        await transaction.AddRevisionAsync(revision, cancellationToken);

        AssetIngestReservationConsumeResult reservation =
            await transaction.ConsumeReservationAsync(
                command.TenantId,
                command.ReservationId,
                command.OperationId,
                now,
                cancellationToken);
        AssetIngestResult? reservationFailure = MapReservationFailure(reservation);
        if (reservationFailure is not null)
        {
            return reservationFailure;
        }

        AssetIngestReceipt receipt = new(
            command.TenantId,
            command.OperationId,
            command.UploadSessionId,
            asset.Id,
            revision.Id,
            blob.Id,
            blobReused,
            now);

        await transaction.AppendAuditAsync(
            CreateAudit(command, receipt, now),
            cancellationToken);
        ImagePipelineFingerprint derivativePipeline =
            _derivativeImageProcessor.PipelineFingerprint;
        foreach (string preset in AssetReadinessPolicy.RequiredPresetNames)
        {
            await transaction.AddJobAsync(
                CreateDerivativeJob(
                    command,
                    receipt,
                    preset,
                    derivativePipeline,
                    now),
                cancellationToken);
        }

        EventSequence sequence = await transaction.ReserveEventSequenceAsync(
            command.TenantId,
            cancellationToken);
        await transaction.AppendOutboxAsync(
            CreateOutbox(command, receipt, sequence, now),
            cancellationToken);
        await transaction.MarkUploadActivatedAsync(
            new AssetIngestActivation(
                command.TenantId,
                command.UploadSessionId,
                command.UploadVersion,
                command.OperationId,
                receipt.AssetId,
                receipt.RevisionId,
                receipt.BlobId,
                now),
            cancellationToken);
        await transaction.RecordOperationAsync(
            command.TenantId,
            command.OperationId,
            receipt,
            cancellationToken);
        return AssetIngestResult.Created(receipt);
    }

    private static AssetIngestResult? MapReservationFailure(
        AssetIngestReservationConsumeResult result) =>
        result.Status switch
        {
            AssetIngestReservationConsumeStatus.Consumed => null,
            AssetIngestReservationConsumeStatus.NotFound =>
                AssetIngestResult.Rejected(ReservationNotFound),
            AssetIngestReservationConsumeStatus.AlreadyConsumed =>
                AssetIngestResult.Rejected(ReservationAlreadyConsumed),
            AssetIngestReservationConsumeStatus.Expired =>
                AssetIngestResult.Rejected(ReservationExpired),
            AssetIngestReservationConsumeStatus.InvalidState =>
                AssetIngestResult.Rejected(ReservationInvalidState),
            AssetIngestReservationConsumeStatus.ConcurrencyConflict =>
                AssetIngestResult.RetryableConflict(ConcurrencyConflict),
            _ => throw new InvalidOperationException("Unknown reservation consume status."),
        };

    private AuditRecord CreateAudit(
        AssetIngestCommand command,
        AssetIngestReceipt receipt,
        DateTimeOffset now)
    {
        Result<AuditChangeSummary> summary = AuditChangeSummary.Create(
        [
            AuditField.Plain("uploadSessionId", command.UploadSessionId.ToString("D")),
            AuditField.Plain("revisionId", receipt.RevisionId.ToString("D")),
            AuditField.Plain(
                "blobReused",
                receipt.BlobReused.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()),
            AuditField.Plain("state", "processing"),
        ]);
        if (!summary.TryGetValue(out AuditChangeSummary? after))
        {
            throw new InvalidOperationException(summary.Error?.Message);
        }

        return new AuditRecord(
            new AuditEventId(_idGenerator.NewId()),
            new AuditTenantId(command.TenantId),
            new AuditActor(AuditActorKind.User, command.ActorId.ToString("D")),
            "asset.ingested",
            new AuditResource("asset", receipt.AssetId.ToString("D")),
            AuditChangeSummary.Empty,
            after,
            AuditOutcome.Succeeded,
            now);
    }

    private DurableJob CreateDerivativeJob(
        AssetIngestCommand command,
        AssetIngestReceipt receipt,
        string preset,
        ImagePipelineFingerprint pipelineFingerprint,
        DateTimeOffset now)
    {
        DerivativeGenerationRequest generation =
            _derivativePresets.ResolveDefault(
                new DerivativeSourceIdentity(
                    command.TenantId,
                    receipt.AssetId,
                    receipt.RevisionId,
                    revisionNumber: 1,
                    new ImageSha256(command.Promotion.Sha256.Value)),
                preset,
                pipelineFingerprint)
            .GenerationRequest ??
            throw new InvalidOperationException(
                $"The pre-generation preset '{preset}' is unavailable.");
        DerivativeJobPayloadV1 jobPayload =
            DerivativeJobContract.CreatePayload(generation);
        return DurableJob.Create(
            new JobId(_idGenerator.NewId()),
            new JobTenantId(command.TenantId),
            DerivativeJobContract.Type,
            DerivativeJobContract.Serialize(jobPayload),
            DerivativeJobContract.PayloadVersion,
            DerivativeJobContract.CreateDedupeKey(jobPayload),
            priority: 0,
            maxAttempts: 5,
            availableAtUtc: now,
            createdAtUtc: now);
    }

    private OutboxMessage CreateOutbox(
        AssetIngestCommand command,
        AssetIngestReceipt receipt,
        EventSequence sequence,
        DateTimeOffset now)
    {
        string payload = JsonSerializer.Serialize(
            new AssetIngestedPayload(
                receipt.AssetId,
                receipt.RevisionId,
                receipt.UploadSessionId,
                "processing"),
            JsonOptions);
        var envelope = new EventEnvelope(
            new EventMetadata(
                new EventId(_idGenerator.NewId()),
                new EventTenantId(command.TenantId),
                sequence,
                "asset.ingested",
                eventVersion: 1,
                now,
                correlationId: command.OperationId),
            payload);
        return OutboxMessage.Create(
            new OutboxMessageId(_idGenerator.NewId()),
            envelope,
            now);
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The ingest clock must return UTC.");
        }
    }

    private sealed record AssetIngestedPayload(
        Guid AssetId,
        Guid RevisionId,
        Guid UploadSessionId,
        string State);
}
