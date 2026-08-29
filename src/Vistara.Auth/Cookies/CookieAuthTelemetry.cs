namespace Vistara.Auth.Cookies;

internal static class CookieAuthTelemetry
{
    public static async ValueTask TryWriteAsync(
        ICookieAuthAuditSink sink,
        CookieAuthAuditEvent auditEvent)
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
