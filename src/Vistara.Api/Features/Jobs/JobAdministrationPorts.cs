using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Api.Features.Jobs;

public sealed record JobListQuery(
    Guid TenantId,
    IReadOnlyList<string> States,
    string? Type,
    int Limit,
    DateTimeOffset? AfterCreatedAtUtc,
    Guid? AfterJobId);

public sealed record JobListPage(
    IReadOnlyList<JobSnapshot> Items,
    DateTimeOffset? NextCreatedAtUtc,
    Guid? NextJobId);

/// <summary>
/// Tenant-scoped job administration. Only the actions the durable job model
/// can express are offered; see <see cref="JobActions"/>.
/// </summary>
public interface IJobAdministrationPort
{
    ValueTask<JobListPage> ListAsync(
        JobListQuery query,
        CancellationToken cancellationToken);

    ValueTask<Result<JobSnapshot>> RetryAsync(
        Guid tenantId,
        Guid jobId,
        long expectedVersion,
        CancellationToken cancellationToken);
}

public static class JobActions
{
    /// <summary>States a job may be returned to the queue from.</summary>
    public static bool CanRetry(JobState state) =>
        state is JobState.DeadLettered or JobState.RetryScheduled;

    /// <summary>
    /// The durable job model has no cancelled state, so cancellation is never
    /// offered in this release rather than being faked with a wrong failure.
    /// </summary>
    public static bool CanCancel(JobState state) => false;
}
