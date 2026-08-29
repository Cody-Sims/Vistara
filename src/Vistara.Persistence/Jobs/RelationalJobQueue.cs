using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Derivatives;
using Vistara.Application.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Persistence.Jobs;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Queue is the domain term used by the application port.")]
public sealed class RelationalJobQueue : IJobQueue
{
    private const string UploadIngestJobType = "upload.ingest";
    private const string DerivativeGenerationJobType = "asset.derivative.generate";
    private const int ReservedJobsPerUpload = 5;

    private static readonly JobRetryPolicy ExpiredLeaseRecoveryPolicy =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly JobDbContext _context;
    private readonly bool _isSqlite;
    private readonly bool _isPostgreSql;

    public RelationalJobQueue(JobDbContext context, JobQueueOptions options)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(options);
        if (options.ConfiguredWorkerCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Configured worker count must be positive.");
        }

        string? provider = context.Database.ProviderName;
        _isSqlite = provider == "Microsoft.EntityFrameworkCore.Sqlite";
        _isPostgreSql = provider == "Npgsql.EntityFrameworkCore.PostgreSQL";
        if (!_isSqlite && !_isPostgreSql)
        {
            throw new InvalidOperationException("The job queue requires SQLite or PostgreSQL.");
        }

        if (_isSqlite && options.ConfiguredWorkerCount != 1)
        {
            throw new InvalidOperationException(
                "SQLite job execution supports a single worker only.");
        }
    }

    public async ValueTask<Result<JobEnqueueResult>> EnqueueAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        _context.Jobs.Add(JobMapper.ToRow(job));
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return Result.Success(new JobEnqueueResult(job.Id, true));
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            JobRow? existing = await _context.Jobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row =>
                        row.TenantId == job.TenantId.Value &&
                        row.DedupeKey == job.DedupeKey.Value,
                    cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return Result.Success(new JobEnqueueResult(new JobId(existing.Id), false));
        }
    }

    public async ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseAsync(
        JobLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum claim count must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _isSqlite
            ? await LeaseSqliteAsync(request, cancellationToken)
            : await LeasePostgreSqlAsync(request, cancellationToken);
    }

    public async ValueTask<Result<JobLease>> HeartbeatAsync(
        JobHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await MutateAsync(
            request.JobId,
            request.ExpectedVersion,
            job => job.Heartbeat(request.Owner, request.NowUtc, request.LeaseDuration),
            job => job.Lease!,
            row => row.LeaseHeartbeatAtUtc = request.NowUtc,
            cancellationToken);
    }

    public async ValueTask<Result> CompleteAsync(
        JobCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using IDbContextTransaction transaction =
            await _context.Database.BeginTransactionAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        JobRow? row = await FindAsync(request.JobId, cancellationToken);
        if (row is null)
        {
            return Result.Failure(NotFound(request.JobId));
        }

        Result<DurableJob> restored = JobMapper.ToDomain(row);
        if (!restored.TryGetValue(out DurableJob? job))
        {
            return Result.Failure(restored.Error!);
        }

        if (job.State == JobState.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }

        if (job.Version != request.ExpectedVersion)
        {
            return Result.Failure(JobErrors.LeaseConflict);
        }

        Result transition = job.Complete(request.Owner, request.CompletedAtUtc);
        if (transition.IsFailure)
        {
            return transition;
        }

        JobMapper.Copy(job, row);
        Result saved = await SaveMutationAsync(
            row,
            request.ExpectedVersion,
            cancellationToken);
        if (saved.IsFailure)
        {
            return saved;
        }

        await ReleaseCommittedCapacityAsync(job, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    public async ValueTask<Result> FailAsync(
        JobFailureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await MutateAsync(
            request.JobId,
            request.ExpectedVersion,
            job => job.Fail(
                request.Owner,
                request.Failure,
                request.FailedAtUtc,
                request.RetryPolicy),
            cancellationToken);
    }

    public async ValueTask<Result> RecoverExpiredAsync(
        JobExpiredLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await MutateAsync(
            request.JobId,
            request.ExpectedVersion,
            job => job.RecoverExpiredLease(
                request.Failure,
                request.RecoveredAtUtc,
                request.RetryPolicy),
            cancellationToken);
    }

    private async ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseSqliteAsync(
        JobLeaseRequest request,
        CancellationToken cancellationToken)
    {
        await _context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)_context.Database.GetDbConnection();
        await using SqliteTransaction transaction =
            connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await _context.Database.UseTransactionAsync(transaction, cancellationToken);
        try
        {
            Result<IReadOnlyList<JobLeaseAssignment>> result =
                await LeaseLockedRowsAsync(
                    _context.Jobs
                        .Where(row =>
                            ((row.State == nameof(JobState.Pending) ||
                              row.State == nameof(JobState.RetryScheduled)) &&
                             row.AvailableAtUtc <= request.NowUtc &&
                             row.Attempts < row.MaxAttempts) ||
                            (row.State == nameof(JobState.Leased) &&
                             row.LeaseExpiresAtUtc <= request.NowUtc))
                        .OrderByDescending(row => row.Priority)
                        .ThenBy(row => row.AvailableAtUtc)
                        .ThenBy(row => row.CreatedAtUtc)
                        .ThenBy(row => row.Id)
                        .Take(request.MaximumCount),
                    request,
                    cancellationToken);
            if (result.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            await _context.Database.UseTransactionAsync(null, CancellationToken.None);
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeasePostgreSqlAsync(
        JobLeaseRequest request,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await _context.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        IQueryable<JobRow> query = _context.Jobs.FromSqlRaw(
            PostgreSqlJobClaimSql.Statement,
            request.NowUtc.UtcDateTime,
            request.MaximumCount);
        Result<IReadOnlyList<JobLeaseAssignment>> result =
            await LeaseLockedRowsAsync(query, request, cancellationToken);
        if (result.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseLockedRowsAsync(
        IQueryable<JobRow> query,
        JobLeaseRequest request,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        JobRow[] rows = await query.ToArrayAsync(cancellationToken);
        var assignments = new List<JobLeaseAssignment>(rows.Length);
        var terminal = new List<DurableJob>();
        foreach (JobRow row in rows)
        {
            Result<DurableJob> restored = JobMapper.ToDomain(row);
            if (!restored.TryGetValue(out DurableJob? job))
            {
                return Result.Failure<IReadOnlyList<JobLeaseAssignment>>(restored.Error!);
            }

            if (job.State == JobState.Leased &&
                job.Lease is not null &&
                request.NowUtc >= job.Lease.ExpiresAtUtc &&
                job.Attempts >= job.MaxAttempts)
            {
                Result recovered = job.RecoverExpiredLease(
                    new JobFailure(JobFailureReason.LeaseExpired),
                    request.NowUtc,
                    ExpiredLeaseRecoveryPolicy);
                if (recovered.IsFailure)
                {
                    return Result.Failure<IReadOnlyList<JobLeaseAssignment>>(
                        recovered.Error!);
                }

                JobMapper.Copy(job, row);
                terminal.Add(job);
                continue;
            }

            Result<JobLease> lease = job.TryLease(
                request.Owner,
                request.NowUtc,
                request.LeaseDuration);
            if (!lease.TryGetValue(out JobLease? value))
            {
                return Result.Failure<IReadOnlyList<JobLeaseAssignment>>(lease.Error!);
            }

            JobMapper.Copy(job, row);
            row.LeaseHeartbeatAtUtc = request.NowUtc;
            assignments.Add(new JobLeaseAssignment(job, value));
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            foreach (DurableJob job in terminal)
            {
                await ReleaseCommittedCapacityAsync(job, cancellationToken);
            }

            return Result.Success<IReadOnlyList<JobLeaseAssignment>>(assignments);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<IReadOnlyList<JobLeaseAssignment>>(JobErrors.LeaseConflict);
        }
    }

    private async ValueTask<Result> MutateAsync(
        JobId jobId,
        JobVersion expectedVersion,
        Func<DurableJob, Result> transition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using IDbContextTransaction transaction =
            await _context.Database.BeginTransactionAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        JobRow? row = await FindAsync(jobId, cancellationToken);
        if (row is null)
        {
            return Result.Failure(NotFound(jobId));
        }

        Result<DurableJob> restored = JobMapper.ToDomain(row);
        if (!restored.TryGetValue(out DurableJob? job))
        {
            return Result.Failure(restored.Error!);
        }

        if (job.Version != expectedVersion)
        {
            return Result.Failure(JobErrors.LeaseConflict);
        }

        Result result = transition(job);
        if (result.IsFailure)
        {
            return result;
        }

        JobMapper.Copy(job, row);
        Result saved = await SaveMutationAsync(
            row,
            expectedVersion,
            cancellationToken);
        if (saved.IsFailure)
        {
            return saved;
        }

        if (job.State == JobState.DeadLettered)
        {
            await ReleaseCommittedCapacityAsync(job, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    private async ValueTask<Result<T>> MutateAsync<T>(
        JobId jobId,
        JobVersion expectedVersion,
        Func<DurableJob, Result> transition,
        Func<DurableJob, T> value,
        Action<JobRow>? afterCopy,
        CancellationToken cancellationToken)
        where T : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        JobRow? row = await FindAsync(jobId, cancellationToken);
        if (row is null)
        {
            return Result.Failure<T>(NotFound(jobId));
        }

        Result<DurableJob> restored = JobMapper.ToDomain(row);
        if (!restored.TryGetValue(out DurableJob? job))
        {
            return Result.Failure<T>(restored.Error!);
        }

        if (job.Version != expectedVersion)
        {
            return Result.Failure<T>(JobErrors.LeaseConflict);
        }

        Result result = transition(job);
        if (result.IsFailure)
        {
            return Result.Failure<T>(result.Error!);
        }

        T captured = value(job);
        JobMapper.Copy(job, row);
        afterCopy?.Invoke(row);
        Result saved = await SaveMutationAsync(row, expectedVersion, cancellationToken);
        return saved.IsSuccess
            ? Result.Success(captured)
            : Result.Failure<T>(saved.Error!);
    }

    private Task<JobRow?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _context.Jobs.SingleOrDefaultAsync(row => row.Id == id.Value, cancellationToken);

    private async ValueTask<Result> SaveMutationAsync(
        JobRow row,
        JobVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        _context.Entry(row).Property(item => item.Version).OriginalValue = expectedVersion.Value;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(JobErrors.LeaseConflict);
        }
    }

    private static ResultError NotFound(JobId id) =>
        ResultError.NotFound(
            "jobs.not_found",
            $"Job '{id.Value}' was not found.");

    private async ValueTask ReleaseCommittedCapacityAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        // The unique canonical job row is the receipt; this runs only in its
        // first terminal-state transaction.
        if (job.Type.Value == UploadIngestJobType)
        {
            if (!TryReadUploadSessionId(job, out Guid uploadSessionId))
            {
                return;
            }

            _ = await _context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE quota_usage
                 SET committed_jobs = committed_jobs - 1,
                     version = version + 1
                 WHERE tenant_id = {job.TenantId.Value}
                   AND committed_jobs > 0
                   AND EXISTS (
                       SELECT 1
                       FROM quota_reservations AS reservation
                       WHERE reservation.tenant_id = quota_usage.tenant_id
                         AND reservation.upload_session_id = {uploadSessionId}
                         AND reservation.state = 'Consumed'
                         AND reservation.reserved_jobs = {ReservedJobsPerUpload}
                   )
                 """,
                cancellationToken);
            return;
        }

        if (job.Type.Value != DerivativeGenerationJobType ||
            !DerivativeJobContract.TryParse(
                job.Type,
                job.PayloadVersion,
                job.Payload,
                out DerivativeJobPayloadV1? payload) ||
            payload is null ||
            !IsStandardDerivativePreset(payload.Preset) ||
            job.DedupeKey != DerivativeJobContract.CreateDedupeKey(payload))
        {
            return;
        }

        _ = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE quota_usage
             SET committed_jobs = committed_jobs - 1,
                 version = version + 1
             WHERE tenant_id = {job.TenantId.Value}
               AND committed_jobs > 0
               AND EXISTS (
                   SELECT 1
                   FROM upload_sessions AS upload
                   INNER JOIN quota_reservations AS reservation
                       ON reservation.tenant_id = upload.tenant_id
                      AND reservation.upload_session_id = upload.id
                   WHERE upload.tenant_id = quota_usage.tenant_id
                     AND upload.activated_revision_id = {payload.RevisionId}
                     AND reservation.state = 'Consumed'
                     AND reservation.reserved_jobs = {ReservedJobsPerUpload}
               )
             """,
            cancellationToken);
    }

    private static bool TryReadUploadSessionId(
        DurableJob job,
        out Guid uploadSessionId)
    {
        uploadSessionId = default;
        if (job.PayloadVersion != 1)
        {
            return false;
        }

        try
        {
            UploadIngestCapacityPayload? payload =
                JsonSerializer.Deserialize<UploadIngestCapacityPayload>(
                    job.Payload,
                    JsonOptions);
            if (payload is null ||
                payload.UploadSessionId == Guid.Empty ||
                payload.UploadSessionId.Version != 7 ||
                job.DedupeKey.Value !=
                    $"upload:{payload.UploadSessionId:D}:ingest:v1")
            {
                return false;
            }

            uploadSessionId = payload.UploadSessionId;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStandardDerivativePreset(string preset) =>
        preset is "thumb" or "grid" or "viewer" or "download-web";

    private sealed record UploadIngestCapacityPayload(Guid UploadSessionId);
}
