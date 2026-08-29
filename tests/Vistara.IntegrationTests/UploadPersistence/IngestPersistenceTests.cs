using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Common.Events;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Idempotency;
using Vistara.Domain.Assets;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Ingest;
using Xunit;

namespace Vistara.IntegrationTests.UploadPersistence;

public sealed class IngestPersistenceTests
{
    [Fact]
    public async Task Production_ingest_service_completes_the_persisted_upload_flow()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        UploadSessionSnapshot committed = await CreateCommittedUploadAsync(
            database,
            storage,
            tenantId,
            actorId,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2)),
            "full-flow");
        using ServiceProvider worker = database.CreateWorkerProvider(storage);
        await using AsyncServiceScope scope = worker.CreateAsyncScope();

        Vistara.Worker.Runtime.Jobs.JobHandlerResult result =
            await scope.ServiceProvider
                .GetRequiredService<IngestService>()
                .ProcessAsync(
                    tenantId,
                    committed.UploadId,
                    CancellationToken.None);

        Assert.True(result.IsSuccess);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Equal("Accepted", (await context.UploadSessions.SingleAsync()).State);
        Assert.Single(await context.Assets.ToListAsync());
        Vistara.Persistence.Model.AssetRevisionRow revision =
            Assert.Single(await context.AssetRevisions.ToListAsync());
        Assert.Equal("{}", revision.PrivateMetadataJson);
        Assert.Equal(5, await database.CountAsync("jobs"));
        Assert.Equal(1, await database.CountAsync("outbox_messages"));
        Assert.Equal(1, await database.CountAsync("audit_events"));
        Assert.False(storage.Contains(new BlobKey(committed.StagingKey)));
    }

    [Fact]
    public async Task Activation_is_atomic_exactly_once_and_cleanup_is_restart_durable()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        UploadSessionSnapshot committed = await CreateCommittedUploadAsync(
            database,
            storage,
            tenantId,
            actorId,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2)),
            "activation");

        IngestCleanupToken cleanupToken;
        using (ServiceProvider worker = database.CreateWorkerProvider(storage))
        {
            worker.ValidateVistaraWorkerPlatformComposition();
            await using AsyncServiceScope scope = worker.CreateAsyncScope();
            IIngestTransactionPort transactions =
                scope.ServiceProvider.GetRequiredService<IIngestTransactionPort>();
            IngestLoadResult loaded = await transactions.LoadAndFenceAsync(
                tenantId,
                committed.UploadId,
                CancellationToken.None);
            IngestWorkItem work = Assert.IsType<IngestWorkItem>(loaded.Work);
            VerifiedIngestObject verified = Verified(work);
            IngestPromotionPlan plan = await transactions.PlanPromotionAsync(
                work.Fence,
                verified,
                CancellationToken.None);
            IngestActivation activation = Activation(work, plan, verified);

            await transactions.ActivateAsync(activation, CancellationToken.None);
            await transactions.ActivateAsync(activation, CancellationToken.None);

            IngestLoadResult activated = await transactions.LoadAndFenceAsync(
                tenantId,
                committed.UploadId,
                CancellationToken.None);
            IngestCleanup cleanup = Assert.IsType<IngestCleanup>(activated.Cleanup);
            cleanupToken = cleanup.Token;
            await transactions.CompleteCleanupAsync(
                cleanup.Token,
                CancellationToken.None);
            await transactions.CompleteCleanupAsync(
                cleanup.Token,
                CancellationToken.None);
        }

        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Single(await context.Assets.ToListAsync());
        Assert.Single(await context.AssetRevisions.ToListAsync());
        Assert.Single(await context.Blobs.ToListAsync());
        Assert.Equal(5, await database.CountAsync("jobs"));
        Assert.Equal(1, await database.CountAsync("outbox_messages"));
        Assert.Equal(1, await database.CountAsync("audit_events"));
        Assert.Equal(1, await database.CountAsync("ingest_operations"));
        Assert.Equal(
            "Consumed",
            await context.QuotaReservations.Select(row => row.State).SingleAsync());
        Vistara.Persistence.Uploads.QuotaUsageRow usage =
            await context.QuotaUsage.SingleAsync();
        Assert.Equal(0, usage.ReservedJobs);
        Assert.Equal(5, usage.CommittedJobs);
        Vistara.Persistence.Model.UploadSessionRow upload =
            await context.UploadSessions.SingleAsync();
        Assert.Equal("Accepted", upload.State);
        Assert.NotNull(upload.CleanupCompletedAtUtc);
        Assert.Equal(cleanupToken.Value, upload.IngestOperationId?.ToString("D"));

        using ServiceProvider restarted = database.CreateWorkerProvider(storage);
        await using AsyncServiceScope restartedScope = restarted.CreateAsyncScope();
        IngestLoadResult completed = await restartedScope.ServiceProvider
            .GetRequiredService<IIngestTransactionPort>()
            .LoadAndFenceAsync(tenantId, committed.UploadId, CancellationToken.None);
        Assert.Equal(IngestLoadDisposition.Completed, completed.Disposition);
    }

    [Fact]
    public async Task Ingest_fence_and_promotion_plan_survive_restart()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        UploadSessionSnapshot committed = await CreateCommittedUploadAsync(
            database,
            storage,
            tenantId,
            actorId,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2)),
            "restart-plan");

        IngestWorkItem firstWork;
        IngestPromotionPlan firstPlan;
        using (ServiceProvider firstWorker = database.CreateWorkerProvider(storage))
        {
            await using AsyncServiceScope scope = firstWorker.CreateAsyncScope();
            IIngestTransactionPort transactions =
                scope.ServiceProvider.GetRequiredService<IIngestTransactionPort>();
            firstWork = Assert.IsType<IngestWorkItem>(
                (await transactions.LoadAndFenceAsync(
                    tenantId,
                    committed.UploadId,
                    CancellationToken.None)).Work);
            firstPlan = await transactions.PlanPromotionAsync(
                firstWork.Fence,
                Verified(firstWork),
                CancellationToken.None);
        }

        using ServiceProvider restartedWorker =
            database.CreateWorkerProvider(storage);
        await using AsyncServiceScope restartedScope =
            restartedWorker.CreateAsyncScope();
        IIngestTransactionPort restarted = restartedScope.ServiceProvider
            .GetRequiredService<IIngestTransactionPort>();
        IngestWorkItem restartedWork = Assert.IsType<IngestWorkItem>(
            (await restarted.LoadAndFenceAsync(
                tenantId,
                committed.UploadId,
                CancellationToken.None)).Work);
        IngestPromotionPlan replayedPlan = await restarted.PlanPromotionAsync(
            restartedWork.Fence,
            Verified(restartedWork),
            CancellationToken.None);

        Assert.Equal(firstWork.Fence, restartedWork.Fence);
        Assert.Equal(firstPlan, replayedPlan);
    }

    [Fact]
    public async Task Rejection_releases_quota_and_writes_one_audit_record()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        UploadSessionSnapshot committed = await CreateCommittedUploadAsync(
            database,
            storage,
            tenantId,
            actorId,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2)),
            "rejection");
        using ServiceProvider worker = database.CreateWorkerProvider(storage);
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        IIngestTransactionPort transactions =
            scope.ServiceProvider.GetRequiredService<IIngestTransactionPort>();
        IngestWorkItem work = Assert.IsType<IngestWorkItem>(
            (await transactions.LoadAndFenceAsync(
                tenantId,
                committed.UploadId,
                CancellationToken.None)).Work);
        var rejection = new IngestRejection(
            work.Fence,
            IngestRejectionCode.ChecksumMismatch,
            UploadPersistenceDatabase.Now.AddMinutes(1),
            QuarantineStaging: true,
            ReleaseReservation: true);

        await transactions.RejectAsync(rejection, CancellationToken.None);
        await transactions.RejectAsync(rejection, CancellationToken.None);

        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Equal("Rejected", (await context.UploadSessions.SingleAsync()).State);
        Assert.Equal(
            "Released",
            await context.QuotaReservations.Select(row => row.State).SingleAsync());
        Vistara.Persistence.Uploads.QuotaUsageRow usage =
            await context.QuotaUsage.SingleAsync();
        Assert.Equal(0, usage.ReservedJobs);
        Assert.Equal(0, usage.CommittedJobs);
        Assert.Equal(1, await database.CountAsync("audit_events"));
        Assert.Empty(await context.Assets.ToListAsync());
    }

    [Fact]
    public async Task Blob_dedupe_is_same_tenant_only()
    {
        Guid tenantOne = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorOne = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid tenantTwo = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        Guid actorTwo = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(3));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantOne, actorOne);
        await database.SeedTenantAsync(tenantTwo, actorTwo);
        TestBlobStore storage = new();

        UploadSessionSnapshot first = await CreateCommittedUploadAsync(
            database,
            storage,
            tenantOne,
            actorOne,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(4)),
            "dedupe-one");
        UploadSessionSnapshot second = await CreateCommittedUploadAsync(
            database,
            storage,
            tenantOne,
            actorOne,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(5)),
            "dedupe-two");
        UploadSessionSnapshot otherTenant = await CreateCommittedUploadAsync(
            database,
            storage,
            tenantTwo,
            actorTwo,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(6)),
            "dedupe-three");

        await ActivateAsync(database, storage, tenantOne, first);
        await ActivateAsync(database, storage, tenantOne, second);
        await ActivateAsync(database, storage, tenantTwo, otherTenant);

        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantOne);
        Assert.Equal(
            2,
            await context.Blobs.IgnoreQueryFilters().CountAsync());
        Assert.Equal(
            3,
            await context.Assets.IgnoreQueryFilters().CountAsync());
        Vistara.Persistence.Model.TenantKey tenantOneKey = tenantOne;
        Guid[] tenantOneBlobIds = await context.AssetRevisions
            .Where(row => row.TenantId == tenantOneKey)
            .Select(row => row.BlobId)
            .ToArrayAsync();
        Assert.Equal(2, tenantOneBlobIds.Length);
        Assert.Single(tenantOneBlobIds.Distinct());
    }

    [Fact]
    public async Task Rejected_unit_of_work_rolls_back_all_tracked_mutations()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        using ServiceProvider worker =
            database.CreateWorkerProvider(new TestBlobStore());
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        IAssetIngestUnitOfWork unitOfWork =
            scope.ServiceProvider.GetRequiredService<IAssetIngestUnitOfWork>();

        AssetIngestResult result = await unitOfWork.ExecuteAsync(
            tenantId,
            Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2)),
            async (transaction, cancellationToken) =>
            {
                var identity = new AssetIngestBlobIdentity(
                    tenantId,
                    "test-provider",
                    new Sha256Checksum(new string('b', 64)),
                    42);
                var blob = new BlobObjectMetadata(
                    Guid.CreateVersion7(
                        UploadPersistenceDatabase.Now.AddMilliseconds(3)),
                    tenantId,
                    "test-provider",
                    "media",
                    $"originals/{tenantId:N}/rollback.jpg",
                    "version",
                    identity.Sha256,
                    "checksum",
                    42,
                    new MediaContentType("image/jpeg"),
                    UploadPersistenceDatabase.Now);
                Guid assetId = Guid.CreateVersion7(
                    UploadPersistenceDatabase.Now.AddMilliseconds(4));
                Asset asset = Asset.Create(
                    assetId,
                    tenantId,
                    actorId,
                    "Rollback",
                    AssetVisibility.Private,
                    UploadPersistenceDatabase.Now);
                var revision = new AssetRevision(
                    Guid.CreateVersion7(
                        UploadPersistenceDatabase.Now.AddMilliseconds(5)),
                    tenantId,
                    assetId,
                    1,
                    blob,
                    new MediaDescriptor(
                        "jpeg",
                        new MediaContentType("image/jpeg"),
                        new PixelDimensions(1, 1),
                        1,
                        new MediaPrivacyMetadata()),
                    UploadPersistenceDatabase.Now);
                Assert.True(asset.AddRevision(
                    revision,
                    UploadPersistenceDatabase.Now).IsSuccess);
                await transaction.AddBlobAsync(identity, blob, cancellationToken);
                await transaction.AddAssetAsync(asset, cancellationToken);
                await transaction.AddRevisionAsync(revision, cancellationToken);
                await transaction.AppendAuditAsync(
                    new AuditRecord(
                        new AuditEventId(Guid.CreateVersion7(
                            UploadPersistenceDatabase.Now.AddMilliseconds(6))),
                        new AuditTenantId(tenantId),
                        new AuditActor(AuditActorKind.User, actorId.ToString("D")),
                        "test.rollback",
                        new AuditResource("asset", assetId.ToString("D")),
                        AuditChangeSummary.Empty,
                        AuditChangeSummary.Empty,
                        AuditOutcome.Failed,
                        UploadPersistenceDatabase.Now),
                    cancellationToken);
                await transaction.AddJobAsync(
                    DurableJob.Create(
                        new JobId(Guid.CreateVersion7(
                            UploadPersistenceDatabase.Now.AddMilliseconds(7))),
                        new JobTenantId(tenantId),
                        new JobType("test.rollback"),
                        """{"safe":true}""",
                        1,
                        new JobDedupeKey($"rollback:{assetId:D}"),
                        0,
                        1,
                        UploadPersistenceDatabase.Now,
                        UploadPersistenceDatabase.Now),
                    cancellationToken);
                EventSequence sequence =
                    await transaction.ReserveEventSequenceAsync(
                        tenantId,
                        cancellationToken);
                await transaction.AppendOutboxAsync(
                    OutboxMessage.Create(
                        new OutboxMessageId(Guid.CreateVersion7(
                            UploadPersistenceDatabase.Now.AddMilliseconds(8))),
                        new EventEnvelope(
                            new EventMetadata(
                                new EventId(Guid.CreateVersion7(
                                    UploadPersistenceDatabase.Now.AddMilliseconds(9))),
                                new EventTenantId(tenantId),
                                sequence,
                                "test.rollback",
                                1,
                                UploadPersistenceDatabase.Now,
                                Guid.CreateVersion7(
                                    UploadPersistenceDatabase.Now.AddMilliseconds(10))),
                            """{"safe":true}"""),
                        UploadPersistenceDatabase.Now),
                    cancellationToken);
                return AssetIngestResult.Rejected(ResultError.Conflict(
                    "test.rollback",
                    "Rollback the transaction."));
            },
            CancellationToken.None);

        Assert.Equal(AssetIngestDisposition.Rejected, result.Disposition);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Empty(await context.Blobs.ToListAsync());
        Assert.Empty(await context.Assets.ToListAsync());
        Assert.Empty(await context.AssetRevisions.ToListAsync());
        Assert.Equal(0, await database.CountAsync("audit_events"));
        Assert.Equal(0, await database.CountAsync("jobs"));
        Assert.Equal(0, await database.CountAsync("outbox_messages"));
    }

    private static async ValueTask<UploadSessionSnapshot> CreateCommittedUploadAsync(
        UploadPersistenceDatabase database,
        TestBlobStore storage,
        Guid tenantId,
        Guid actorId,
        Guid uploadId,
        string idempotencyPrefix)
    {
        byte[] content = "durable-upload-payload"u8.ToArray();
        string sha256 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(content));
        using ServiceProvider api = database.CreateApiProvider(tenantId, storage);
        await using AsyncServiceScope scope = api.CreateAsyncScope();
        IUploadApplicationPort application =
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        UploadReserveResult reserved = await application.ReserveAsync(
            new ReserveUploadRequest(
                tenantId,
                actorId,
                uploadId,
                "direct",
                $"{idempotencyPrefix}.jpg",
                content.LongLength,
                "image/jpeg",
                sha256,
                $"staging/{tenantId:N}/{uploadId:N}",
                Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(
                        Encoding.UTF8.GetBytes(idempotencyPrefix))),
                new IdempotencyKey($"{idempotencyPrefix}-create"),
                UploadPersistenceDatabase.Now.AddHours(1)),
            CancellationToken.None);
        UploadIssuance issued = await application.IssueAsync(
            reserved.Session!,
            CancellationToken.None);
        storage.StoreUploaded(storage.LastDirectRequest!, content);
        UploadCommitResult committed = await application.CommitAsync(
            issued.Session,
            [],
            new IdempotencyKey($"{idempotencyPrefix}-commit"),
            issued.Session.Version,
            CancellationToken.None);
        return committed.Session!;
    }

    private static async ValueTask ActivateAsync(
        UploadPersistenceDatabase database,
        TestBlobStore storage,
        Guid tenantId,
        UploadSessionSnapshot upload)
    {
        using ServiceProvider worker = database.CreateWorkerProvider(storage);
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        IIngestTransactionPort transactions =
            scope.ServiceProvider.GetRequiredService<IIngestTransactionPort>();
        IngestWorkItem work = Assert.IsType<IngestWorkItem>(
            (await transactions.LoadAndFenceAsync(
                tenantId,
                upload.UploadId,
                CancellationToken.None)).Work);
        VerifiedIngestObject verified = Verified(work);
        IngestPromotionPlan plan = await transactions.PlanPromotionAsync(
            work.Fence,
            verified,
            CancellationToken.None);
        await transactions.ActivateAsync(
            Activation(work, plan, verified),
            CancellationToken.None);
    }

    private static VerifiedIngestObject Verified(IngestWorkItem work) =>
        new(
            new BlobIdentity(work.StagingKey, work.ExpectedStagingVersion),
            work.ExpectedSizeBytes,
            work.ExpectedSha256,
            new NormalizedIngestMedia(
                "jpeg",
                work.DeclaredContentType,
                640,
                480,
                1,
                ImageOrientation.Normal,
                HasExif: false,
                HasGps: false,
                HasXmp: false,
                HasIptc: false,
                HasComments: false,
                HasEmbeddedThumbnail: false,
                HasEmbeddedFileName: false));

    private static IngestActivation Activation(
        IngestWorkItem work,
        IngestPromotionPlan plan,
        VerifiedIngestObject verified)
    {
        BlobVersion version = new("canonical-version");
        BlobHead canonical = new(
            new BlobIdentity(plan.CanonicalKey, version),
            new BlobProperties(
                verified.SizeBytes,
                new BlobMediaType(verified.Media.ContentType.Value),
                UploadPersistenceDatabase.Now,
                version,
                new BlobEntityTag("canonical-etag"),
                [new BlobChecksum(
                    BlobChecksumAlgorithm.Sha256,
                    verified.Sha256.Value)],
                BlobMetadata.Empty));
        return new IngestActivation(
            work.Fence,
            work.ActorId,
            work.ReservationId,
            "test-provider",
            "media",
            plan,
            verified,
            plan.Mode == IngestPromotionMode.ExistingExactBlob ? null : canonical,
            UploadPersistenceDatabase.Now.AddMinutes(1),
            ConsumeReservation: true,
            EnqueueStandardDerivatives: true,
            EnqueueOutbox: true);
    }
}
