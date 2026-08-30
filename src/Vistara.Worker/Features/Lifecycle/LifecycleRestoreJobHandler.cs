using Vistara.Application.Lifecycle;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Lifecycle;

public sealed class LifecycleRestoreJobHandler(
    LifecycleRestoreService service) : IJobHandler
{
    private readonly LifecycleRestoreService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType => LifecycleJobContracts.RestoreType;

    public JobType JobType => SupportedJobType;

    public ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!LifecycleJobContracts.TryParseRestore(
                job.Type,
                job.PayloadVersion,
                job.Payload,
                out LifecycleRestoreJobPayload? payload))
        {
            return ValueTask.FromResult(
                JobHandlerResult.Failed(
                    new JobFailure(JobFailureReason.ProcessingFailed)));
        }

        return _service.ProcessAsync(payload!, cancellationToken);
    }
}
