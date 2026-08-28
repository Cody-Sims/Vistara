using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Application.Tenancy;

public interface ITenantMembershipRepository
{
    ValueTask<TenantMembership?> FindAsync(
        TenantId tenantId,
        UserId userId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<TenantMembership>> ListForUserAsync(
        UserId userId,
        CancellationToken cancellationToken);

    ValueTask AddAsync(
        TenantMembership membership,
        CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        TenantMembership membership,
        long expectedVersion,
        CancellationToken cancellationToken);
}
