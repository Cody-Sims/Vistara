using Vistara.Domain.Jobs;

namespace Vistara.Application.Jobs;

/// <summary>
/// Reads a single durable job, always constrained to the requested tenant so
/// cross-tenant identifiers are indistinguishable from unknown identifiers.
/// </summary>
public interface IJobStatusReader
{
    ValueTask<JobSnapshot?> FindAsync(
        Guid tenantId,
        JobId id,
        CancellationToken cancellationToken);
}
