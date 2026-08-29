namespace Vistara.Auth.Delivery;

internal static class DeliveryGrantTelemetry
{
    public static async ValueTask TryWriteAsync(
        IDeliveryGrantAuditSink sink,
        DeliveryGrantAuditEvent auditEvent)
    {
#pragma warning disable CA1031
        try
        {
            await sink.WriteAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }
}
