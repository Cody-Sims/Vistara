using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;

namespace Vistara.Worker.Features.Derivatives;

public sealed record DerivativeJobRequest
{
    public DerivativeJobRequest(
        Guid requestId,
        Guid tenantId,
        DerivativeJobPayloadV1 payload,
        JobLease jobLease)
    {
        if (requestId == Guid.Empty || requestId.Version != 7)
        {
            throw new ArgumentException("Request ID must be a UUIDv7.", nameof(requestId));
        }

        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("Tenant ID must be a UUIDv7.", nameof(tenantId));
        }

        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(jobLease);
        if (jobLease.JobId.Value != requestId)
        {
            throw new ArgumentException(
                "The derivative request must use the leased job identity.",
                nameof(jobLease));
        }

        RequestId = requestId;
        TenantId = tenantId;
        Payload = payload;
        JobLease = jobLease;
    }

    public Guid RequestId { get; }

    public Guid TenantId { get; }

    public DerivativeJobPayloadV1 Payload { get; }

    public JobLease JobLease { get; }
}
