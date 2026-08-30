using Vistara.Application.Lifecycle;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Lifecycle;

public sealed class LifecyclePurgeJobHandler(
    LifecyclePurgeService service) : IJobHandler
{
    private readonly LifecyclePurgeService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType => LifecycleJobContracts.PurgeType;

    public JobType JobType => SupportedJobType;

    public ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!LifecycleJobContracts.TryParsePurge(
                job.Type,
                job.PayloadVersion,
                job.Payload,
                out LifecyclePurgeJobPayload? payload))
        {
            return ValueTask.FromResult(
                JobHandlerResult.Failed(
                    new JobFailure(JobFailureReason.ProcessingFailed)));
        }

        return _service.ProcessAsync(
            payload!.TenantId,
            payload.BatchId,
            cancellationToken);
    }
}
