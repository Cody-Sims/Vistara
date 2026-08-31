using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Tenants;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Exercises the shipped tenant directory adapter over real persistence:
/// listing, invitation, role and status changes, version concurrency, and the
/// last-owner invariant.
/// </summary>
public sealed class TenantMemberAdministrationTests
{
    [Fact]
    public async Task Inviting_then_updating_a_member_moves_the_version_forward()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        TenantMemberView invited = await InviteAsync(
            harness,
            owner,
            "member@example.com",
            "Member");
        Result<TenantMemberView> updated = await UpdateAsync(
            harness,
            owner,
            invited.UserId,
            role: "TenantAdmin",
            status: "Active",
            expectedVersion: invited.Version);

        Assert.Equal("Invited", invited.Status);
        Assert.True(updated.TryGetValue(out TenantMemberView? member));
        Assert.Equal("TenantAdmin", member.Role);
        Assert.Equal("Active", member.Status);
        Assert.True(member.Version > invited.Version);
        Assert.Equal("member@example.com", member.Email);
    }

    [Fact]
    public async Task A_stale_version_is_refused_and_the_member_is_unchanged()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView invited = await InviteAsync(
            harness,
            owner,
            "member@example.com",
            "Member");
        await UpdateAsync(
            harness,
            owner,
            invited.UserId,
            status: "Active",
            expectedVersion: invited.Version);

        Result<TenantMemberView> stale = await UpdateAsync(
            harness,
            owner,
            invited.UserId,
            role: "Viewer",
            expectedVersion: invited.Version);

        Assert.True(stale.IsFailure);
        Assert.Equal("tenants.member_version_conflict", stale.Error!.Code);
        TenantMemberView current = await FindAsync(harness, owner, invited.UserId);
        Assert.Equal("Member", current.Role);
    }

    [Fact]
    public async Task The_last_active_owner_can_neither_be_demoted_nor_suspended()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView self = await FindAsync(harness, owner, owner.UserId);

        Result<TenantMemberView> demoted = await UpdateAsync(
            harness,
            owner,
            owner.UserId,
            role: "Member",
            expectedVersion: self.Version);
        Result<TenantMemberView> suspended = await UpdateAsync(
            harness,
            owner,
            owner.UserId,
            status: "Suspended",
            expectedVersion: self.Version);

        foreach (Result<TenantMemberView> refused in new[] { demoted, suspended })
        {
            Assert.True(refused.IsFailure);
            Assert.Equal("tenants.last_owner", refused.Error!.Code);
            Assert.Equal(ErrorCategory.Conflict, refused.Error.Category);
        }

        TenantMemberView current = await FindAsync(harness, owner, owner.UserId);
        Assert.Equal("TenantOwner", current.Role);
        Assert.Equal("Active", current.Status);
    }

    [Fact]
    public async Task An_owner_may_step_down_once_a_second_owner_is_active()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView second = await InviteAsync(
            harness,
            owner,
            "second@example.com",
            "TenantOwner");
        await UpdateAsync(
            harness,
            owner,
            second.UserId,
            status: "Active",
            expectedVersion: second.Version);

        TenantMemberView self = await FindAsync(harness, owner, owner.UserId);
        Result<TenantMemberView> stepped = await UpdateAsync(
            harness,
            owner,
            owner.UserId,
            role: "TenantAdmin",
            expectedVersion: self.Version);

        Assert.True(stepped.TryGetValue(out TenantMemberView? member));
        Assert.Equal("TenantAdmin", member.Role);
    }

    [Fact]
    public async Task An_unknown_member_is_reported_as_not_found()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<TenantMemberView> updated = await UpdateAsync(
            harness,
            owner,
            Guid.CreateVersion7(),
            role: "Member",
            expectedVersion: 1);

        Assert.True(updated.IsFailure);
        Assert.Equal("tenants.member_not_found", updated.Error!.Code);
        Assert.Equal(ErrorCategory.NotFound, updated.Error.Category);
    }

    [Theory]
    [InlineData("PlatformAdmin", null, "tenants.invalid_role")]
    [InlineData(null, "Invited", "tenants.invalid_status")]
    [InlineData(null, "Archived", "tenants.invalid_status")]
    [InlineData(null, null, "tenants.empty_member_patch")]
    public async Task Unsupported_patches_are_refused(
        string? role,
        string? status,
        string expectedCode)
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView invited = await InviteAsync(
            harness,
            owner,
            "member@example.com",
            "Member");

        Result<TenantMemberView> updated = await UpdateAsync(
            harness,
            owner,
            invited.UserId,
            role,
            status,
            invited.Version);

        Assert.True(updated.IsFailure);
        Assert.Equal(expectedCode, updated.Error!.Code);
    }

    [Fact]
    public async Task Every_membership_change_is_audited_inside_the_tenant()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        TenantMemberView invited = await InviteAsync(
            harness,
            owner,
            "member@example.com",
            "Member");
        await UpdateAsync(
            harness,
            owner,
            invited.UserId,
            status: "Active",
            expectedVersion: invited.Version);

        await using VistaraDbContext read = harness.CreateContext(owner.TenantId);
        string[] actions = await read.AuditEvents
            .OrderBy(row => row.OccurredAtUtc)
            .Select(row => row.Action)
            .ToArrayAsync(default);
        Assert.Contains("tenant.member.invited", actions);
        Assert.Contains("tenant.member.updated", actions);
    }

    private static async ValueTask<TenantMemberView> InviteAsync(
        AccountSurfaceHarness harness,
        ProvisionedOwnerView owner,
        string email,
        string role)
    {
        await using AsyncServiceScope scope =
            harness.CreateTenantScope(owner.TenantId);
        Result<TenantMemberView> invited = await scope.ServiceProvider
            .GetRequiredService<ITenantDirectoryPort>()
            .InviteMemberAsync(
                new TenantMemberInvitation(owner.TenantId, owner.UserId, email, role),
                default);
        Assert.True(
            invited.TryGetValue(out TenantMemberView? member),
            invited.Error?.Message ?? "Invitation failed.");
        return member!;
    }

    private static async ValueTask<Result<TenantMemberView>> UpdateAsync(
        AccountSurfaceHarness harness,
        ProvisionedOwnerView owner,
        Guid memberUserId,
        string? role = null,
        string? status = null,
        long expectedVersion = 1)
    {
        await using AsyncServiceScope scope =
            harness.CreateTenantScope(owner.TenantId);
        return await scope.ServiceProvider
            .GetRequiredService<ITenantDirectoryPort>()
            .UpdateMemberAsync(
                new TenantMemberUpdate(
                    owner.TenantId,
                    owner.UserId,
                    memberUserId,
                    role,
                    status),
                expectedVersion,
                default);
    }

    private static async ValueTask<TenantMemberView> FindAsync(
        AccountSurfaceHarness harness,
        ProvisionedOwnerView owner,
        Guid memberUserId)
    {
        await using AsyncServiceScope scope =
            harness.CreateTenantScope(owner.TenantId);
        IReadOnlyList<TenantMemberView> members = await scope.ServiceProvider
            .GetRequiredService<ITenantDirectoryPort>()
            .ListMembersAsync(owner.TenantId, default);
        return members.Single(member => member.UserId == memberUserId);
    }
}
