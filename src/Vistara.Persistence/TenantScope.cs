namespace Vistara.Persistence;

public interface ITenantScope
{
    Guid TenantId { get; }
}

public interface IMutableTenantScope : ITenantScope
{
    void Establish(Guid tenantId);
}

public sealed class FixedTenantScope : ITenantScope
{
    public FixedTenantScope(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException(
                "A UUIDv7 tenant scope is required.",
                nameof(tenantId));
        }

        TenantId = tenantId;
    }

    public Guid TenantId { get; }
}

internal static class TenantScopeGuard
{
    internal static Guid RequireTenantId(ITenantScope tenantScope)
    {
        ArgumentNullException.ThrowIfNull(tenantScope);
        Guid tenantId = tenantScope.TenantId;
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new InvalidOperationException(
                "A tenant scope must be established before database access.");
        }

        return tenantId;
    }

    internal static void EstablishOrValidate(
        ITenantScope tenantScope,
        Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException(
                "A UUIDv7 tenant scope is required.",
                nameof(tenantId));
        }

        if (tenantScope is IMutableTenantScope mutable)
        {
            mutable.Establish(tenantId);
            return;
        }

        if (TenantScopeGuard.RequireTenantId(tenantScope) != tenantId)
        {
            throw new InvalidOperationException(
                "The requested tenant does not match the established tenant scope.");
        }
    }
}
