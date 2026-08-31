using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Tenants;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Role hierarchy and the last-owner invariant, proven against the shipped
/// adapter and a real database rather than a pre-check in the endpoint.
/// </summary>
public sealed class TenantMemberHierarchyTests
{
    [Fact]
    public async Task An_admin_may_not_change_an_owner_role()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView admin = await AddMemberAsync(harness, owner, "admin@example.com", "TenantAdmin");
        TenantMemberView target = await FindAsync(harness, owner.TenantId, owner.UserId);

        Result<TenantMemberView> refused = await UpdateAsync(
            harness,
            owner.TenantId,
            admin.UserId,
            TenantRoleName.TenantAdmin,
            owner.UserId,
            role: "Member",
            expectedVersion: target.Version);

        Assert.True(refused.IsFailure);
        Assert.Equal("tenants.owner_requires_owner", refused.Error!.Code);
        Assert.Equal(ErrorCategory.Forbidden, refused.Error.Category);
        Assert.Equal(
            "TenantOwner",
            (await FindAsync(harness, owner.TenantId, owner.UserId)).Role);
    }

    [Fact]
    public async Task An_admin_may_not_suspend_an_owner()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView admin = await AddMemberAsync(harness, owner, "admin@example.com", "TenantAdmin");
        TenantMemberView target = await FindAsync(harness, owner.TenantId, owner.UserId);

        Result<TenantMemberView> refused = await UpdateAsync(
            harness,
            owner.TenantId,
            admin.UserId,
            TenantRoleName.TenantAdmin,
            owner.UserId,
            status: "Suspended",
            expectedVersion: target.Version);

        Assert.True(refused.IsFailure);
        Assert.Equal("tenants.owner_requires_owner", refused.Error!.Code);
    }

    [Fact]
    public async Task An_admin_may_still_manage_a_member()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView admin = await AddMemberAsync(harness, owner, "admin@example.com", "TenantAdmin");
        TenantMemberView member = await AddMemberAsync(harness, owner, "member@example.com", "Member");

        Result<TenantMemberView> updated = await UpdateAsync(
            harness,
            owner.TenantId,
            admin.UserId,
            TenantRoleName.TenantAdmin,
            member.UserId,
            role: "Viewer",
            expectedVersion: member.Version);

        Assert.True(updated.TryGetValue(out TenantMemberView? view));
        Assert.Equal("Viewer", view.Role);
    }

    [Fact]
    public async Task An_admin_may_not_promote_anyone_to_owner()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView admin = await AddMemberAsync(harness, owner, "admin@example.com", "TenantAdmin");
        TenantMemberView member = await AddMemberAsync(harness, owner, "member@example.com", "Member");

        Result<TenantMemberView> refused = await UpdateAsync(
            harness,
            owner.TenantId,
            admin.UserId,
            TenantRoleName.TenantAdmin,
            member.UserId,
            role: "TenantOwner",
            expectedVersion: member.Version);

        Assert.True(refused.IsFailure);
        Assert.Equal("tenants.owner_requires_owner", refused.Error!.Code);
    }

    [Fact]
    public async Task Concurrent_demotions_of_different_owners_leave_one_owner()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView second =
            await AddMemberAsync(harness, owner, "second@example.com", "TenantOwner");
        TenantMemberView first = await FindAsync(harness, owner.TenantId, owner.UserId);

        Result<TenantMemberView>[] results = await Task.WhenAll(
            Task.Run(() => UpdateAsync(
                harness,
                owner.TenantId,
                owner.UserId,
                TenantRoleName.TenantOwner,
                owner.UserId,
                role: "TenantAdmin",
                expectedVersion: first.Version).AsTask()),
            Task.Run(() => UpdateAsync(
                harness,
                owner.TenantId,
                owner.UserId,
                TenantRoleName.TenantOwner,
                second.UserId,
                role: "TenantAdmin",
                expectedVersion: second.Version).AsTask()));

        Assert.Single(results, result => result.IsSuccess);
        Result<TenantMemberView> refused = Assert.Single(
            results,
            result => result.IsFailure);
        Assert.Equal(ErrorCategory.Conflict, refused.Error!.Category);
        IReadOnlyList<TenantMemberView> members =
            await ListAsync(harness, owner.TenantId);
        Assert.Single(
            members,
            member => member.Role == "TenantOwner" && member.Status == "Active");
    }

    [Fact]
    public async Task The_only_owner_still_cannot_demote_themselves()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView self = await FindAsync(harness, owner.TenantId, owner.UserId);

        Result<TenantMemberView> refused = await UpdateAsync(
            harness,
            owner.TenantId,
            owner.UserId,
            TenantRoleName.TenantOwner,
            owner.UserId,
            role: "TenantAdmin",
            expectedVersion: self.Version);

        Assert.True(refused.IsFailure);
        Assert.Equal("tenants.last_owner", refused.Error!.Code);
        Assert.Equal(
            "TenantOwner",
            (await FindAsync(harness, owner.TenantId, owner.UserId)).Role);
    }

    private static async ValueTask<TenantMemberView> AddMemberAsync(
        AccountSurfaceHarness harness,
        ProvisionedOwnerView owner,
        string email,
        string role)
    {
        await using AsyncServiceScope scope = harness.CreateTenantScope(owner.TenantId);
        Result<TenantMemberView> invited = await scope.ServiceProvider
            .GetRequiredService<ITenantDirectoryPort>()
            .InviteMemberAsync(
                new TenantMemberInvitation(owner.TenantId, owner.UserId, email, role),
                default);
        Assert.True(invited.TryGetValue(out TenantMemberView? member));
        Result<TenantMemberView> activated = await UpdateAsync(
            harness,
            owner.TenantId,
            owner.UserId,
            TenantRoleName.TenantOwner,
            member!.UserId,
            status: "Active",
            expectedVersion: member.Version);
        Assert.True(activated.TryGetValue(out TenantMemberView? active));
        return active!;
    }

    private static async ValueTask<Result<TenantMemberView>> UpdateAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        Guid actorUserId,
        string actorRole,
        Guid memberUserId,
        string? role = null,
        string? status = null,
        long expectedVersion = 1)
    {
        await using AsyncServiceScope scope = harness.CreateTenantScope(tenantId);
        return await scope.ServiceProvider
            .GetRequiredService<ITenantDirectoryPort>()
            .UpdateMemberAsync(
                new TenantMemberUpdate(
                    tenantId,
                    actorUserId,
                    actorRole,
                    memberUserId,
                    role,
                    status),
                expectedVersion,
                default);
    }

    private static async ValueTask<IReadOnlyList<TenantMemberView>> ListAsync(
        AccountSurfaceHarness harness,
        Guid tenantId)
    {
        await using AsyncServiceScope scope = harness.CreateTenantScope(tenantId);
        return await scope.ServiceProvider
            .GetRequiredService<ITenantDirectoryPort>()
            .ListMembersAsync(tenantId, default);
    }

    private static async ValueTask<TenantMemberView> FindAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        Guid memberUserId)
    {
        IReadOnlyList<TenantMemberView> members = await ListAsync(harness, tenantId);
        return members.Single(member => member.UserId == memberUserId);
    }
}

internal static class TenantRoleName
{
    internal const string TenantOwner = "TenantOwner";
    internal const string TenantAdmin = "TenantAdmin";
}
