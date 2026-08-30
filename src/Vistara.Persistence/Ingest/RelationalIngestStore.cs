using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Application.Uploads.Quotas;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Ingest;

public enum PersistedIngestLoadDisposition
{
    Ready,
    Activated,
    Rejected,
    Completed,
    NotFound,
    Retry,
}

public sealed record PersistedIngestWork(
    Guid TenantId,
    Guid UploadSessionId,
    long UploadVersion,
    Guid OperationId,
    Guid ActorId,
    Guid ReservationId,
    string DisplayFileName,
    string StagingKey,
    string StagingProviderVersion,
    long ExpectedSizeBytes,
    string ExpectedSha256,
    string DeclaredContentType,
    string StorageContainer);

public sealed record PersistedIngestCleanup(
    Guid OperationId,
    string StagingKey,
    string StagingProviderVersion);

public sealed record PersistedIngestLoadResult(
    PersistedIngestLoadDisposition Disposition,
    PersistedIngestWork? Work,
    PersistedIngestCleanup? Cleanup);

public sealed record PersistedIngestPromotion(
    Guid OperationId,
    string Mode,
    string CanonicalKey);

public sealed record PersistedIngestActivationContext(
    Guid OperationId,
    string DisplayFileName,
    string StagingKey,
    string StagingProviderVersion,
    string StorageProvider,
    string StorageContainer,
    string PromotionMode,
    string CanonicalKey);

public sealed record PersistedIngestVerifiedObject(
    string StorageProvider,
    string Sha256,
    long SizeBytes,
    string DetectedFormat,
    string DetectedContentType);

