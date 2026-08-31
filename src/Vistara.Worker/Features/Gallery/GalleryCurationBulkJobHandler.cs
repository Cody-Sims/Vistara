using Vistara.Application.Gallery.Curation;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Gallery;

/// <summary>
/// Consumes the durable bulk curation job produced by
/// <c>POST /api/v1/assets/bulk</c>. The payload is parsed strictly and pinned
/// to the tenant of the claimed job so a job row can never widen its own scope.
/// </summary>
public sealed class GalleryCurationBulkJobHandler(
    GalleryCurationBulkService service) : IJobHandler
{
    private readonly GalleryCurationBulkService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType =>
        GalleryCurationJobContracts.BulkType;

    public JobType JobType => SupportedJobType;

    public ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!GalleryCurationJobContracts.TryParseBulk(
                job.Type,
                job.PayloadVersion,
                job.Payload,
                out GalleryCurationBulkJobPayload? payload) ||
            payload!.TenantId != job.TenantId.Value)
        {
            return ValueTask.FromResult(
                JobHandlerResult.Failed(
                    new JobFailure(JobFailureReason.ProcessingFailed)));
        }

        return _service.ProcessAsync(job.Id.Value, payload, cancellationToken);
    }
}
