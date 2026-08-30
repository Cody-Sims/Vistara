using Microsoft.EntityFrameworkCore;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Assets;
using Vistara.Persistence.Ingest;
using Vistara.Worker.Features.Ingest;

namespace Vistara.Worker.Composition.Platform;

internal sealed class WorkerIngestPersistenceAdapter(
    WorkerTenantContext tenantContext,
    RelationalIngestStore store,
    AssetIngestService assetIngest,
    IBlobStore blobStore,
    IClock clock,
    IUuid7Generator idGenerator) : IIngestTransactionPort
{
    private readonly WorkerTenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    private readonly RelationalIngestStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly AssetIngestService _assetIngest =
        assetIngest ?? throw new ArgumentNullException(nameof(assetIngest));
    private readonly IBlobStore _blobStore =
        blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
    private Guid? _activeTenantId;

    public async ValueTask<IngestLoadResult> LoadAndFenceAsync(
        Guid tenantId,
        Guid uploadSessionId,
        CancellationToken cancellationToken)
    {
        EstablishTenant(tenantId);
        PersistedIngestLoadResult result = await _store.LoadAndFenceAsync(
            tenantId,
            uploadSessionId,
            _idGenerator.NewId(),
            cancellationToken);
        return result.Disposition switch
        {
            PersistedIngestLoadDisposition.Ready when result.Work is not null =>
                IngestLoadResult.Ready(ToWorker(result.Work)),
            PersistedIngestLoadDisposition.Activated when result.Cleanup is not null =>
                IngestLoadResult.Activated(ToWorker(result.Cleanup)),
            PersistedIngestLoadDisposition.Rejected => IngestLoadResult.Rejected(),
            PersistedIngestLoadDisposition.Completed => IngestLoadResult.Completed(),
            PersistedIngestLoadDisposition.NotFound => IngestLoadResult.NotFound(),
            _ => IngestLoadResult.Retry(),
        };
    }

    public async ValueTask<IngestPromotionPlan> PlanPromotionAsync(
        IngestFence fence,
        VerifiedIngestObject verified,
        CancellationToken cancellationToken)
    {
        EstablishTenant(fence.TenantId);
        PersistedIngestPromotion result = await _store.PlanPromotionAsync(
            fence.TenantId,
            fence.UploadSessionId,
            fence.Version,
            new PersistedIngestVerifiedObject(
                _blobStore.Name,
                verified.Sha256.Value,
                verified.SizeBytes,
                verified.Media.DetectedFormat,
                verified.Media.ContentType.Value),
            cancellationToken);
        return new IngestPromotionPlan(
            new IngestPromotionToken(result.OperationId.ToString("D")),
            Enum.Parse<IngestPromotionMode>(result.Mode),
            new BlobKey(result.CanonicalKey));
    }

    public ValueTask RecordPromotionOutcomeUnknownAsync(
        IngestFence fence,
        IngestPromotionPlan plan,
        CancellationToken cancellationToken)
    {
        EstablishTenant(fence.TenantId);
        return _store.RecordPromotionOutcomeUnknownAsync(
            fence.TenantId,
            fence.UploadSessionId,
            fence.Version,
            ParseOperationId(plan.Token.Value),
            cancellationToken);
    }

    public async ValueTask ActivateAsync(
        IngestActivation activation,
        CancellationToken cancellationToken)
    {
        EstablishTenant(activation.Fence.TenantId);
        Guid operationId = ParseOperationId(activation.Plan.Token.Value);
        PersistedIngestActivationContext context =
            await _store.GetActivationContextAsync(
                activation.Fence.TenantId,
                activation.Fence.UploadSessionId,
                activation.Fence.Version,
                operationId,
                cancellationToken);
        ValidateActivation(activation, context);
        BlobHead? canonical = activation.CanonicalHead;
        string? providerChecksum = canonical?.Properties.Checksums
            .FirstOrDefault(checksum =>
                checksum.Algorithm == BlobChecksumAlgorithm.Sha256)
            ?.Value ?? canonical?.Properties.EntityTag.Value;
        if (activation.Plan.Mode == IngestPromotionMode.ExistingExactBlob)
        {
            await _store.RefreshDedupedBlobAsync(
                activation.Fence.TenantId,
                activation.Fence.UploadSessionId,
                activation.Fence.Version,
                operationId,
                canonical!.Identity,
                providerChecksum,
                cancellationToken);
        }

        var media = new MediaDescriptor(
            activation.Verified.Media.DetectedFormat,
            new MediaContentType(activation.Verified.Media.ContentType.Value),
            new PixelDimensions(
                activation.Verified.Media.Width,
                activation.Verified.Media.Height),
            activation.Verified.Media.FrameCount,
            new MediaPrivacyMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["orientation"] =
                        activation.Verified.Media.Orientation.ToString(),
                    ["hadExif"] = Bool(activation.Verified.Media.HasExif),
                    ["hadGps"] = Bool(activation.Verified.Media.HasGps),
                    ["hadXmp"] = Bool(activation.Verified.Media.HasXmp),
                    ["hadIptc"] = Bool(activation.Verified.Media.HasIptc),
                    ["hadComments"] = Bool(activation.Verified.Media.HasComments),
                    ["hadEmbeddedThumbnail"] =
                        Bool(activation.Verified.Media.HasEmbeddedThumbnail),
                    ["hadEmbeddedFileName"] =
                        Bool(activation.Verified.Media.HasEmbeddedFileName),
                }));
        AssetIngestResult result = await _assetIngest.IngestAsync(
            new AssetIngestCommand(
                activation.Fence.TenantId,
                context.OperationId,
                activation.Fence.UploadSessionId,
                activation.Fence.Version,
                activation.ActorId,
                activation.ReservationId,
                context.DisplayFileName,
                AssetVisibility.Private,
                new AuthoritativeBlobPromotion(
                    activation.StorageProvider,
                    activation.StorageContainer,
                    activation.Plan.CanonicalKey.Value,
                    canonical?.Identity.Version.Value,
                    providerChecksum,
                    activation.Verified.Sha256,
                    activation.Verified.SizeBytes,
                    new MediaContentType(
                        activation.Verified.Media.ContentType.Value),
                    media)),
            cancellationToken);
        if (result.Disposition == AssetIngestDisposition.RetryableConflict)
        {
            throw new DbUpdateConcurrencyException(
                result.Error?.Message ?? "The ingest transaction conflicted.");
        }

        if (result.Disposition == AssetIngestDisposition.Rejected)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "The ingest activation was rejected.");
        }
    }

    public ValueTask RejectAsync(
        IngestRejection rejection,
        CancellationToken cancellationToken)
    {
        EstablishTenant(rejection.Fence.TenantId);
        return _store.RejectAsync(
            rejection.Fence.TenantId,
            rejection.Fence.UploadSessionId,
            rejection.Fence.Version,
            _idGenerator.NewId(),
            rejection.Code.ToString(),
            rejection.RejectedAtUtc,
            cancellationToken);
    }

    public ValueTask CompleteCleanupAsync(
        IngestCleanupToken cleanupToken,
        CancellationToken cancellationToken)
    {
        if (_activeTenantId is not { } tenantId)
        {
            throw new InvalidOperationException(
                "An ingest tenant must be established before cleanup.");
        }

        return _store.CompleteCleanupAsync(
            tenantId,
            ParseOperationId(cleanupToken.Value),
            _clock.UtcNow,
            cancellationToken);
    }

    private void EstablishTenant(Guid tenantId)
    {
        if (_activeTenantId.HasValue && _activeTenantId.Value != tenantId)
        {
            throw new InvalidOperationException(
                "An ingest adapter cannot switch tenants within one scope.");
        }

        _tenantContext.Establish(tenantId);
        _activeTenantId = tenantId;
    }

    private static IngestWorkItem ToWorker(PersistedIngestWork work) =>
        new(
            new IngestFence(
                work.TenantId,
                work.UploadSessionId,
                work.UploadVersion),
            work.ActorId,
            work.ReservationId,
            new BlobKey(work.StagingKey),
            new BlobVersion(work.StagingProviderVersion),
            work.ExpectedSizeBytes,
            new Sha256Checksum(work.ExpectedSha256),
            new MediaContentType(work.DeclaredContentType),
            work.StorageContainer,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vistara-tenant-id"] = work.TenantId.ToString("D"),
                ["vistara-upload-id"] = work.UploadSessionId.ToString("D"),
            });

    private static IngestCleanup ToWorker(PersistedIngestCleanup cleanup) =>
        new(
            new IngestCleanupToken(cleanup.OperationId.ToString("D")),
            new BlobKey(cleanup.StagingKey),
            new BlobVersion(cleanup.StagingProviderVersion));

    private static Guid ParseOperationId(string value) =>
        Guid.TryParse(value, out Guid operationId) &&
        operationId != Guid.Empty &&
        operationId.Version == 7
            ? operationId
            : throw new InvalidOperationException(
                "The persisted ingest operation token is invalid.");

    private void ValidateActivation(
        IngestActivation activation,
        PersistedIngestActivationContext context)
    {
        if (!activation.ConsumeReservation ||
            !activation.EnqueueStandardDerivatives ||
            !activation.EnqueueOutbox ||
            !string.Equals(
                activation.StorageProvider,
                _blobStore.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                activation.StorageProvider,
                context.StorageProvider,
                StringComparison.Ordinal) ||
            !string.Equals(
                activation.StorageContainer,
                context.StorageContainer,
                StringComparison.Ordinal) ||
            !string.Equals(
                activation.Verified.SourceIdentity.Key.Value,
                context.StagingKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                activation.Verified.SourceIdentity.Version.Value,
                context.StagingProviderVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                activation.Plan.Mode.ToString(),
                context.PromotionMode,
                StringComparison.Ordinal) ||
            !string.Equals(
                activation.Plan.CanonicalKey.Value,
                context.CanonicalKey,
                StringComparison.Ordinal) ||
            activation.CanonicalHead is null ||
            activation.CanonicalHead.Identity.Key != activation.Plan.CanonicalKey)
        {
            throw new InvalidOperationException(
                "The ingest activation does not match its durable plan.");
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
