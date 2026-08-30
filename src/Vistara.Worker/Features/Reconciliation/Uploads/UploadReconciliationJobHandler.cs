using System.Text.Json;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Reconciliation.Uploads;

public sealed class UploadReconciliationJobHandler(
    UploadReconciliationService service) : IJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly UploadReconciliationService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType { get; } = new("upload.reconcile");

    public JobType JobType => SupportedJobType;

    public async ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.PayloadVersion != 1 ||
            job.Type.Value != SupportedJobType.Value ||
            !TryReadPayload(job.Payload, out Payload payload))
        {
            return JobHandlerResult.Failed(
                new JobFailure(JobFailureReason.ProcessingFailed));
        }

        _ = await _service.RunAsync(
            new UploadReconciliationRunRequest(
                job.TenantId.Value,
                job.Id.Value,
                payload.Cursor,
                payload.DryRun),
            cancellationToken);
        return JobHandlerResult.Success();
    }

    private static bool TryReadPayload(string json, out Payload payload)
    {
        try
        {
            Payload? parsed = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
            if (parsed is null)
            {
                payload = null!;
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException)
        {
            payload = null!;
            return false;
        }
    }

    private sealed record Payload(string? Cursor, bool DryRun);
}
