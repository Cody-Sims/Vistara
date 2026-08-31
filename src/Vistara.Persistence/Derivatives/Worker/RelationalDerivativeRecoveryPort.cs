using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Derivatives.Worker;

/// <summary>
/// Recovers derivative generation whose durable job was dead-lettered. The
/// generation payload only exists on the job row, so recovery revives that row
/// instead of rebuilding a descriptor the derivative request cannot reproduce.
/// </summary>
public sealed class RelationalDerivativeRecoveryPort(
    VistaraDbContext context) : IDerivativeRecoveryPort
{
    private const string DeadLettered = nameof(JobState.DeadLettered);
    private const string QueuedRequest = "Queued";
    private const string ProcessingRequest = "Processing";
    private const string FailedRequest = "Failed";
    private const string RecoveryExhausted = "recovery_exhausted";

    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<IReadOnlyList<StalledDerivativeRequest>>
        ListStalledAsync(
            Guid tenantId,
            DateTimeOffset stalledBeforeUtc,
            int batchSize,
            CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        EstablishTenant(tenantId);
        var rows = await (
                from request in _context.Set<DerivativeRequestRow>().AsNoTracking()
                join job in _context.Jobs.AsNoTracking()
                    on request.JobId equals job.Id
                where (request.State == QueuedRequest ||
                        request.State == ProcessingRequest) &&
                    request.UpdatedAtUtc <= stalledBeforeUtc &&
                    job.State == DeadLettered
                orderby request.UpdatedAtUtc, request.Id
                select new
                {
                    RequestId = request.Id,
                    JobId = job.Id,
                    job.Version,
                    job.MaxAttempts,
                    job.FailureCode,
                    request.UpdatedAtUtc,
                })
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
        return
        [
            .. rows.Select(row => new StalledDerivativeRequest(
                row.RequestId,
                row.JobId,
                row.Version,
                row.MaxAttempts,
                row.FailureCode,
                row.UpdatedAtUtc)),
        ];
    }

    public async ValueTask<DerivativeRecoveryOutcome> RecoverAsync(
        Guid tenantId,
        StalledDerivativeRequest candidate,
        DerivativeRecoveryBudget budget,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(budget);
        EstablishTenant(tenantId);
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        try
        {
            JobRow? row = await _context.Jobs.SingleOrDefaultAsync(
                job => job.Id == candidate.JobId,
                cancellationToken);
            DerivativeRequestRow? request = await _context
                .Set<DerivativeRequestRow>()
                .SingleOrDefaultAsync(
                    entry => entry.Id == candidate.RequestId,
                    cancellationToken);
            if (row is null ||
                request is null ||
                row.Version != candidate.JobVersion ||
                row.State != DeadLettered ||
                request.State is not (QueuedRequest or ProcessingRequest))
            {
                await transaction.RollbackAsync(cancellationToken);
                return DerivativeRecoveryOutcome.Stale;
            }

            Result<DurableJob> restored = JobMapper.ToDomain(row);
            if (!restored.TryGetValue(out DurableJob? job))
            {
                await transaction.RollbackAsync(cancellationToken);
                return DerivativeRecoveryOutcome.Stale;
            }

            Result granted = job.GrantRecoveryAttempts(
                budget.AdditionalAttempts,
                budget.MaximumAttempts,
                nowUtc);
            if (granted.IsFailure)
            {
                request.State = FailedRequest;
                request.FailureCode = row.FailureCode ?? RecoveryExhausted;
                request.UpdatedAtUtc = nowUtc;
                request.Version++;
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return DerivativeRecoveryOutcome.Exhausted;
            }

            JobMapper.Copy(job, row);
            row.FailureCode = null;
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DerivativeRecoveryOutcome.Requeued;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            return DerivativeRecoveryOutcome.Stale;
        }
    }

    private void EstablishTenant(Guid tenantId) =>
        _context.EstablishTenant(tenantId);
}
