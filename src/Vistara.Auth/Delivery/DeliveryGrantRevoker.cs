using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Auth.Delivery;

public sealed class DeliveryGrantRevoker
{
    private readonly IClock _clock;
    private readonly IDeliveryGrantStore _store;
    private readonly IDeliveryGrantAuditSink _auditSink;

    public DeliveryGrantRevoker(
        IClock clock,
        IDeliveryGrantStore store,
        IDeliveryGrantAuditSink auditSink)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    }

    public async ValueTask<Result> RevokeAsync(
        Guid tenantId,
        Guid actorId,
        Guid grantId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        DeliveryGrantRecord? revoked = await _store.RevokeAsync(
            tenantId,
            grantId,
            expectedVersion,
            now,
            cancellationToken);
        if (revoked is null)
        {
            return Result.Failure(DeliveryGrantErrors.Concealed);
        }

        await DeliveryGrantTelemetry.TryWriteAsync(
            _auditSink,
            new DeliveryGrantAuditEvent(
                DeliveryGrantAuditAction.Revoked,
                revoked.TenantId,
                revoked.GrantId,
                actorId,
                null,
                now));
        return Result.Success();
    }
}
