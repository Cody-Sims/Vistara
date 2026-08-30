using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Vistara.Persistence.Uploads;

internal static class TenantDatabaseTransaction
{
    internal static async ValueTask<IDbContextTransaction> BeginAsync(
        VistaraDbContext context,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("A UUIDv7 tenant ID is required.", nameof(tenantId));
        }

        if (context.TenantId == Guid.Empty)
        {
            context.EstablishTenant(tenantId);
        }

        if (context.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                "The persistence operation does not match the active tenant scope.");
        }

        IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        if (context.Database.ProviderName ==
            "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            _ = await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('vistara.tenant_id', {tenantId.ToString("D")}, true);",
                cancellationToken);
        }

        return transaction;
    }
}
