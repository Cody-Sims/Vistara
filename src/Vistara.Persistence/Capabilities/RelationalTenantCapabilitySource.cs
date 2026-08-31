using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Capabilities;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Capabilities;

/// <summary>
/// Reads the persisted tenant quota policy that narrows advertised capabilities.
/// </summary>
public sealed class RelationalTenantCapabilitySource(VistaraDbContext context)
    : ITenantCapabilitySource
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<TenantCapabilityLimits> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        TenantRow? tenant = await _context.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == (TenantKey)tenantId,
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tenant is null
            ? TenantCapabilityLimits.Unlimited
            : Read(tenant.QuotasJson);
    }

    internal static TenantCapabilityLimits Read(string quotasJson)
    {
        if (string.IsNullOrWhiteSpace(quotasJson))
        {
            return TenantCapabilityLimits.Unlimited;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(quotasJson);
            JsonElement root = document.RootElement;
            return new TenantCapabilityLimits(
                ReadPositive(root, "maximumUploadBytes") ??
                    ReadPositive(root, "maxUploadBytes"),
                ReadPositive(root, "concurrentUploads"));
        }
        catch (JsonException)
        {
            return TenantCapabilityLimits.Unlimited;
        }
    }

    private static long? ReadPositive(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out long value) ||
            value <= 0)
        {
            return null;
        }

        return value;
    }
}
