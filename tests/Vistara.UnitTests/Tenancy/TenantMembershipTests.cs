using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.UnitTests.Tenancy;

public sealed class TenantMembershipTests
{
    private static readonly DateTimeOffset InvitedAt =
        new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);

    private static readonly TenantId TenantId = new(Guid.CreateVersion7(InvitedAt));
    private static readonly UserId UserId = new(Guid.CreateVersion7(InvitedAt.AddMilliseconds(1)));

    [Fact]
    public void Invite_binds_one_user_to_one_tenant_with_an_explicit_role()
    {
        Result<TenantMembership> result = TenantMembership.Invite(
            TenantId,
            UserId,
            TenantRole.Member,
            InvitedAt);

        Assert.True(result.TryGetValue(out TenantMembership? membership));
        Assert.Equal(TenantId, membership.TenantId);
        Assert.Equal(UserId, membership.UserId);
        Assert.Equal(TenantRole.Member, membership.Role);
        Assert.Equal(MembershipStatus.Invited, membership.Status);
        Assert.Null(membership.JoinedAt);
        Assert.Equal(1, membership.Version);
        Assert.True(membership.IsForTenant(TenantId));
        Assert.False(membership.IsForTenant(new TenantId(Guid.CreateVersion7(InvitedAt.AddDays(1)))));
    }

    [Fact]
    public void Invite_rejects_undefined_roles()
    {
        Result<TenantMembership> result = TenantMembership.Invite(
            TenantId,
            UserId,
            (TenantRole)999,
            InvitedAt);

        Assert.Equal("tenancy.invalid_role", result.Error?.Code);
    }

    [Fact]
    public void Membership_lifecycle_sets_joined_time_and_rejects_invalid_transitions()
    {
        TenantMembership membership = CreateMembership();
        DateTimeOffset joinedAt = InvitedAt.AddMinutes(1);

        Assert.True(membership.Activate(joinedAt).IsSuccess);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(joinedAt, membership.JoinedAt);

        Assert.True(membership.Suspend(joinedAt.AddMinutes(1)).IsSuccess);
        Assert.True(membership.Activate(joinedAt.AddMinutes(2)).IsSuccess);
        Assert.Equal(joinedAt, membership.JoinedAt);

        Assert.True(membership.Remove(joinedAt.AddMinutes(3)).IsSuccess);
        Result reactivateRemoved = membership.Activate(joinedAt.AddMinutes(4));

        Assert.Equal("tenancy.membership_removed", reactivateRemoved.Error?.Code);
        Assert.Equal(MembershipStatus.Removed, membership.Status);
        Assert.Equal(5, membership.Version);
    }

    [Fact]
    public void Role_change_rejects_duplicates_undefined_roles_and_removed_memberships()
    {
        TenantMembership membership = CreateMembership();

        Result duplicate = membership.ChangeRole(TenantRole.Member, InvitedAt.AddMinutes(1));
        Result undefined = membership.ChangeRole((TenantRole)999, InvitedAt.AddMinutes(1));
        Assert.Equal("tenancy.role_unchanged", duplicate.Error?.Code);
        Assert.Equal("tenancy.invalid_role", undefined.Error?.Code);

        Assert.True(membership.ChangeRole(TenantRole.TenantAdmin, InvitedAt.AddMinutes(1)).IsSuccess);
        Assert.Equal(TenantRole.TenantAdmin, membership.Role);
        Assert.Equal(2, membership.Version);

        Assert.True(membership.Remove(InvitedAt.AddMinutes(2)).IsSuccess);
        Result removed = membership.ChangeRole(TenantRole.Viewer, InvitedAt.AddMinutes(3));
        Assert.Equal("tenancy.membership_removed", removed.Error?.Code);
        Assert.Equal(TenantRole.TenantAdmin, membership.Role);
    }

    private static TenantMembership CreateMembership()
    {
        Result<TenantMembership> result = TenantMembership.Invite(
            TenantId,
            UserId,
            TenantRole.Member,
            InvitedAt);
        Assert.True(result.TryGetValue(out TenantMembership? membership));
        return membership;
    }
}
