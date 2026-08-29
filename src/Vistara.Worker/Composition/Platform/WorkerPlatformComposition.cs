using Vistara.Persistence;
using Vistara.Persistence.Outbox;

namespace Vistara.Worker.Composition.Platform;

internal sealed class WorkerTenantContext :
    IMutableTenantScope,
    IOutboxTenantContext
{
    public Guid TenantId { get; private set; }

    internal void Establish(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new InvalidOperationException(
                "A UUIDv7 tenant scope is required.");
        }

        TenantId = tenantId;
    }

    void IMutableTenantScope.Establish(Guid tenantId) => Establish(tenantId);
}
