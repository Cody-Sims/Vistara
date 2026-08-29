using System.Text.Json;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Ingest;

public sealed class IngestJobHandler(IngestService service) : IJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IngestService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType { get; } = new("upload.ingest");

    public JobType JobType => SupportedJobType;

    public async ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.PayloadVersion != 1 ||
            job.Type.Value != SupportedJobType.Value ||
            !TryReadUploadSessionId(job.Payload, out Guid uploadSessionId))
        {
            return JobHandlerResult.Failed(
                new JobFailure(JobFailureReason.ProcessingFailed));
        }

        return await _service.ProcessAsync(
            job.TenantId.Value,
            uploadSessionId,
            cancellationToken);
    }

    private static bool TryReadUploadSessionId(string payload, out Guid uploadSessionId)
    {
        uploadSessionId = default;
        try
        {
            IngestPayload? parsed = JsonSerializer.Deserialize<IngestPayload>(
                payload,
                JsonOptions);
            if (parsed is null ||
                parsed.UploadSessionId == Guid.Empty ||
                parsed.UploadSessionId.Version != 7)
            {
                return false;
            }

            uploadSessionId = parsed.UploadSessionId;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record IngestPayload(Guid UploadSessionId);
}
