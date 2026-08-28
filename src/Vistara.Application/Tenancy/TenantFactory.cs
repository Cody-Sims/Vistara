using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Application.Tenancy;

public sealed class TenantFactory(IUuid7Generator idGenerator, IClock clock)
{
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));

    public Result<Tenant> Create(string slug, string name) =>
        Tenant.Create(
            new TenantId(_idGenerator.NewId()),
            slug,
            name,
            _clock.UtcNow);

    public Result<TenantMembership> InviteMember(
        TenantId tenantId,
        UserId userId,
        TenantRole role) =>
        TenantMembership.Invite(tenantId, userId, role, _clock.UtcNow);
}
