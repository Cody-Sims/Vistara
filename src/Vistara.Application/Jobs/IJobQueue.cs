using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Jobs;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Queue is the domain term used by the application port.")]
public interface IJobQueue
{
    /// <summary>
    /// Atomically inserts by tenant-scoped dedupe identity or returns the existing job.
    /// </summary>
    ValueTask<Result<JobEnqueueResult>> EnqueueAsync(
        DurableJob job,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims only jobs that remain available when the persistence transaction locks them.
    /// </summary>
    ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseAsync(
        JobLeaseRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extends the matching lease only when its owner and expected version still match.
    /// </summary>
    ValueTask<Result<JobLease>> HeartbeatAsync(
        JobHeartbeatRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Completes the matching lease with idempotent already-completed handling.
    /// </summary>
    ValueTask<Result> CompleteAsync(
        JobCompletionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a retry or dead-letter transition only for the matching lease and version.
    /// </summary>
    ValueTask<Result> FailAsync(
        JobFailureRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Recovers an expired lease only when the persisted version still matches.
    /// </summary>
    ValueTask<Result> RecoverExpiredAsync(
        JobExpiredLeaseRequest request,
        CancellationToken cancellationToken);
}
