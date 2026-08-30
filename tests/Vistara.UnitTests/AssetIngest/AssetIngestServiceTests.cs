using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Common.Events;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Domain.Assets;
using Vistara.Domain.Jobs;

namespace Vistara.UnitTests.AssetIngest;

public sealed class AssetIngestServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2035, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [Fact]
    public async Task New_promoted_content_is_activated_in_one_transaction()
    {
        FakeAssetIngestTransaction transaction = new();
        FakeAssetIngestUnitOfWork unitOfWork = new(transaction);
        AssetIngestService service = new(
            unitOfWork,
            new SequenceUuid7Generator(Now),
            new FixedClock(Now),
            DerivativePresetRegistry.Standard,
            new DescriptorImageProcessor());
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] =
            AssetIngestReservation.Reserved(
                command.TenantId,
                command.ReservationId,
                version: 3,
                expiresAtUtc: Now.AddMinutes(5));

        AssetIngestResult result = await service.IngestAsync(
            command,
            CancellationToken.None);

        Assert.Equal(AssetIngestDisposition.Created, result.Disposition);
        Assert.NotNull(result.Receipt);
        Assert.False(result.Receipt.BlobReused);
        Assert.Single(transaction.Blobs);
        Assert.Single(transaction.Assets);
        Assert.Single(transaction.Revisions);
        Assert.Equal(
            AssetIngestReservationState.Consumed,
            transaction.Reservations[command.ReservationId].State);
        Assert.Single(transaction.AuditRecords);
        Assert.Equal(4, transaction.Jobs.Count);
        Assert.Single(transaction.OutboxMessages);
        Assert.Single(transaction.Activations);
        Assert.Single(transaction.Operations);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Duplicate_content_reuses_tenant_blob_without_merging_logical_ownership()
    {
        FakeAssetIngestTransaction transaction = new();
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand first = CreateCommand(title: "First owner title");
        AssetIngestCommand second = CreateCommand(
            operationId: Id(12),
            uploadSessionId: Id(13),
            actorId: Id(14),
            reservationId: Id(15),
            title: "Second owner title");
        transaction.Reservations[first.ReservationId] = Reserved(first);
        transaction.Reservations[second.ReservationId] = Reserved(second);

        AssetIngestResult firstResult = await service.IngestAsync(
            first,
            CancellationToken.None);
        AssetIngestResult secondResult = await service.IngestAsync(
            second,
            CancellationToken.None);

        Assert.False(firstResult.Receipt?.BlobReused);
        Assert.True(secondResult.Receipt?.BlobReused);
        Assert.Equal(firstResult.Receipt?.BlobId, secondResult.Receipt?.BlobId);
        Assert.NotEqual(firstResult.Receipt?.AssetId, secondResult.Receipt?.AssetId);
        Assert.Equal(2, transaction.Assets.Count);
        Asset firstAsset = transaction.Assets[firstResult.Receipt!.AssetId];
        Asset secondAsset = transaction.Assets[secondResult.Receipt!.AssetId];
        Assert.Equal(first.ActorId, firstAsset.OwnerId);
        Assert.Equal(second.ActorId, secondAsset.OwnerId);
        Assert.Equal("First owner title", firstAsset.Title);
        Assert.Equal("Second owner title", secondAsset.Title);
        Assert.Single(firstAsset.Revisions);
        Assert.Single(secondAsset.Revisions);
    }

    [Fact]
    public async Task Matching_content_in_another_tenant_creates_a_separate_blob()
    {
        FakeAssetIngestTransaction transaction = new();
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand first = CreateCommand();
        AssetIngestCommand second = CreateCommand(
            tenantId: Id(21),
            operationId: Id(22),
            uploadSessionId: Id(23),
            actorId: Id(24),
            reservationId: Id(25));
        transaction.Reservations[first.ReservationId] = Reserved(first);
        transaction.Reservations[second.ReservationId] = Reserved(second);

        AssetIngestResult firstResult = await service.IngestAsync(
            first,
            CancellationToken.None);
        AssetIngestResult secondResult = await service.IngestAsync(
            second,
            CancellationToken.None);

        Assert.Equal(2, transaction.Blobs.Count);
        Assert.NotEqual(firstResult.Receipt?.BlobId, secondResult.Receipt?.BlobId);
        Assert.False(secondResult.Receipt?.BlobReused);
    }

    [Fact]
    public async Task Commit_concurrency_conflict_is_explicit_retryable_and_rolls_back()
    {
        FakeAssetIngestTransaction transaction = new();
        FakeAssetIngestUnitOfWork unitOfWork = new(transaction)
        {
            ForceConcurrencyConflict = true,
        };
        AssetIngestService service = CreateService(transaction, unitOfWork);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] = Reserved(command);

        AssetIngestResult result = await service.IngestAsync(
            command,
            CancellationToken.None);

        Assert.Equal(AssetIngestDisposition.RetryableConflict, result.Disposition);
        Assert.True(result.IsRetryable);
        Assert.Equal("asset_ingest.concurrency_conflict", result.Error?.Code);
        Assert.Empty(transaction.Assets);
        Assert.Equal(
            AssetIngestReservationState.Reserved,
            transaction.Reservations[command.ReservationId].State);
    }

    [Fact]
    public async Task Replaying_the_same_operation_returns_the_original_result_exactly_once()
    {
        FakeAssetIngestTransaction transaction = new();
        FakeAssetIngestUnitOfWork unitOfWork = new(transaction);
        AssetIngestService service = CreateService(transaction, unitOfWork);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] = Reserved(command);

        AssetIngestResult created = await service.IngestAsync(
            command,
            CancellationToken.None);
        AssetIngestResult replayed = await service.IngestAsync(
            command,
            CancellationToken.None);

        Assert.Equal(AssetIngestDisposition.Created, created.Disposition);
        Assert.Equal(AssetIngestDisposition.Replayed, replayed.Disposition);
        Assert.Equal(created.Receipt, replayed.Receipt);
        Assert.Equal(1, unitOfWork.CommitCount);
        Assert.Single(transaction.Assets);
        Assert.Single(transaction.AuditRecords);
        Assert.Equal(4, transaction.Jobs.Count);
        Assert.Single(transaction.OutboxMessages);
        Assert.Single(transaction.Activations);
    }

    [Fact]
    public async Task Already_consumed_reservation_rejects_and_rolls_back()
    {
        FakeAssetIngestTransaction transaction = new();
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] =
            Reserved(command).Consume(Id(99), Now.AddSeconds(-1));

        AssetIngestResult result = await service.IngestAsync(
            command,
            CancellationToken.None);

        Assert.Equal(AssetIngestDisposition.Rejected, result.Disposition);
        Assert.Equal("asset_ingest.reservation_already_consumed", result.Error?.Code);
        Assert.Empty(transaction.Blobs);
        Assert.Empty(transaction.Assets);
        Assert.Empty(transaction.OutboxMessages);
    }

    [Fact]
    public async Task Expired_reservation_rejects_and_rolls_back()
    {
        FakeAssetIngestTransaction transaction = new();
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] =
            AssetIngestReservation.Reserved(
                command.TenantId,
                command.ReservationId,
                version: 3,
                expiresAtUtc: Now);

        AssetIngestResult result = await service.IngestAsync(
            command,
            CancellationToken.None);

        Assert.Equal(AssetIngestDisposition.Rejected, result.Disposition);
        Assert.Equal("asset_ingest.reservation_expired", result.Error?.Code);
        Assert.Empty(transaction.Blobs);
        Assert.Empty(transaction.Assets);
        Assert.Empty(transaction.OutboxMessages);
    }

    public static TheoryData<FailurePoint> TransactionFailurePoints =>
        new()
        {
            FailurePoint.AddBlob,
            FailurePoint.AddAsset,
            FailurePoint.AddRevision,
            FailurePoint.ConsumeReservation,
            FailurePoint.AppendAudit,
            FailurePoint.AddJob,
            FailurePoint.ReserveEventSequence,
            FailurePoint.AppendOutbox,
            FailurePoint.ActivateUpload,
            FailurePoint.RecordOperation,
        };

    [Theory]
    [MemberData(nameof(TransactionFailurePoints))]
    public async Task Failure_at_each_transaction_step_rolls_back_every_mutation(
        FailurePoint failurePoint)
    {
        FakeAssetIngestTransaction transaction = new()
        {
            FailAt = failurePoint,
        };
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] = Reserved(command);

        await Assert.ThrowsAsync<SimulatedTransactionException>(
            async () => await service.IngestAsync(command, CancellationToken.None));

        Assert.Empty(transaction.Blobs);
        Assert.Empty(transaction.Assets);
        Assert.Empty(transaction.Revisions);
        Assert.Empty(transaction.AuditRecords);
        Assert.Empty(transaction.Jobs);
        Assert.Empty(transaction.OutboxMessages);
        Assert.Empty(transaction.Activations);
        Assert.Empty(transaction.Operations);
        Assert.Equal(
            AssetIngestReservationState.Reserved,
            transaction.Reservations[command.ReservationId].State);
    }

    [Fact]
    public async Task Audit_and_outbox_payloads_exclude_storage_and_private_metadata()
    {
        FakeAssetIngestTransaction transaction = new();
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] = Reserved(command);

        await service.IngestAsync(command, CancellationToken.None);

        string audit = string.Join(
            "|",
            transaction.AuditRecords.Single().After.Fields.Select(
                pair => $"{pair.Key}={pair.Value}"));
        string eventPayload =
            transaction.OutboxMessages.Single().Envelope.ClientPayload;
        foreach (string forbidden in new[]
        {
            command.Promotion.StorageProvider,
            command.Promotion.StorageContainer,
            command.Promotion.ObjectKey,
            command.Promotion.ProviderVersion!,
            command.Promotion.ProviderChecksum!,
            "private",
            command.Promotion.Sha256.Value,
        })
        {
            Assert.DoesNotContain(forbidden, audit, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                forbidden,
                eventPayload,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Job_and_event_payloads_use_the_standard_web_json_contract()
    {
        FakeAssetIngestTransaction transaction = new();
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] = Reserved(command);

        await service.IngestAsync(command, CancellationToken.None);

        Assert.Contains(
            "\"assetId\"",
            transaction.Jobs[0].Payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"assetId\"",
            transaction.OutboxMessages.Single().Envelope.ClientPayload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"AssetId\"",
            transaction.OutboxMessages.Single().Envelope.ClientPayload,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pre_generated_jobs_use_the_exact_canonical_generation_identity()
    {
        FakeAssetIngestTransaction transaction = new();
        AssetIngestService service = CreateService(transaction);
        AssetIngestCommand command = CreateCommand();
        transaction.Reservations[command.ReservationId] = Reserved(command);

        AssetIngestResult result = await service.IngestAsync(
            command,
            CancellationToken.None);

        AssetIngestReceipt receipt = Assert.IsType<AssetIngestReceipt>(result.Receipt);
        DurableJob job = Assert.Single(transaction.Jobs, candidate =>
            DerivativeJobContract.TryParse(
                candidate.Type,
                candidate.PayloadVersion,
                candidate.Payload,
                out DerivativeJobPayloadV1? parsed) &&
            parsed?.Generation.PresetName == "thumb");
        Assert.True(DerivativeJobContract.TryParse(
            job.Type,
            job.PayloadVersion,
            job.Payload,
            out DerivativeJobPayloadV1? payload));
        DerivativeGenerationRequest generation = Assert.IsType<DerivativeGenerationRequest>(
            DerivativePresetRegistry.Standard.ResolveDefault(
                new DerivativeSourceIdentity(
                    command.TenantId,
                    receipt.AssetId,
                    receipt.RevisionId,
                    revisionNumber: 1,
                    new ImageSha256(command.Promotion.Sha256.Value)),
                new DerivativePresetId("thumb", 1),
                new ImagePipelineFingerprint("asset-ingest-pipeline"))
            .GenerationRequest);

        Assert.Equal(generation.DedupeIdentity.Key, job.DedupeKey);
        Assert.Equal(
            DerivativeGenerationDescriptorV1.Create(generation),
            payload?.Generation);
        Assert.Contains(
            "\"pipelineFingerprint\":\"asset-ingest-pipeline\"",
            job.Payload,
            StringComparison.Ordinal);
        Assert.Contains("\"quality\":82", job.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Asset_revisions_expose_no_public_mutation_surface()
    {
        Assert.All(
            typeof(AssetRevision).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }

    private static AssetIngestService CreateService(
        FakeAssetIngestTransaction transaction,
        FakeAssetIngestUnitOfWork? unitOfWork = null) =>
        new(
            unitOfWork ?? new FakeAssetIngestUnitOfWork(transaction),
            new SequenceUuid7Generator(Now),
            new FixedClock(Now),
            DerivativePresetRegistry.Standard,
            new DescriptorImageProcessor());

    private static AssetIngestReservation Reserved(AssetIngestCommand command) =>
        AssetIngestReservation.Reserved(
            command.TenantId,
            command.ReservationId,
            version: 3,
            expiresAtUtc: Now.AddMinutes(5));

    private static AssetIngestCommand CreateCommand(
        Guid? tenantId = null,
        Guid? operationId = null,
        Guid? uploadSessionId = null,
        Guid? actorId = null,
        Guid? reservationId = null,
        string title = "Owner title") =>
        new(
            tenantId ?? Id(1),
            operationId ?? Id(2),
            uploadSessionId ?? Id(3),
            uploadVersion: 7,
            actorId ?? Id(4),
            reservationId ?? Id(5),
            title,
            AssetVisibility.Private,
            new AuthoritativeBlobPromotion(
                storageProvider: "local",
                storageContainer: "originals",
                objectKey: "originals/aa/object.jpg",
                providerVersion: "version-private",
                providerChecksum: "provider-private",
                new Sha256Checksum(new string('a', 64)),
                sizeBytes: 1234,
                new MediaContentType("image/jpeg"),
                new MediaDescriptor(
                    "jpeg",
                    new MediaContentType("image/jpeg"),
                    new PixelDimensions(640, 480),
                    frameCount: 1,
                    new MediaPrivacyMetadata(
                        new Dictionary<string, string>
                        {
                            ["orientation"] = "normal",
                        },
                        new Dictionary<string, string>
                        {
                            ["gps"] = "private",
                        }))));

    private static Guid Id(int suffix) =>
        Guid.Parse($"019cb10a-dc00-7000-8000-{suffix:D12}");

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class DescriptorImageProcessor : IImageProcessor
    {
        public ImageProcessorCapabilities Capabilities =>
            throw new NotSupportedException();

        public ImagePipelineFingerprint PipelineFingerprint { get; } =
            new("asset-ingest-pipeline");

        public ValueTask<ImageInspection> InspectAsync(
            IReplayableImageSource source,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ImageTransformResult> TransformAsync(
            IReplayableImageSource source,
            Stream destination,
            CanonicalTransformRecipe recipe,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SequenceUuid7Generator(DateTimeOffset timestamp) : IUuid7Generator
    {
        private int _sequence = 100;

        public Guid NewId()
        {
            _sequence++;
            return Guid.CreateVersion7(timestamp.AddMilliseconds(_sequence));
        }
    }

    private sealed class FakeAssetIngestUnitOfWork(
        FakeAssetIngestTransaction transaction) : IAssetIngestUnitOfWork
    {
        public int CommitCount { get; private set; }

        public bool ForceConcurrencyConflict { get; init; }

        public async ValueTask<AssetIngestResult> ExecuteAsync(
            Guid tenantId,
            Guid operationId,
            Func<IAssetIngestTransaction, CancellationToken, ValueTask<AssetIngestResult>> action,
            CancellationToken cancellationToken)
        {
            FakeAssetIngestTransaction.Snapshot snapshot = transaction.Capture();
            try
            {
                AssetIngestResult result = await action(transaction, cancellationToken);
                if (ForceConcurrencyConflict)
                {
                    transaction.Restore(snapshot);
                    return AssetIngestResult.RetryableConflict(
                        Vistara.Domain.Common.ResultError.Conflict(
                            "asset_ingest.concurrency_conflict",
                            "Retry the ingest transaction."));
                }

                if (result.Disposition == AssetIngestDisposition.Created)
                {
                    CommitCount++;
                }
                else
                {
                    transaction.Restore(snapshot);
                }

                return result;
            }
            catch
            {
                transaction.Restore(snapshot);
                throw;
            }
        }
    }

    private sealed class FakeAssetIngestTransaction : IAssetIngestTransaction
    {
        public FailurePoint? FailAt { get; init; }

        public Dictionary<AssetIngestBlobIdentity, BlobObjectMetadata> Blobs { get; } = [];

        public Dictionary<Guid, Asset> Assets { get; } = [];

        public Dictionary<Guid, AssetRevision> Revisions { get; } = [];

        public Dictionary<Guid, AssetIngestReservation> Reservations { get; } = [];

        public List<AuditRecord> AuditRecords { get; } = [];

        public List<DurableJob> Jobs { get; } = [];

        public List<OutboxMessage> OutboxMessages { get; } = [];

        public List<AssetIngestActivation> Activations { get; } = [];

        public Dictionary<(Guid TenantId, Guid OperationId), AssetIngestReceipt> Operations
        {
            get;
        } = [];

        public ValueTask<AssetIngestReceipt?> FindOperationAsync(
            Guid tenantId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.TryGetValue((tenantId, operationId), out AssetIngestReceipt? receipt);
            return ValueTask.FromResult(receipt);
        }

        public ValueTask<BlobObjectMetadata?> FindBlobAsync(
            AssetIngestBlobIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Blobs.TryGetValue(identity, out BlobObjectMetadata? blob);
            return ValueTask.FromResult(blob);
        }

        public ValueTask AddBlobAsync(
            AssetIngestBlobIdentity identity,
            BlobObjectMetadata blob,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Blobs.Add(identity, blob);
            ThrowIf(AssetIngestServiceTests.FailurePoint.AddBlob);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddAssetAsync(
            Asset asset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assets.Add(asset.Id, asset);
            ThrowIf(AssetIngestServiceTests.FailurePoint.AddAsset);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddRevisionAsync(
            AssetRevision revision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Revisions.Add(revision.Id, revision);
            ThrowIf(AssetIngestServiceTests.FailurePoint.AddRevision);
            return ValueTask.CompletedTask;
        }

        public ValueTask<AssetIngestReservationConsumeResult> ConsumeReservationAsync(
            Guid tenantId,
            Guid reservationId,
            Guid operationId,
            DateTimeOffset consumedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Reservations.TryGetValue(reservationId, out AssetIngestReservation? reservation))
            {
                return ValueTask.FromResult(AssetIngestReservationConsumeResult.NotFound());
            }

            if (reservation.TenantId != tenantId)
            {
                return ValueTask.FromResult(AssetIngestReservationConsumeResult.NotFound());
            }

            if (reservation.State == AssetIngestReservationState.Consumed)
            {
                return ValueTask.FromResult(
                    AssetIngestReservationConsumeResult.AlreadyConsumed(reservation));
            }

            if (reservation.State == AssetIngestReservationState.Expired ||
                consumedAtUtc >= reservation.ExpiresAtUtc)
            {
                return ValueTask.FromResult(
                    AssetIngestReservationConsumeResult.Expired(reservation));
            }

            if (reservation.State != AssetIngestReservationState.Reserved)
            {
                return ValueTask.FromResult(
                    AssetIngestReservationConsumeResult.InvalidState(reservation));
            }

            AssetIngestReservation consumed = reservation.Consume(operationId, consumedAtUtc);
            Reservations[reservationId] = consumed;
            ThrowIf(AssetIngestServiceTests.FailurePoint.ConsumeReservation);
            return ValueTask.FromResult(
                AssetIngestReservationConsumeResult.Consumed(consumed));
        }

        public ValueTask AppendAuditAsync(
            AuditRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuditRecords.Add(record);
            ThrowIf(AssetIngestServiceTests.FailurePoint.AppendAudit);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddJobAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Jobs.Add(job);
            ThrowIf(AssetIngestServiceTests.FailurePoint.AddJob);
            return ValueTask.CompletedTask;
        }

        public ValueTask<EventSequence> ReserveEventSequenceAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIf(AssetIngestServiceTests.FailurePoint.ReserveEventSequence);
            return ValueTask.FromResult(new EventSequence(OutboxMessages.Count + 1));
        }

        public ValueTask AppendOutboxAsync(
            OutboxMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutboxMessages.Add(message);
            ThrowIf(AssetIngestServiceTests.FailurePoint.AppendOutbox);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkUploadActivatedAsync(
            AssetIngestActivation activation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Activations.Add(activation);
            ThrowIf(AssetIngestServiceTests.FailurePoint.ActivateUpload);
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordOperationAsync(
            Guid tenantId,
            Guid operationId,
            AssetIngestReceipt receipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add((tenantId, operationId), receipt);
            ThrowIf(AssetIngestServiceTests.FailurePoint.RecordOperation);
            return ValueTask.CompletedTask;
        }

        public Snapshot Capture() =>
            new(
                new Dictionary<AssetIngestBlobIdentity, BlobObjectMetadata>(Blobs),
                new Dictionary<Guid, Asset>(Assets),
                new Dictionary<Guid, AssetRevision>(Revisions),
                new Dictionary<Guid, AssetIngestReservation>(Reservations),
                [.. AuditRecords],
                [.. Jobs],
                [.. OutboxMessages],
                [.. Activations],
                new Dictionary<(Guid TenantId, Guid OperationId), AssetIngestReceipt>(
                    Operations));

        public void Restore(Snapshot snapshot)
        {
            Restore(Blobs, snapshot.Blobs);
            Restore(Assets, snapshot.Assets);
            Restore(Revisions, snapshot.Revisions);
            Restore(Reservations, snapshot.Reservations);
            Restore(AuditRecords, snapshot.AuditRecords);
            Restore(Jobs, snapshot.Jobs);
            Restore(OutboxMessages, snapshot.OutboxMessages);
            Restore(Activations, snapshot.Activations);
            Restore(Operations, snapshot.Operations);
        }

        private void ThrowIf(FailurePoint expected)
        {
            if (expected == FailAt)
            {
                throw new SimulatedTransactionException(expected);
            }
        }

        private static void Restore<TKey, TValue>(
            Dictionary<TKey, TValue> target,
            Dictionary<TKey, TValue> source)
            where TKey : notnull
        {
            target.Clear();
            foreach ((TKey key, TValue value) in source)
            {
                target.Add(key, value);
            }
        }

        private static void Restore<T>(List<T> target, List<T> source)
        {
            target.Clear();
            target.AddRange(source);
        }

        public sealed record Snapshot(
            Dictionary<AssetIngestBlobIdentity, BlobObjectMetadata> Blobs,
            Dictionary<Guid, Asset> Assets,
            Dictionary<Guid, AssetRevision> Revisions,
            Dictionary<Guid, AssetIngestReservation> Reservations,
            List<AuditRecord> AuditRecords,
            List<DurableJob> Jobs,
            List<OutboxMessage> OutboxMessages,
            List<AssetIngestActivation> Activations,
            Dictionary<(Guid TenantId, Guid OperationId), AssetIngestReceipt> Operations);
    }

    public enum FailurePoint
    {
        AddBlob,
        AddAsset,
        AddRevision,
        ConsumeReservation,
        AppendAudit,
        AddJob,
        ReserveEventSequence,
        AppendOutbox,
        ActivateUpload,
        RecordOperation,
    }

    private sealed class SimulatedTransactionException(FailurePoint failurePoint)
        : Exception($"Simulated failure at {failurePoint}.");
}
