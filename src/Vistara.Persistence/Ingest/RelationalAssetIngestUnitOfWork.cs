using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Common.Events;
using Vistara.Domain.Assets;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Persistence.Outbox;
using Vistara.Persistence.Repositories;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Ingest;

public sealed class RelationalAssetIngestUnitOfWork(VistaraDbContext context)
    : IAssetIngestUnitOfWork
{
    private static readonly ResultError ConcurrencyConflict = ResultError.Conflict(
        "asset_ingest.concurrency_conflict",
        "The ingest transaction conflicted with another writer and can be retried.");

    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<AssetIngestResult> ExecuteAsync(
        Guid tenantId,
        Guid operationId,
        Func<IAssetIngestTransaction, CancellationToken, ValueTask<AssetIngestResult>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        _context.EstablishTenant(tenantId);
        await using IDbContextTransaction databaseTransaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        var transaction = new RelationalAssetIngestTransaction(_context);
        try
        {
            AssetIngestResult result = await action(transaction, cancellationToken);
            if (result.Disposition != AssetIngestDisposition.Created)
            {
                await databaseTransaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return result;
            }

            await _context.SaveChangesAsync(cancellationToken);
            transaction.ApplyCurrentRevisionLinks();
            await _context.SaveChangesAsync(cancellationToken);
            await databaseTransaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            await databaseTransaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return await ReplayOrConflictAsync(
                tenantId,
                operationId,
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            await databaseTransaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return await ReplayOrConflictAsync(
                tenantId,
                operationId,
                cancellationToken);
        }
        catch (DbException)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            await databaseTransaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return await ReplayOrConflictAsync(
                tenantId,
                operationId,
                cancellationToken);
        }
        catch
        {
            await databaseTransaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    private async ValueTask<AssetIngestResult> ReplayOrConflictAsync(
        Guid tenantId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        AssetIngestReceipt? receipt =
            await RelationalAssetIngestTransaction.FindReceiptAsync(
                _context,
                tenantId,
                operationId,
                cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return receipt is null
            ? AssetIngestResult.RetryableConflict(ConcurrencyConflict)
            : AssetIngestResult.Replayed(receipt);
    }
}

internal sealed class RelationalAssetIngestTransaction(
    VistaraDbContext context) : IAssetIngestTransaction
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly VistaraDbContext _context = context;
    private readonly List<(AssetRow Row, Guid RevisionId)> _currentRevisionLinks = [];
    private readonly OutboxRepository _outbox = new(context, context);

    public ValueTask<AssetIngestReceipt?> FindOperationAsync(
        Guid tenantId,
        Guid operationId,
        CancellationToken cancellationToken) =>
        FindReceiptAsync(_context, tenantId, operationId, cancellationToken);

    public async ValueTask<BlobObjectMetadata?> FindBlobAsync(
        AssetIngestBlobIdentity identity,
        CancellationToken cancellationToken)
    {
        TenantKey tenantId = identity.TenantId;
        BlobRow? row = await _context.Blobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TenantId == tenantId &&
                    candidate.Provider == identity.StorageProvider &&
                    candidate.Sha256 == identity.Sha256.Value &&
                    candidate.SizeBytes == identity.SizeBytes &&
                    candidate.State == "Active",
                cancellationToken);
        return row is null ? null : DomainMapper.ToDomain(row);
    }

    public ValueTask AddBlobAsync(
        AssetIngestBlobIdentity identity,
        BlobObjectMetadata blob,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (identity.TenantId != blob.TenantId ||
            !string.Equals(
                identity.StorageProvider,
                blob.Provider,
                StringComparison.Ordinal) ||
            identity.Sha256 != blob.Sha256 ||
            identity.SizeBytes != blob.SizeBytes)
        {
            throw new InvalidOperationException(
                "The blob does not match its ingest dedupe identity.");
        }

        _context.Blobs.Add(DomainMapper.ToRow(blob));
        return ValueTask.CompletedTask;
    }

    public ValueTask AddAssetAsync(
        Asset asset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssetRow row = DomainMapper.ToRow(asset);
        if (asset.CurrentRevision is { } revision)
        {
            row.CurrentRevisionId = null;
            _currentRevisionLinks.Add((row, revision.Id));
        }

        _context.Assets.Add(row);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddRevisionAsync(
        AssetRevision revision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.AssetRevisions.Add(DomainMapper.ToRow(revision));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<AssetIngestReservationConsumeResult>
        ConsumeReservationAsync(
            Guid tenantId,
            Guid reservationId,
            Guid operationId,
            DateTimeOffset consumedAtUtc,
            CancellationToken cancellationToken)
    {
        TenantKey tenantKey = tenantId;
        QuotaReservationRow? row = await _context.QuotaReservations
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TenantId == tenantKey &&
                    candidate.Id == reservationId,
                cancellationToken);
        if (row is null)
        {
            return AssetIngestReservationConsumeResult.NotFound();
        }

        AssetIngestReservation current = ToIngestReservation(row);
        if (row.State == "Consumed")
        {
            return AssetIngestReservationConsumeResult.AlreadyConsumed(current);
        }

        if (row.State != "Reserved")
        {
            return AssetIngestReservationConsumeResult.InvalidState(current);
        }

        if (consumedAtUtc >= row.ExpiresAtUtc)
        {
            return AssetIngestReservationConsumeResult.Expired(current);
        }

        QuotaUsageRow? usage = await _context.QuotaUsage
            .SingleOrDefaultAsync(
                candidate => candidate.TenantId == tenantKey,
                cancellationToken);
        usage ??= CreateUsageFromReservation(row);
        if (_context.Entry(usage).State == EntityState.Detached)
        {
            _context.QuotaUsage.Add(usage);
        }

        SubtractReserved(usage, row);
        usage.CommittedBytes = checked(usage.CommittedBytes + row.ReservedBytes);
        usage.CommittedObjects =
            checked(usage.CommittedObjects + row.ReservedObjects);
        usage.CommittedComputeUnits =
            checked(usage.CommittedComputeUnits + row.ReservedComputeUnits);
        usage.CommittedJobs = checked(usage.CommittedJobs + row.ReservedJobs);
        usage.CommittedBudgetUnits =
            checked(usage.CommittedBudgetUnits + row.ReservedBudgetUnits);
        usage.Version = checked(usage.Version + 1);

        long expectedVersion = row.Version;
        row.State = "Consumed";
        row.ConsumedByOperationId = operationId;
        row.UpdatedAtUtc = consumedAtUtc;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        return AssetIngestReservationConsumeResult.Consumed(
            ToIngestReservation(row));
    }

    public ValueTask AppendAuditAsync(
        AuditRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        _context.AuditEvents.Add(new AuditEventRow
        {
            Id = record.Id.Value,
            TenantId = record.TenantId.Value,
            ActorKind = record.Actor.Kind.ToString(),
            ActorIdentifier = record.Actor.Identifier,
            Action = record.Action,
            ResourceType = record.Resource.Type,
            ResourceIdentifier = record.Resource.Identifier,
            BeforeJson = JsonSerializer.Serialize(record.Before.Fields, JsonOptions),
            AfterJson = JsonSerializer.Serialize(record.After.Fields, JsonOptions),
            Outcome = record.Outcome.ToString(),
            OccurredAtUtc = record.OccurredAtUtc,
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask AddJobAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        _context.Jobs.Add(JobMapper.ToRow(job));
        return ValueTask.CompletedTask;
    }

    public ValueTask<EventSequence> ReserveEventSequenceAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId != _context.TenantId)
        {
            throw new InvalidOperationException(
                "The event sequence tenant does not match the transaction.");
        }

        return _outbox.ReserveSequenceAsync(cancellationToken);
    }

    public ValueTask AppendOutboxAsync(
        OutboxMessage message,
        CancellationToken cancellationToken) =>
        _outbox.AppendAsync(message, cancellationToken);

    public async ValueTask MarkUploadActivatedAsync(
        AssetIngestActivation activation,
        CancellationToken cancellationToken)
    {
        TenantKey tenantId = activation.TenantId;
        UploadSessionRow row = await _context.UploadSessions
            .SingleAsync(
                candidate =>
                    candidate.TenantId == tenantId &&
                    candidate.Id == activation.UploadSessionId,
                cancellationToken);
        if (row.State == "Accepted" &&
            row.IngestOperationId == activation.OperationId &&
            row.ActivatedAssetId == activation.AssetId &&
            row.ActivatedRevisionId == activation.RevisionId &&
            row.ActivatedBlobId == activation.BlobId)
        {
            return;
        }

        if (row.Version != activation.ExpectedUploadVersion ||
            row.IngestOperationId != activation.OperationId ||
            row.State is not ("Verifying" or "OutcomeUnknown" or "Reconciling"))
        {
            throw new DbUpdateConcurrencyException(
                "The upload activation fence is no longer current.");
        }

        long expectedVersion = row.Version;
        row.State = "Accepted";
        row.ActivatedAssetId = activation.AssetId;
        row.ActivatedRevisionId = activation.RevisionId;
        row.ActivatedBlobId = activation.BlobId;
        row.UpdatedAtUtc = activation.ActivatedAtUtc;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
    }

    public async ValueTask RecordOperationAsync(
        Guid tenantId,
        Guid operationId,
        AssetIngestReceipt receipt,
        CancellationToken cancellationToken)
    {
        TenantKey tenantKey = tenantId;
        IngestOperationRow? row = _context.IngestOperations.Local.SingleOrDefault(
            candidate =>
                candidate.TenantId == tenantKey &&
                candidate.OperationId == operationId);
        row ??= await _context.IngestOperations.SingleOrDefaultAsync(
            candidate =>
                candidate.TenantId == tenantKey &&
                candidate.OperationId == operationId,
            cancellationToken);
        if (row is null)
        {
            row = new IngestOperationRow
            {
                TenantId = tenantId,
                OperationId = operationId,
                UploadSessionId = receipt.UploadSessionId,
                FencedUploadVersion = 1,
                State = "Fenced",
                CreatedAtUtc = receipt.ActivatedAtUtc,
                UpdatedAtUtc = receipt.ActivatedAtUtc,
                Version = 1,
            };
            _context.IngestOperations.Add(row);
        }

        row.AssetId = receipt.AssetId;
        row.RevisionId = receipt.RevisionId;
        row.BlobId = receipt.BlobId;
        row.BlobReused = receipt.BlobReused;
        row.ActivatedAtUtc = receipt.ActivatedAtUtc;
        row.State = "Activated";
        row.UpdatedAtUtc = receipt.ActivatedAtUtc;
        row.Version = checked(row.Version + 1);
    }

    internal void ApplyCurrentRevisionLinks()
    {
        foreach ((AssetRow row, Guid revisionId) in _currentRevisionLinks)
        {
            row.CurrentRevisionId = revisionId;
        }
    }

    internal static async ValueTask<AssetIngestReceipt?> FindReceiptAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        TenantKey tenantKey = tenantId;
        IngestOperationRow? row = await context.IngestOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.TenantId == tenantKey &&
                    candidate.OperationId == operationId &&
                    (candidate.State == "Activated" ||
                     candidate.State == "CleanupCompleted"),
                cancellationToken);
        return row is null ||
            row.AssetId is null ||
            row.RevisionId is null ||
            row.BlobId is null ||
            row.BlobReused is null ||
            row.ActivatedAtUtc is null
                ? null
                : new AssetIngestReceipt(
                    tenantId,
                    operationId,
                    row.UploadSessionId,
                    row.AssetId.Value,
                    row.RevisionId.Value,
                    row.BlobId.Value,
                    row.BlobReused.Value,
                    row.ActivatedAtUtc.Value);
    }

    private static AssetIngestReservation ToIngestReservation(
        QuotaReservationRow row)
    {
        var reservation = AssetIngestReservation.Reserved(
            row.TenantId,
            row.Id,
            row.State == "Consumed" ? row.Version - 1 : row.Version,
            row.ExpiresAtUtc);
        return row.State switch
        {
            "Reserved" => reservation,
            "Consumed" when row.ConsumedByOperationId.HasValue =>
                reservation.Consume(
                    row.ConsumedByOperationId.Value,
                    row.UpdatedAtUtc),
            _ => reservation,
        };
    }

    private static QuotaUsageRow CreateUsageFromReservation(
        QuotaReservationRow reservation) =>
        new()
        {
            TenantId = reservation.TenantId,
            ReservedUploads = reservation.ReservedUploads,
            ReservedBytes = reservation.ReservedBytes,
            ReservedObjects = reservation.ReservedObjects,
            ReservedComputeUnits = reservation.ReservedComputeUnits,
            ReservedJobs = reservation.ReservedJobs,
            ReservedBudgetUnits = reservation.ReservedBudgetUnits,
            Version = 0,
        };

    private static void SubtractReserved(
        QuotaUsageRow usage,
        QuotaReservationRow reservation)
    {
        usage.ReservedUploads =
            checked(usage.ReservedUploads - reservation.ReservedUploads);
        usage.ReservedBytes =
            checked(usage.ReservedBytes - reservation.ReservedBytes);
        usage.ReservedObjects =
            checked(usage.ReservedObjects - reservation.ReservedObjects);
        usage.ReservedComputeUnits =
            checked(usage.ReservedComputeUnits - reservation.ReservedComputeUnits);
        usage.ReservedJobs =
            checked(usage.ReservedJobs - reservation.ReservedJobs);
        usage.ReservedBudgetUnits =
            checked(usage.ReservedBudgetUnits - reservation.ReservedBudgetUnits);
    }
}
