using Vistara.Api.Features.Jobs;
using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence.Jobs;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Bridges job administration onto the tenant-scoped job catalog. Only the
/// retry transition the durable job model can express is offered.
/// </summary>
internal sealed class PlatformJobAdministrationAdapter(
    RelationalJobAdministrationStore store,
    IClock clock) : IJobAdministrationPort
{
    public async ValueTask<JobListPage> ListAsync(
        JobListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        JobAdministrationPage page = await store.ListAsync(
            new JobAdministrationQuery(
                query.TenantId,
                query.States,
                query.Type,
                query.Limit,
                query.AfterCreatedAtUtc,
                query.AfterJobId),
            cancellationToken);
        return new JobListPage(page.Items, page.NextCreatedAtUtc, page.NextJobId);
    }

    public async ValueTask<Result<JobSnapshot>> RetryAsync(
        Guid tenantId,
        Guid jobId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        JobRetryStatus status = await store.RetryAsync(
            tenantId,
            jobId,
            expectedVersion,
            clock.UtcNow,
            cancellationToken);
        switch (status)
        {
            case JobRetryStatus.NotFound:
                return Result.Failure<JobSnapshot>(ResultError.NotFound(
                    "jobs.not_found",
                    "The requested job was not found."));
            case JobRetryStatus.VersionConflict:
                return Result.Failure<JobSnapshot>(ResultError.Conflict(
                    "jobs.version_conflict",
                    "The job changed since it was read."));
            case JobRetryStatus.NotRetryable:
                return Result.Failure<JobSnapshot>(ResultError.Conflict(
                    "jobs.not_retryable",
                    "Only a dead-lettered or retry-scheduled job can be requeued."));
            default:
                break;
        }

        JobSnapshot? snapshot =
            await store.FindAsync(tenantId, jobId, cancellationToken);
        return snapshot is null
            ? Result.Failure<JobSnapshot>(ResultError.NotFound(
                "jobs.not_found",
                "The requested job was not found."))
            : Result.Success(snapshot);
    }
}
