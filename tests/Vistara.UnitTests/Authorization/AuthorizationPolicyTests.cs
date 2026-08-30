using Vistara.Application.Tenancy.Authorization;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.UnitTests.Authorization;

public sealed class AuthorizationPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2032, 6, 7, 8, 9, 10, TimeSpan.Zero);

    private static readonly TenantId TenantId = new(Guid.CreateVersion7(Now));
    private static readonly TenantId OtherTenantId =
        new(Guid.CreateVersion7(Now.AddMilliseconds(1)));
    private static readonly UserId UserId =
        new(Guid.CreateVersion7(Now.AddMilliseconds(2)));

    public static TheoryData<TenantRole, TenantAction, ActorScope, bool> RoleScopeMatrix =>
        new()
        {
            { TenantRole.Viewer, TenantAction.ReadAssets, ActorScope.ReadAssets, true },
            { TenantRole.Viewer, TenantAction.UploadAssets, ActorScope.UploadAssets, false },
            { TenantRole.Member, TenantAction.UploadAssets, ActorScope.UploadAssets, true },
            { TenantRole.Member, TenantAction.ManageMembers, ActorScope.ManageMembers, false },
            { TenantRole.TenantAdmin, TenantAction.ManageMembers, ActorScope.ManageMembers, true },
            { TenantRole.TenantAdmin, TenantAction.ManageQuotas, ActorScope.ManageQuotas, false },
            { TenantRole.TenantOwner, TenantAction.ManageQuotas, ActorScope.ManageQuotas, true },
            { TenantRole.TenantOwner, TenantAction.ManageApiKeys, ActorScope.None, false },
        };

    [Theory]
    [MemberData(nameof(RoleScopeMatrix))]
    public void Tenant_policy_requires_both_role_and_scope(
        TenantRole role,
        TenantAction action,
        ActorScope scopes,
        bool allowed)
    {
        AuthenticatedActor actor = CreateActor(role, scopes);

        Result result = AuthorizationPolicy.AuthorizeTenant(actor, TenantId, action);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("authorization.forbidden", result.Error?.Code);
        }
    }

    [Fact]
    public void Actor_context_requires_the_authenticated_users_active_membership()
    {
        TenantMembership invited = Invite(TenantRole.Member);

        Result<AuthenticatedActor> inactive =
            AuthenticatedActor.Create(UserId, invited, ActorScope.All);
        Result<AuthenticatedActor> wrongUser = AuthenticatedActor.Create(
            new UserId(Guid.CreateVersion7(Now.AddMinutes(1))),
            Activate(Invite(TenantRole.Member)),
            ActorScope.All);

        Assert.Equal("authorization.inactive_membership", inactive.Error?.Code);
        Assert.Equal("authorization.membership_principal_mismatch", wrongUser.Error?.Code);
    }

    [Fact]
    public void Cross_tenant_denial_is_concealed_as_not_found()
    {
        AuthenticatedActor actor = CreateActor(TenantRole.TenantOwner, ActorScope.All);

        Result result = AuthorizationPolicy.AuthorizeTenant(
            actor,
            OtherTenantId,
            TenantAction.ManageQuotas);

        Assert.Equal(ErrorCategory.NotFound, result.Error?.Category);
        Assert.Equal("authorization.resource_not_found", result.Error?.Code);
    }

    [Fact]
    public void Object_policy_enforces_ownership_and_privileged_overrides()
    {
        AuthenticatedActor member = CreateActor(
            TenantRole.Member,
            ActorScope.ManageMetadata);
        TenantObjectReference owned = new(TenantId, UserId.Value);
        TenantObjectReference somebodyElses = new(
            TenantId,
            Guid.CreateVersion7(Now.AddMinutes(2)));

        Result ownResult = AuthorizationPolicy.AuthorizeObject(
            member,
            TenantId,
            owned,
            ObjectAction.UpdateMetadata);
        Result otherResult = AuthorizationPolicy.AuthorizeObject(
            member,
            TenantId,
            somebodyElses,
            ObjectAction.UpdateMetadata);
        Result adminResult = AuthorizationPolicy.AuthorizeObject(
            CreateActor(TenantRole.TenantAdmin, ActorScope.ManageMetadata),
            TenantId,
            somebodyElses,
            ObjectAction.UpdateMetadata);

        Assert.True(ownResult.IsSuccess);
        Assert.Equal(ErrorCategory.Forbidden, otherResult.Error?.Category);
        Assert.True(adminResult.IsSuccess);
    }

    [Fact]
    public void Authorization_precedes_existence_sensitive_disclosure()
    {
        AuthenticatedActor viewer = CreateActor(
            TenantRole.Viewer,
            ActorScope.ReadAssets);

        Result forbidden = AuthorizationPolicy.AuthorizeObject(
            viewer,
            TenantId,
            resource: null,
            ObjectAction.UpdateMetadata);
        Result missing = AuthorizationPolicy.AuthorizeObject(
            viewer,
            TenantId,
            resource: null,
            ObjectAction.Read);
        Result concealed = AuthorizationPolicy.AuthorizeObject(
            viewer,
            OtherTenantId,
            resource: null,
            ObjectAction.UpdateMetadata);

        Assert.Equal(ErrorCategory.Forbidden, forbidden.Error?.Category);
        Assert.Equal(ErrorCategory.NotFound, missing.Error?.Category);
        Assert.Equal(ErrorCategory.NotFound, concealed.Error?.Category);
    }

    private static AuthenticatedActor CreateActor(TenantRole role, ActorScope scopes)
    {
        Result<AuthenticatedActor> result =
            AuthenticatedActor.Create(UserId, Activate(Invite(role)), scopes);
        Assert.True(result.TryGetValue(out AuthenticatedActor? actor));
        return actor;
    }

    private static TenantMembership Invite(TenantRole role)
    {
        Result<TenantMembership> result =
            TenantMembership.Invite(TenantId, UserId, role, Now);
        Assert.True(result.TryGetValue(out TenantMembership? membership));
        return membership;
    }

    private static TenantMembership Activate(TenantMembership membership)
    {
        Assert.True(membership.Activate(Now.AddSeconds(1)).IsSuccess);
        return membership;
    }
}
