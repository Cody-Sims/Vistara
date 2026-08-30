using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Derivatives;

public sealed class DerivativeJobHandler(DerivativeService service) : IJobHandler
{
    private readonly DerivativeService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType => DerivativeJobContract.Type;

    public JobType JobType => SupportedJobType;

    public async ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!DerivativeJobContract.TryParse(
                job.Type,
                job.PayloadVersion,
                job.Payload,
                out DerivativeJobPayloadV1? payload) ||
            payload is null ||
            job.State != JobState.Leased ||
            job.Lease is null ||
            job.DedupeKey != DerivativeJobContract.CreateDedupeKey(payload))
        {
            return Failed(JobFailureReason.ProcessingFailed);
        }

        try
        {
            return await _service.ProcessAsync(
                new DerivativeJobRequest(
                    job.Id.Value,
                    job.TenantId.Value,
                    payload,
                    job.Lease),
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return Failed(JobFailureReason.ProcessingFailed);
        }
    }

    private static JobHandlerResult Failed(JobFailureReason reason) =>
        JobHandlerResult.Failed(new JobFailure(reason));
}