public sealed class RelationalIngestStore(
    VistaraDbContext context,
    IClock clock,
    IUuid7Generator idGenerator)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));

    public async ValueTask<PersistedIngestLoadResult> LoadAndFenceAsync(
        Guid tenantId,
        Guid uploadSessionId,
        Guid candidateOperationId,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow? upload = await _context.UploadSessions
            .SingleOrDefaultAsync(
                row => row.Id == uploadSessionId,
                cancellationToken);
        if (upload is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedIngestLoadDisposition.NotFound, null, null);
        }

        IngestOperationRow? operation = await _context.IngestOperations
            .SingleOrDefaultAsync(
                row => row.UploadSessionId == uploadSessionId,
                cancellationToken);
        if (upload.State == "Rejected" || operation?.State == "Rejected")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedIngestLoadDisposition.Rejected, null, null);
        }

        if (upload.State is "Aborted" or "Expired")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedIngestLoadDisposition.Completed, null, null);
        }

        if (upload.State == "Accepted")
        {
            if (operation is null ||
                operation.CleanupCompletedAtUtc.HasValue ||
                upload.CleanupCompletedAtUtc.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedIngestLoadDisposition.Completed, null, null);
            }

            PersistedIngestCleanup cleanup = Cleanup(upload, operation);
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedIngestLoadDisposition.Activated, null, cleanup);
        }

        if (upload.State == "CommitRequested")
        {
            if (string.IsNullOrWhiteSpace(upload.StagingProviderVersion))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedIngestLoadDisposition.Retry, null, null);
            }

            upload.State = "Verifying";
            upload.IngestOperationId = candidateOperationId;
            upload.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
            upload.Version = checked(upload.Version + 1);
            operation = new IngestOperationRow
            {
                TenantId = tenantId,
                OperationId = candidateOperationId,
                UploadSessionId = uploadSessionId,
                FencedUploadVersion = upload.Version,
                State = "Fenced",
                CreatedAtUtc = upload.UpdatedAtUtc,
                UpdatedAtUtc = upload.UpdatedAtUtc,
                Version = 1,
            };
            _context.IngestOperations.Add(operation);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                PersistedIngestWork fencedWork =
                    await WorkAsync(upload, operation, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(
                    PersistedIngestLoadDisposition.Ready,
                    fencedWork,
                    null);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return new(PersistedIngestLoadDisposition.Retry, null, null);
            }
            catch (DbException)
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return new(PersistedIngestLoadDisposition.Retry, null, null);
            }
        }

        if (upload.State is not ("Verifying" or "OutcomeUnknown" or "Reconciling") ||
            operation is null ||
            upload.IngestOperationId != operation.OperationId ||
            upload.Version != operation.FencedUploadVersion ||
            string.IsNullOrWhiteSpace(upload.StagingProviderVersion))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedIngestLoadDisposition.Retry, null, null);
        }

        PersistedIngestWork work = await WorkAsync(
            upload,
            operation,
            cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return new(PersistedIngestLoadDisposition.Ready, work, null);
    }

    public async ValueTask<PersistedIngestPromotion> PlanPromotionAsync(
        Guid tenantId,
        Guid uploadSessionId,
        long uploadVersion,
        PersistedIngestVerifiedObject verified,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verified);
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow upload = await _context.UploadSessions
            .SingleAsync(row => row.Id == uploadSessionId, cancellationToken);
        IngestOperationRow operation = await _context.IngestOperations
            .SingleAsync(row => row.UploadSessionId == uploadSessionId, cancellationToken);
        EnsureFence(upload, operation, uploadVersion);

        if (operation.PromotionMode is not null &&
            operation.CanonicalKey is not null)
        {
            EnsureVerifiedIdentity(operation, verified);
            PersistedIngestPromotion replay = Promotion(operation);
            await transaction.RollbackAsync(cancellationToken);
            return replay;
        }

        BlobRow? existing = await _context.Blobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Provider == verified.StorageProvider &&
                    row.Sha256 == verified.Sha256 &&
                    row.SizeBytes == verified.SizeBytes &&
                    row.State == "Active",
                cancellationToken);
        operation.PromotionMode = existing is null
            ? "PromoteCreateOnly"
            : "ExistingExactBlob";
        operation.CanonicalKey = existing?.ObjectKey ??
            CanonicalKey(
                tenantId,
                uploadSessionId,
                verified.DetectedFormat);
        operation.StorageProvider = verified.StorageProvider;
        operation.VerifiedSha256 = verified.Sha256;
        operation.VerifiedSizeBytes = verified.SizeBytes;
        operation.DetectedFormat = verified.DetectedFormat;
        operation.DetectedContentType = verified.DetectedContentType;
        operation.State = "Planned";
        operation.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        operation.Version = checked(operation.Version + 1);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Promotion(operation);
    }

    public async ValueTask RecordPromotionOutcomeUnknownAsync(
        Guid tenantId,
        Guid uploadSessionId,
        long uploadVersion,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow upload = await _context.UploadSessions
            .SingleAsync(row => row.Id == uploadSessionId, cancellationToken);
        IngestOperationRow operation = await _context.IngestOperations
            .SingleAsync(row => row.OperationId == operationId, cancellationToken);
        EnsureFence(upload, operation, uploadVersion);
        if (operation.State != "PromotionOutcomeUnknown")
        {
            operation.State = "PromotionOutcomeUnknown";
            operation.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
            operation.Version = checked(operation.Version + 1);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<PersistedIngestActivationContext> GetActivationContextAsync(
        Guid tenantId,
        Guid uploadSessionId,
        long uploadVersion,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow upload = await _context.UploadSessions
            .AsNoTracking()
            .SingleAsync(row => row.Id == uploadSessionId, cancellationToken);
        IngestOperationRow operation = await _context.IngestOperations
            .AsNoTracking()
            .SingleAsync(row => row.OperationId == operationId, cancellationToken);
        if (operation.State is not ("Activated" or "CleanupCompleted"))
        {
            EnsureFence(upload, operation, uploadVersion);
        }
        else if (upload.IngestOperationId != operationId ||
                 upload.State != "Accepted")
        {
            throw new DbUpdateConcurrencyException(
                "The activated ingest operation no longer matches the upload.");
        }

        await transaction.RollbackAsync(cancellationToken);
        return new(
            operationId,
            upload.DisplayFileName,
            upload.StagingKey,
            upload.StagingProviderVersion
                ?? throw new InvalidOperationException(
                    "The upload staging version is missing."),
            operation.StorageProvider
                ?? throw new InvalidOperationException(
                    "The ingest storage provider is missing."),
            upload.StorageContainer ?? "media",
            operation.PromotionMode
                ?? throw new InvalidOperationException(
                    "The ingest promotion mode is missing."),
            operation.CanonicalKey
                ?? throw new InvalidOperationException(
                    "The ingest canonical key is missing."));
    }

    public async ValueTask RefreshDedupedBlobAsync(
        Guid tenantId,
        Guid uploadSessionId,
        long uploadVersion,
        Guid operationId,
        BlobIdentity canonicalIdentity,
        string? providerChecksum,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canonicalIdentity);
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow upload = await _context.UploadSessions
            .SingleAsync(row => row.Id == uploadSessionId, cancellationToken);
        IngestOperationRow operation = await _context.IngestOperations
            .SingleAsync(row => row.OperationId == operationId, cancellationToken);
        EnsureFence(upload, operation, uploadVersion);
        if (operation.PromotionMode != "ExistingExactBlob" ||
            !string.Equals(
                operation.CanonicalKey,
                canonicalIdentity.Key.Value,
                StringComparison.Ordinal) ||
            operation.StorageProvider is null ||
            operation.VerifiedSha256 is null ||
            operation.VerifiedSizeBytes is null)
        {
            throw new InvalidOperationException(
                "The deduplicated canonical refresh does not match its durable plan.");
        }

        BlobRow blob = await _context.Blobs.SingleAsync(
            row =>
                row.Provider == operation.StorageProvider &&
                row.Sha256 == operation.VerifiedSha256 &&
                row.SizeBytes == operation.VerifiedSizeBytes &&
                row.State == "Active",
            cancellationToken);
        blob.ObjectKey = canonicalIdentity.Key.Value;
        blob.ProviderVersion = canonicalIdentity.Version.Value;
        blob.ProviderChecksum = providerChecksum;
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask RejectAsync(
        Guid tenantId,
        Guid uploadSessionId,
        long uploadVersion,
        Guid auditEventId,
        string rejectionCode,
        DateTimeOffset rejectedAtUtc,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow upload = await _context.UploadSessions
            .SingleAsync(row => row.Id == uploadSessionId, cancellationToken);
        IngestOperationRow? operation = await _context.IngestOperations
            .SingleOrDefaultAsync(
                row => row.UploadSessionId == uploadSessionId,
                cancellationToken);
        if (upload.State == "Rejected")
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        if (operation is null)
        {
            throw new DbUpdateConcurrencyException(
                "The ingest operation is missing.");
        }

        EnsureFence(upload, operation, uploadVersion);
        long expectedVersion = upload.Version;
        upload.State = "Rejected";
        upload.RejectionCode = rejectionCode;
        upload.RejectedAtUtc = rejectedAtUtc;
        upload.UpdatedAtUtc = rejectedAtUtc;
        upload.Version = checked(upload.Version + 1);
        _context.Entry(upload).Property(row => row.Version).OriginalValue =
            expectedVersion;
        operation.State = "Rejected";
        operation.RejectionCode = rejectionCode;
        operation.UpdatedAtUtc = rejectedAtUtc;
        operation.Version = checked(operation.Version + 1);
        _context.AuditEvents.Add(new AuditEventRow
        {
            Id = auditEventId,
            TenantId = tenantId,
            ActorKind = "System",
            ActorIdentifier = "ingest-worker",
            Action = "upload.rejected",
            ResourceType = "upload",
            ResourceIdentifier = uploadSessionId.ToString("D"),
            BeforeJson = "{}",
            AfterJson = JsonSerializer.Serialize(
                new { state = "rejected", code = rejectionCode },
                JsonOptions),
            Outcome = "Rejected",
            OccurredAtUtc = rejectedAtUtc,
        });
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CompleteCleanupAsync(
        Guid tenantId,
        Guid operationId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        IngestOperationRow operation = await _context.IngestOperations
            .SingleAsync(row => row.OperationId == operationId, cancellationToken);
        UploadSessionRow upload = await _context.UploadSessions
            .SingleAsync(
                row => row.Id == operation.UploadSessionId,
                cancellationToken);
        if (operation.CleanupCompletedAtUtc.HasValue &&
            upload.CleanupCompletedAtUtc.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        if (operation.State is not ("Activated" or "CleanupCompleted") ||
            upload.State != "Accepted")
        {
            throw new InvalidOperationException(
                "Cleanup cannot complete before upload activation.");
        }

        operation.State = "CleanupCompleted";
        operation.CleanupCompletedAtUtc = completedAtUtc;
        operation.UpdatedAtUtc = completedAtUtc;
        operation.Version = checked(operation.Version + 1);
        upload.CleanupCompletedAtUtc = completedAtUtc;
        upload.UpdatedAtUtc = completedAtUtc;
        upload.Version = checked(upload.Version + 1);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async ValueTask<PersistedIngestWork> WorkAsync(
        UploadSessionRow upload,
        IngestOperationRow operation,
        CancellationToken cancellationToken)
    {
        QuotaReservationRow reservation = await _context.QuotaReservations
            .AsNoTracking()
            .SingleAsync(
                row => row.UploadSessionId == upload.Id,
                cancellationToken);
        return new PersistedIngestWork(
            upload.TenantId,
            upload.Id,
            upload.Version,
            operation.OperationId,
            upload.ActorId,
            reservation.Id,
            upload.DisplayFileName,
            upload.StagingKey,
            upload.StagingProviderVersion!,
            upload.ExpectedBytes,
            upload.ExpectedSha256,
            upload.DeclaredContentType,
            upload.StorageContainer ?? "media");
    }

    private static PersistedIngestCleanup Cleanup(
        UploadSessionRow upload,
        IngestOperationRow operation) =>
        new(
            operation.OperationId,
            upload.StagingKey,
            upload.StagingProviderVersion
                ?? throw new InvalidOperationException(
                    "Activated upload is missing its staging version."));

    private static PersistedIngestPromotion Promotion(
        IngestOperationRow operation) =>
        new(
            operation.OperationId,
            operation.PromotionMode
                ?? throw new InvalidOperationException(
                    "The ingest promotion mode is missing."),
            operation.CanonicalKey
                ?? throw new InvalidOperationException(
                    "The ingest canonical key is missing."));

    private static void EnsureFence(
        UploadSessionRow upload,
        IngestOperationRow operation,
        long uploadVersion)
    {
        if (upload.Version != uploadVersion ||
            operation.FencedUploadVersion != uploadVersion ||
            upload.IngestOperationId != operation.OperationId ||
            upload.State is not ("Verifying" or "OutcomeUnknown" or "Reconciling"))
        {
            throw new DbUpdateConcurrencyException(
                "The ingest upload fence is no longer current.");
        }
    }

    private static void EnsureVerifiedIdentity(
        IngestOperationRow operation,
        PersistedIngestVerifiedObject verified)
    {
        if (!string.Equals(
                operation.StorageProvider,
                verified.StorageProvider,
                StringComparison.Ordinal) ||
            !string.Equals(
                operation.VerifiedSha256,
                verified.Sha256,
                StringComparison.Ordinal) ||
            operation.VerifiedSizeBytes != verified.SizeBytes ||
            !string.Equals(
                operation.DetectedFormat,
                verified.DetectedFormat,
                StringComparison.Ordinal) ||
            !string.Equals(
                operation.DetectedContentType,
                verified.DetectedContentType,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The verified ingest identity changed after promotion planning.");
        }
    }

    private static string CanonicalKey(
        Guid tenantId,
        Guid uploadSessionId,
        string detectedFormat)
    {
        string extension = detectedFormat.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "jpg",
            "png" => "png",
            "webp" => "webp",
            _ => throw new InvalidOperationException(
                "The verified image format is unsupported."),
        };
        string tenant = tenantId.ToString("D");
        string shard = tenantId.ToString("N")[..2];
        return $"originals/{shard}" +
            $"/{tenant}/{uploadSessionId:D}/1/{uploadSessionId:D}.{extension}";
    }

    private static DateTimeOffset EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The ingest clock must return UTC.");
        }

        return value;
    }
}
