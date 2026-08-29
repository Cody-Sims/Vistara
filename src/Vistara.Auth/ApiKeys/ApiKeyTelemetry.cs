using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.ApiKeys;

internal static class ApiKeyTelemetry
{
    public static async ValueTask TryRecordUsageAsync(
        IApiKeyStore store,
        TenantId tenantId,
        ApiKeyId keyId,
        DateTimeOffset coarseUsedAt)
    {
#pragma warning disable CA1031
        try
        {
            await store.RecordLastUsedAsync(
                tenantId,
                keyId,
                coarseUsedAt,
                CancellationToken.None);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    public static async ValueTask TryWriteAuditAsync(
        IApiKeyAuditSink auditSink,
        ApiKeyAuditEvent auditEvent)
    {
#pragma warning disable CA1031
        try
        {
            await auditSink.WriteAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }
}
