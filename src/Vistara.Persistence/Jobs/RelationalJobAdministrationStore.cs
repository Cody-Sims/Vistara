using Microsoft.EntityFrameworkCore;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Persistence.Jobs;

public sealed record JobAdministrationQuery(
    Guid TenantId,
    IReadOnlyList<string> States,
    string? Type,
    int Limit,
    DateTimeOffset? AfterCreatedAtUtc,
    Guid? AfterJobId);

public sealed record JobAdministrationPage(
    IReadOnlyList<JobSnapshot> Items,
    DateTimeOffset? NextCreatedAtUtc,
    Guid? NextJobId);

public enum JobRetryStatus
{
    Retried,
    NotFound,
    VersionConflict,
    NotRetryable,
}

/// <summary>
/// Tenant-scoped job administration reads and the single safe operator action
/// the durable job model can express.
/// </summary>
public sealed class RelationalJobAdministrationStore(VistaraDbContext context)
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<JobAdministrationPage> ListAsync(
        JobAdministrationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        RequireScope(query.TenantId);

        Guid tenantId = query.TenantId;
        IQueryable<JobRow> rows = _context.Jobs
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId);
        if (query.States.Count > 0)
        {
            rows = rows.Where(row => query.States.Contains(row.State));
        }

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            rows = rows.Where(row => row.Type == query.Type);
        }

        if (query.AfterCreatedAtUtc is { } after && query.AfterJobId is { } afterId)
        {
            rows = rows.Where(row =>
                row.CreatedAtUtc < after ||
                (row.CreatedAtUtc == after && row.Id.CompareTo(afterId) < 0));
        }

        JobRow[] page = await rows
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.Id)
            .Take(query.Limit + 1)
            .ToArrayAsync(cancellationToken);
        bool hasMore = page.Length > query.Limit;
        JobRow[] window = hasMore ? page[..query.Limit] : page;
        var snapshots = new List<JobSnapshot>(window.Length);
        foreach (JobRow row in window)
        {
            Result<DurableJob> job = JobMapper.ToDomain(row);
            if (job.TryGetValue(out DurableJob? restored))
            {
                snapshots.Add(restored.ToSnapshot());
            }
        }

        JobRow? last = window.Length == 0 ? null : window[^1];
        return new JobAdministrationPage(
            snapshots,
            hasMore ? last?.CreatedAtUtc : null,
            hasMore ? last?.Id : null);
    }

    /// <summary>
    /// Returns a dead-lettered or retry-scheduled job to the queue. The job
    /// model has no cancelled state, so cancellation is not offered.
    /// </summary>
    public async ValueTask<JobRetryStatus> RetryAsync(
        Guid tenantId,
        Guid jobId,
        long expectedVersion,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireScope(tenantId);

        JobRow? row = await _context.Jobs.SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Id == jobId,
            cancellationToken);
        if (row is null)
        {
            return JobRetryStatus.NotFound;
        }

        if (row.Version != expectedVersion)
        {
            return JobRetryStatus.VersionConflict;
        }

        if (row.State is not ("DeadLettered" or "RetryScheduled"))
        {
            return JobRetryStatus.NotRetryable;
        }

        row.State = nameof(JobState.Pending);
        row.Attempts = 0;
        row.FailureCode = null;
        row.LeaseOwner = null;
        row.LeaseAcquiredAtUtc = null;
        row.LeaseHeartbeatAtUtc = null;
        row.LeaseExpiresAtUtc = null;
        row.CompletedAtUtc = null;
        row.AvailableAtUtc = availableAtUtc;
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(entry => entry.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return JobRetryStatus.Retried;
        }
        catch (DbUpdateException)
        {
            return JobRetryStatus.VersionConflict;
        }
    }

    public async ValueTask<JobSnapshot?> FindAsync(
        Guid tenantId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        RequireScope(tenantId);
        JobRow? row = await _context.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == jobId,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        Result<DurableJob> job = JobMapper.ToDomain(row);
        return job.TryGetValue(out DurableJob? restored) ? restored.ToSnapshot() : null;
    }

    private void RequireScope(Guid tenantId)
    {
        if (_context.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                "The job administration request does not match the active tenant scope.");
        }
    }
}
