using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Application.Uploads.Quotas;
using Vistara.Domain.Jobs;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Uploads;

public sealed record PersistedUploadReconciliationCandidate(
    Guid TenantId,
    Guid UploadSessionId,
    long Version,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc,
    string State,
    string? LastKnownState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string StagingKey,
    string? StagingProviderVersion,
    string? CanonicalKey,
    long ExpectedSizeBytes,
    string ExpectedSha256,
    string DeclaredContentType,
    string? MultipartIssuanceId,
    TimeSpan? MultipartPartPlanLifetime,
    bool CanonicalRequiresUploadOwnership,
    MultipartSession? MultipartSession,
    IReadOnlyList<UploadedPart> CompletionParts,
    bool ReservationReleased,
    string ContinuationCursor);

public enum PersistedUploadReconciliationMutationStatus
{
    Applied,
    AlreadyApplied,
    Stale,
}

public sealed record PersistedUploadReconciliationMutation(
    PersistedUploadReconciliationMutationStatus Status,
    PersistedUploadReconciliationCandidate? Current,
    bool ReservationReleased);

public sealed record PersistedUploadReconciliationPage(
    IReadOnlyList<PersistedUploadReconciliationCandidate> Candidates,
    string? ContinuationCursor);

public sealed class RelationalUploadReconciliationStore(
    VistaraDbContext context,
    IUuid7Generator idGenerator,
    UploadPersistenceOptions? options = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
    private readonly UploadPersistenceOptions _options =
        options ?? new UploadPersistenceOptions();

    public async ValueTask<string?> LoadCheckpointAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                tenantId,
                cancellationToken);
        string? cursor = await _context.UploadReconciliationCheckpoints
            .AsNoTracking()
            .Where(row => row.RunId == runId)
            .Select(row => row.Cursor)
            .SingleOrDefaultAsync(cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return cursor;
    }

    public ValueTask<PersistedUploadReconciliationPage> ScanAsync(
        Guid tenantId,
        string? cursor,
        int maximumSessions,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        bool dryRun,
        CancellationToken cancellationToken) =>
        ScanAsync(
            tenantId,
            cursor,
            _idGenerator.NewId(),
            maximumSessions,
            utcNow,
            utcNow,
            leaseDuration,
            dryRun,
            cancellationToken);

    public async ValueTask<PersistedUploadReconciliationPage> ScanAsync(
        Guid tenantId,
        string? cursor,
        Guid runId,
        int maximumSessions,
        DateTimeOffset utcNow,
        DateTimeOffset recoveryCutoffUtc,
        TimeSpan leaseDuration,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSessions);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            leaseDuration,
            TimeSpan.Zero);
        string leasePrefix = $"upload-reconcile:{runId:N}:";
        ReconciliationCursor? position = ParseCursor(cursor);
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                tenantId,
                cancellationToken);
        IQueryable<UploadSessionRow> query = _context.UploadSessions
            .Where(row =>
                ((row.State == "Pending" ||
                  row.State == "UploadIssued") &&
                 row.ExpiresAtUtc <= utcNow) ||
                ((row.State == "Committing" ||
                  row.State == "Aborting" ||
                  row.State == "OutcomeUnknown" ||
                  row.State == "CommitRequested" ||
                  row.State == "Verifying" ||
                  row.State == "Promoting" ||
                  row.State == "Reconciling") &&
                 row.UpdatedAtUtc <= recoveryCutoffUtc) ||
                ((row.State == "Expired" ||
                  row.State == "Aborted" ||
                  row.State == "Accepted" ||
                  row.State == "Rejected") &&
                 row.CleanupCompletedAtUtc == null));
        if (!dryRun)
        {
            query = query.Where(row =>
                row.ReconciliationLeaseExpiresAtUtc == null ||
                row.ReconciliationLeaseExpiresAtUtc <= utcNow ||
                (row.ReconciliationLeaseToken != null &&
                 row.ReconciliationLeaseToken.StartsWith(leasePrefix)));
        }

        if (position is not null)
        {
            ReconciliationCursor value = position.Value;
            query = query.Where(row =>
                row.CreatedAtUtc > value.CreatedAtUtc ||
                (row.CreatedAtUtc == value.CreatedAtUtc &&
                 row.Id.CompareTo(value.UploadSessionId) > 0));
        }

        UploadSessionRow[] rows = await query
            .OrderBy(row => row.CreatedAtUtc)
            .ThenBy(row => row.Id)
            .Take(maximumSessions)
            .ToArrayAsync(cancellationToken);
        if (!dryRun)
        {
            foreach (UploadSessionRow row in rows)
            {
                if (row.ReconciliationLeaseExpiresAtUtc > utcNow &&
                    row.ReconciliationLeaseToken is { } existingToken &&
                    existingToken.StartsWith(
                        leasePrefix,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                row.ReconciliationLeaseToken =
                    $"{leasePrefix}{_idGenerator.NewId():N}";
                row.ReconciliationLeaseExpiresAtUtc = utcNow + leaseDuration;
                row.Version = checked(row.Version + 1);
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return new([], cursor);
            }
        }

        var candidates =
            new List<PersistedUploadReconciliationCandidate>(rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            candidates.Add(await ToCandidateAsync(
                rows[index],
                dryRun ? "dry-run" : rows[index].ReconciliationLeaseToken!,
                dryRun ? utcNow + leaseDuration :
                    rows[index].ReconciliationLeaseExpiresAtUtc!.Value,
                FormatCursor(rows[index].CreatedAtUtc, rows[index].Id),
                cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            candidates.AsReadOnly(),
            candidates.Count == 0
                ? cursor
                : candidates[^1].ContinuationCursor);
    }

    public async ValueTask<PersistedUploadReconciliationCandidate?> RevalidateAsync(
        Guid tenantId,
        Guid uploadSessionId,
        long version,
        string leaseToken,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                tenantId,
                cancellationToken);
        UploadSessionRow? row = await _context.UploadSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == uploadSessionId,
                cancellationToken);
        bool dryRun = string.Equals(
            leaseToken,
            "dry-run",
            StringComparison.Ordinal);
        if (row is null ||
            row.Version != version ||
            leaseExpiresAtUtc <= utcNow ||
            (!dryRun &&
             (!string.Equals(
                  row.ReconciliationLeaseToken,
                  leaseToken,
                  StringComparison.Ordinal) ||
              row.ReconciliationLeaseExpiresAtUtc != leaseExpiresAtUtc)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        PersistedUploadReconciliationCandidate candidate =
            await ToCandidateAsync(
                row,
                leaseToken,
                leaseExpiresAtUtc,
                FormatCursor(row.CreatedAtUtc, row.Id),
                cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return candidate;
    }

    public ValueTask<PersistedUploadReconciliationMutation> ExpireAsync(
        PersistedUploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            candidate,
            utcNow,
            targetState: "Expired",
            releaseReservation: false,
            cancellationToken);

    public ValueTask<PersistedUploadReconciliationMutation> PrepareAbortAsync(
        PersistedUploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            candidate,
            utcNow,
            targetState: "Aborting",
            releaseReservation: false,
            cancellationToken);

    public async ValueTask<PersistedUploadReconciliationMutation>
        RecordMultipartIssuedForAbortAsync(
            PersistedUploadReconciliationCandidate candidate,
            MultipartSession session,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                candidate.TenantId,
                cancellationToken);
        UploadSessionRow? row = await LoadFencedAsync(
            candidate,
            utcNow,
            cancellationToken);
        if (row is null ||
            row.State is not ("Pending" or "Aborting") ||
            row.ProviderUploadId is not null ||
            MultipartIssuanceId(row) is not { } issuanceId ||
            session.Key.Value != row.StagingKey ||
            session.ContentLength != row.ExpectedBytes ||
            session.ContentType.Value != row.DeclaredContentType ||
            session.CompletionConditions != BlobRequestConditions.CreateOnly ||
            session.PartPlanLifetime.Ticks !=
                row.MultipartPartPlanLifetimeTicks ||
            !HasMetadata(
                session.Metadata,
                "vistara-tenant-id",
                row.TenantId.Value.ToString("D")) ||
            !HasMetadata(
                session.Metadata,
                "vistara-upload-id",
                row.Id.ToString("D")) ||
            !HasMetadata(
                session.Metadata,
                "vistara-multipart-issuance-id",
                issuanceId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        long expectedVersion = row.Version;
        row.LastKnownState = row.State;
        row.State = "Aborting";
        row.ProviderUploadId = session.UploadId;
        row.MultipartProviderState = session.ProviderState;
        row.MultipartExpiresAtUtc = session.ExpiresAtUtc;
        row.MultipartPartPlanLifetimeTicks = session.PartPlanLifetime.Ticks;
        row.MultipartMaxParts = session.MaxParts;
        row.MultipartMinPartBytes = session.MinPartBytes;
        row.MultipartMaxPartBytes = session.MaxPartBytes;
        row.UpdatedAtUtc = utcNow;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.Applied,
                current,
                false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }
    }

    public ValueTask<PersistedUploadReconciliationMutation> CompleteAbortAsync(
            PersistedUploadReconciliationCandidate candidate,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken) =>
        MutateAsync(
            candidate,
            utcNow,
            targetState: "Aborted",
            releaseReservation: false,
            cancellationToken);

    public ValueTask<PersistedUploadReconciliationMutation>
        RecordAbortOutcomeUnknownAsync(
            PersistedUploadReconciliationCandidate candidate,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken) =>
        MutateAsync(
            candidate,
            utcNow,
            targetState: "OutcomeUnknown",
            releaseReservation: false,
            cancellationToken,
            lastKnownState: "Aborting");

    public async ValueTask<PersistedUploadReconciliationMutation>
        CompleteCommitAsync(
            PersistedUploadReconciliationCandidate candidate,
            BlobIdentity stagingIdentity,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagingIdentity);
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                candidate.TenantId,
                cancellationToken);
        UploadSessionRow? row = await LoadFencedAsync(
            candidate,
            utcNow,
            cancellationToken);
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        if (row.State == "CommitRequested")
        {
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.AlreadyApplied,
                current,
                false);
        }

        if ((row.State != "Committing" &&
             !(row.State == "OutcomeUnknown" &&
               row.LastKnownState == "Committing")) ||
            !string.Equals(
                row.StagingKey,
                stagingIdentity.Key.Value,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        long expectedVersion = row.Version;
        row.StagingProviderVersion = stagingIdentity.Version.Value;
        row.StagingEntityTag = stagingIdentity.Version.Value;
        row.StagingProviderChecksum = row.ExpectedSha256;
        row.LastKnownState = row.State;
        row.State = "CommitRequested";
        row.UpdatedAtUtc = utcNow;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        _context.Jobs.Add(JobMapper.ToRow(CreateIngestJob(row, utcNow)));
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.Applied,
                current,
                false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }
    }

    public async ValueTask<PersistedUploadReconciliationMutation>
        CompleteCleanupAsync(
            PersistedUploadReconciliationCandidate candidate,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                candidate.TenantId,
                cancellationToken);
        UploadSessionRow? row = await LoadFencedAsync(
            candidate,
            utcNow,
            cancellationToken);
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        if (row.CleanupCompletedAtUtc.HasValue)
        {
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.AlreadyApplied,
                current,
                false);
        }

        if (row.State is not ("Expired" or "Aborted" or "Accepted" or "Rejected"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        long expectedVersion = row.Version;
        row.CleanupCompletedAtUtc = utcNow;
        row.UpdatedAtUtc = utcNow;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        Ingest.IngestOperationRow? ingest =
            await _context.IngestOperations.SingleOrDefaultAsync(
                item => item.UploadSessionId == row.Id,
                cancellationToken);
        if (ingest is not null && !ingest.CleanupCompletedAtUtc.HasValue)
        {
            if (row.State == "Accepted")
            {
                ingest.State = "CleanupCompleted";
            }

            ingest.CleanupCompletedAtUtc = utcNow;
            ingest.UpdatedAtUtc = utcNow;
            ingest.Version = checked(ingest.Version + 1);
        }

        bool released = row.State != "Accepted" &&
            await ReleaseReservationAsync(row, utcNow, cancellationToken);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.Applied,
                current,
                released);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }
    }

    public async ValueTask<PersistedUploadReconciliationMutation> ResumeIngestAsync(
        PersistedUploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        await RecoverIngestAsync(
            candidate,
            canonicalIdentity: null,
            utcNow,
            cancellationToken);

    public ValueTask<PersistedUploadReconciliationMutation>
        PreserveCanonicalAsync(
        PersistedUploadReconciliationCandidate candidate,
        BlobIdentity canonicalIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        RecoverIngestAsync(
            candidate,
            canonicalIdentity,
            utcNow,
            cancellationToken);

    public ValueTask<PersistedUploadReconciliationMutation> QuarantineAsync(
        PersistedUploadReconciliationCandidate candidate,
        string reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            candidate,
            utcNow,
            targetState: "Rejected",
            releaseReservation: false,
            cancellationToken,
            rejectionCode: reason);

    public async ValueTask SaveCheckpointAsync(
        Guid tenantId,
        Guid runId,
        string? cursor,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                tenantId,
                cancellationToken);
        UploadReconciliationCheckpointRow? row =
            await _context.UploadReconciliationCheckpoints.SingleOrDefaultAsync(
                item => item.RunId == runId,
                cancellationToken);
        if (row is null)
        {
            _context.UploadReconciliationCheckpoints.Add(
                new UploadReconciliationCheckpointRow
                {
                    TenantId = tenantId,
                    RunId = runId,
                    Cursor = cursor,
                    UpdatedAtUtc = utcNow,
                });
        }
        else
        {
            row.Cursor = cursor;
            row.UpdatedAtUtc = utcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async ValueTask<PersistedUploadReconciliationMutation> RecoverIngestAsync(
        PersistedUploadReconciliationCandidate candidate,
        BlobIdentity? canonicalIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                candidate.TenantId,
                cancellationToken);
        UploadSessionRow? row = await LoadFencedAsync(
            candidate,
            utcNow,
            cancellationToken);
        if (row is null ||
            row.State is not ("CommitRequested" or "Verifying" or "Promoting" or
                "OutcomeUnknown" or "Reconciling") ||
            (canonicalIdentity is not null &&
             !string.Equals(
                 candidate.CanonicalKey,
                 canonicalIdentity.Key.Value,
                 StringComparison.Ordinal)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        Ingest.IngestOperationRow? operation =
            await _context.IngestOperations.SingleOrDefaultAsync(
                item => item.UploadSessionId == row.Id,
                cancellationToken);
        if (row.State != "CommitRequested" && operation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        long expectedVersion = row.Version;
        if (row.State != "CommitRequested")
        {
            row.State = "Verifying";
        }

        row.UpdatedAtUtc = utcNow;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        if (operation is not null)
        {
            operation.FencedUploadVersion = row.Version;
            operation.UpdatedAtUtc = utcNow;
            operation.Version = checked(operation.Version + 1);
        }

        QuotaReservationRow reservation =
            await _context.QuotaReservations.SingleAsync(
                item => item.UploadSessionId == row.Id,
                cancellationToken);
        DateTimeOffset requiredExpiry =
            utcNow + _options.OutcomeReconciliationGrace;
        if (reservation.State == "Reserved" &&
            reservation.ExpiresAtUtc < requiredExpiry)
        {
            reservation.ExpiresAtUtc = requiredExpiry;
            reservation.UpdatedAtUtc = utcNow;
            reservation.Version = checked(reservation.Version + 1);
        }

        JobRow? job = await _context.Jobs.SingleOrDefaultAsync(
            item =>
                item.TenantId == candidate.TenantId &&
                item.DedupeKey == $"upload:{row.Id:D}:ingest:v1",
            cancellationToken);
        if (job is null)
        {
            _context.Jobs.Add(JobMapper.ToRow(CreateIngestJob(row, utcNow)));
        }
        else if (job.State is "Completed" or "DeadLettered")
        {
            job.State = "Pending";
            job.Attempts = 0;
            job.AvailableAtUtc = utcNow;
            job.LeaseOwner = null;
            job.LeaseAcquiredAtUtc = null;
            job.LeaseHeartbeatAtUtc = null;
            job.LeaseExpiresAtUtc = null;
            job.FailureCode = null;
            job.CompletedAtUtc = null;
            job.Version = checked(job.Version + 1);
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.Applied,
                current,
                false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }
    }

    private async ValueTask<PersistedUploadReconciliationMutation> MutateAsync(
        PersistedUploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        string targetState,
        bool releaseReservation,
        CancellationToken cancellationToken,
        string? lastKnownState = null,
        string? rejectionCode = null)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await BeginTenantTransactionAsync(
                candidate.TenantId,
                cancellationToken);
        UploadSessionRow? row = await LoadFencedAsync(
            candidate,
            utcNow,
            cancellationToken);
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }

        if (row.State == targetState)
        {
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.AlreadyApplied,
                current,
                false);
        }

        bool released = releaseReservation &&
            await ReleaseReservationAsync(row, utcNow, cancellationToken);
        long expectedVersion = row.Version;
        row.LastKnownState = lastKnownState ?? row.State;
        row.State = targetState;
        row.RejectionCode = rejectionCode ?? row.RejectionCode;
        row.RejectedAtUtc = rejectionCode is null ? row.RejectedAtUtc : utcNow;
        row.UpdatedAtUtc = utcNow;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadReconciliationCandidate current =
                await ToCandidateAsync(
                    row,
                    candidate.LeaseToken,
                    candidate.LeaseExpiresAtUtc,
                    candidate.ContinuationCursor,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PersistedUploadReconciliationMutationStatus.Applied,
                current,
                released);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Stale();
        }
    }

    private async ValueTask<UploadSessionRow?> LoadFencedAsync(
        PersistedUploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        UploadSessionRow? row = await _context.UploadSessions.SingleOrDefaultAsync(
            item => item.Id == candidate.UploadSessionId,
            cancellationToken);
        return row is not null &&
            row.Version == candidate.Version &&
            string.Equals(
                row.ReconciliationLeaseToken,
                candidate.LeaseToken,
                StringComparison.Ordinal) &&
            row.ReconciliationLeaseExpiresAtUtc ==
            candidate.LeaseExpiresAtUtc &&
            candidate.LeaseExpiresAtUtc > utcNow
                ? row
                : null;
    }

    private ValueTask<IDbContextTransaction> BeginTenantTransactionAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        _context.EstablishTenant(tenantId);
        return TenantDatabaseTransaction.BeginAsync(
            _context,
            tenantId,
            cancellationToken);
    }

    private async ValueTask<bool> ReleaseReservationAsync(
        UploadSessionRow upload,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        QuotaReservationRow? reservation =
            await _context.QuotaReservations.SingleOrDefaultAsync(
                row => row.UploadSessionId == upload.Id,
                cancellationToken);
        if (reservation is null ||
            reservation.State is "Released" or "Expired" or "Consumed")
        {
            return false;
        }

        QuotaStoreTransitionResult transition =
            await QuotaPersistence.TransitionTrackedAsync(
                _context,
                new AtomicQuotaTransition(
                    reservation.Id,
                    QuotaReservationState.Released,
                    reservation.Version,
                    utcNow),
                consumedByOperationId: null,
                cancellationToken);
        return transition.Status == QuotaStoreTransitionStatus.Transitioned;
    }

    private async ValueTask<PersistedUploadReconciliationCandidate>
        ToCandidateAsync(
            UploadSessionRow row,
            string leaseToken,
            DateTimeOffset leaseExpiresAtUtc,
            string cursor,
            CancellationToken cancellationToken)
    {
        QuotaReservationRow? reservation =
            await _context.QuotaReservations.AsNoTracking().SingleOrDefaultAsync(
                item => item.UploadSessionId == row.Id,
                cancellationToken);
        Ingest.IngestOperationRow? ingest =
            await _context.IngestOperations.AsNoTracking().SingleOrDefaultAsync(
                item => item.UploadSessionId == row.Id,
                cancellationToken);
        UploadPartRow[] parts = await _context.UploadParts.AsNoTracking()
            .Where(item => item.UploadSessionId == row.Id)
            .OrderBy(item => item.PartNumber)
            .ToArrayAsync(cancellationToken);
        MultipartSession? multipart =
            row.ProviderUploadId is null ? null : RestoreMultipart(row);
        return new PersistedUploadReconciliationCandidate(
            row.TenantId,
            row.Id,
            row.Version,
            leaseToken,
            leaseExpiresAtUtc,
            row.State,
            row.LastKnownState,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.ExpiresAtUtc,
            row.StagingKey,
            row.StagingProviderVersion,
            ingest?.CanonicalKey,
            row.ExpectedBytes,
            row.ExpectedSha256,
            row.DeclaredContentType,
            MultipartIssuanceId(row),
            row.MultipartPartPlanLifetimeTicks.HasValue
                ? TimeSpan.FromTicks(row.MultipartPartPlanLifetimeTicks.Value)
                : null,
            ingest?.PromotionMode != "ExistingExactBlob",
            multipart,
            parts.Select(item => new UploadedPart(
                item.PartNumber,
                new BlobEntityTag(item.EntityTag),
                item.Checksum is null
                    ? null
                    : new BlobChecksum(
                        BlobChecksumAlgorithm.Sha256,
                        item.Checksum),
                item.SizeBytes)).ToArray(),
            reservation is null || reservation.State != "Reserved",
            cursor);
    }

    private static MultipartSession RestoreMultipart(UploadSessionRow row)
    {
        if (row.ProviderUploadId is null ||
            row.MultipartProviderState is null ||
            row.MultipartExpiresAtUtc is null ||
            row.MultipartPartPlanLifetimeTicks is null ||
            row.MultipartMaxParts is null ||
            row.MultipartMinPartBytes is null ||
            row.MultipartMaxPartBytes is null)
        {
            throw new InvalidOperationException(
                "The persisted multipart reconciliation session is incomplete.");
        }

        return new MultipartSession(
            row.ProviderUploadId,
            new BlobKey(row.StagingKey),
            row.MultipartExpiresAtUtc.Value,
            row.ExpectedBytes,
            BlobRequestConditions.CreateOnly,
            row.MultipartMaxParts.Value,
            row.MultipartMinPartBytes.Value,
            row.MultipartMaxPartBytes.Value,
            TimeSpan.FromTicks(row.MultipartPartPlanLifetimeTicks.Value),
            new BlobMediaType(row.DeclaredContentType),
            checksum: null,
            MultipartMetadata(row),
            row.MultipartProviderState);
    }

    private static BlobMetadata MultipartMetadata(UploadSessionRow row)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vistara-tenant-id"] = row.TenantId.Value.ToString("D"),
            ["vistara-upload-id"] = row.Id.ToString("D"),
        };
        if (MultipartIssuanceId(row) is { } issuanceId)
        {
            metadata["vistara-multipart-issuance-id"] =
                issuanceId;
        }

        return new BlobMetadata(metadata);
    }

    private static string? MultipartIssuanceId(UploadSessionRow row)
    {
        const string prefix = "issuance:v1:";
        return row.ProviderUploadId is null &&
            row.MultipartProviderState is { } state &&
            state.StartsWith(prefix, StringComparison.Ordinal) &&
            state.Length > prefix.Length
                ? state[prefix.Length..]
                : null;
    }

    private static bool HasMetadata(
        BlobMetadata metadata,
        string key,
        string expected) =>
        metadata.TryGetValue(key, out string? value) &&
        string.Equals(value, expected, StringComparison.Ordinal);

    private DurableJob CreateIngestJob(
        UploadSessionRow row,
        DateTimeOffset utcNow) =>
        DurableJob.Create(
            new JobId(_idGenerator.NewId()),
            new JobTenantId(row.TenantId),
            new JobType("upload.ingest"),
            JsonSerializer.Serialize(
                new UploadIngestPayload(row.Id),
                JsonOptions),
            payloadVersion: 1,
            new JobDedupeKey($"upload:{row.Id:D}:ingest:v1"),
            priority: 0,
            maxAttempts: 5,
            availableAtUtc: utcNow,
            createdAtUtc: utcNow);

    private static ReconciliationCursor? ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        string[] segments = cursor.Split(':', 2);
        if (segments.Length != 2 ||
            !long.TryParse(
                segments[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long utcTicks) ||
            !Guid.TryParseExact(segments[1], "N", out Guid uploadSessionId) ||
            uploadSessionId == Guid.Empty ||
            uploadSessionId.Version != 7)
        {
            throw new InvalidOperationException(
                "The upload reconciliation cursor is invalid.");
        }

        try
        {
            return new(
                new DateTimeOffset(utcTicks, TimeSpan.Zero),
                uploadSessionId);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidOperationException(
                "The upload reconciliation cursor is invalid.",
                error);
        }
    }

    private static string FormatCursor(
        DateTimeOffset createdAtUtc,
        Guid uploadSessionId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAtUtc.UtcTicks}:{uploadSessionId:N}");

    private static PersistedUploadReconciliationMutation Stale() =>
        new(PersistedUploadReconciliationMutationStatus.Stale, null, false);

    private readonly record struct ReconciliationCursor(
        DateTimeOffset CreatedAtUtc,
        Guid UploadSessionId);

    private sealed record UploadIngestPayload(Guid UploadSessionId);
}
