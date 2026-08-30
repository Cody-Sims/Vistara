using Microsoft.EntityFrameworkCore;
using Vistara.Application.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Persistence.Jobs;

/// <summary>
/// Reads durable job state through the tenant-filtered catalog so row-level
/// security and the tenant query filter both apply to every lookup.
/// </summary>
public sealed class RelationalJobStatusReader(VistaraDbContext context)
    : IJobStatusReader
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<JobSnapshot?> FindAsync(
        Guid tenantId,
        JobId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                "The job lookup does not match the active tenant scope.");
        }

        Guid jobId = id.Value;
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
        return job.TryGetValue(out DurableJob? restored)
            ? restored.ToSnapshot()
            : null;
    }
}
