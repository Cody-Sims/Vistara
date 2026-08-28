using Vistara.Domain.Tenancy;

namespace Vistara.Application.Tenancy;

public interface ITenantRepository
{
    ValueTask<Tenant?> FindByIdAsync(TenantId id, CancellationToken cancellationToken);

    ValueTask<Tenant?> FindBySlugAsync(
        TenantSlug slug,
        CancellationToken cancellationToken);

    ValueTask AddAsync(Tenant tenant, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        Tenant tenant,
        long expectedVersion,
        CancellationToken cancellationToken);
}
