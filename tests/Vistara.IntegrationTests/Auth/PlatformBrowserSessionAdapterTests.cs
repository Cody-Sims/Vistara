using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Exercises the shipped <c>PlatformBrowserSessionAdapter</c> against real
/// persistence: no fakes, no ambient tenant scope, and a stale cookie that
/// belongs to a different tenant.
/// </summary>
public sealed class PlatformBrowserSessionAdapterTests
{
    private const string Password = "correct-horse-battery";

    [Fact]
    public async Task Login_succeeds_with_no_ambient_tenant_scope()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        var ambient = scope.ServiceProvider
            .GetRequiredService<AccountSurfaceHarness.AmbientTenantScope>();
        Result<BrowserSessionResult> session = await scope.ServiceProvider
            .GetRequiredService<IBrowserSessionPort>()
            .LoginAsync(
                new BrowserLoginCommand(
                    "owner@example.com",
                    Password,
                    null,
                    null),
                default);

        Assert.True(session.TryGetValue(out BrowserSessionResult? issued));
        Assert.Equal(owner.TenantId, issued.User.TenantId);
        Assert.Equal("TenantOwner", issued.User.Role);
        Assert.Contains("__Host-vistara-session=", issued.SetCookieHeader, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(issued.AntiforgeryToken));
        Assert.Equal(Guid.Empty, ambient.TenantId);

        Assert.Equal(1, await harness.CountCookieSessionsAsync(owner.TenantId, true));
    }

    [Fact]
    public async Task Login_rejects_the_wrong_password_and_an_unknown_login()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();

        Result<BrowserSessionResult> wrongPassword =
            await LoginAsync(harness, "owner@example.com", "not-the-password");
        Result<BrowserSessionResult> unknown =
            await LoginAsync(harness, "nobody@example.com", Password);

        Assert.True(wrongPassword.IsFailure);
        Assert.True(unknown.IsFailure);
        Assert.Equal(ErrorCategory.Unauthorized, wrongPassword.Error!.Category);
        Assert.Equal(wrongPassword.Error.Code, unknown.Error!.Code);
    }

    [Fact]
    public async Task Login_retires_a_cookie_that_belongs_to_a_different_tenant()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView first = await harness.ProvisionAsync();
        Guid secondTenantId = await AddSecondTenantAsync(harness, first.UserId);

        Result<BrowserSessionResult> firstSession = await LoginAsync(
            harness,
            "owner@example.com",
            Password,
            first.TenantId);
        Assert.True(firstSession.TryGetValue(out BrowserSessionResult? firstIssued));
        string staleToken = ReadCookieValue(firstIssued.SetCookieHeader);

        Result<BrowserSessionResult> secondSession = await LoginAsync(
            harness,
            "owner@example.com",
            Password,
            secondTenantId,
            staleToken);

        Assert.True(secondSession.TryGetValue(out BrowserSessionResult? secondIssued));
        Assert.Equal(secondTenantId, secondIssued.User.TenantId);
        Assert.NotEqual(staleToken, ReadCookieValue(secondIssued.SetCookieHeader));

        Assert.Equal(1, await harness.CountCookieSessionsAsync(first.TenantId, false));
        Assert.Equal(0, await harness.CountCookieSessionsAsync(first.TenantId, true));
        Assert.Equal(1, await harness.CountCookieSessionsAsync(secondTenantId, true));
    }

    [Fact]
    public async Task Login_selects_the_requested_tenant_among_several_memberships()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView first = await harness.ProvisionAsync();
        Guid secondTenantId = await AddSecondTenantAsync(harness, first.UserId);

        Result<BrowserSessionResult> session = await LoginAsync(
            harness,
            "owner@example.com",
            Password,
            secondTenantId);

        Assert.True(session.TryGetValue(out BrowserSessionResult? issued));
        Assert.Equal(secondTenantId, issued.User.TenantId);
        Assert.Equal("Member", issued.User.Role);
        Assert.Equal(2, issued.User.Tenants.Count);
    }

    [Fact]
    public async Task Login_refuses_a_tenant_the_principal_does_not_belong_to()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();

        Result<BrowserSessionResult> session = await LoginAsync(
            harness,
            "owner@example.com",
            Password,
            Guid.CreateVersion7());

        Assert.True(session.IsFailure);
        Assert.Equal(ErrorCategory.Forbidden, session.Error!.Category);
    }

    [Fact]
    public async Task Membership_resolution_issues_one_query_per_principal()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await AddSecondTenantAsync(harness, owner.UserId);

        await using IdentityCatalogDbContext catalog = harness.CreateCatalog();
        IReadOnlyList<Vistara.Persistence.Tenancy.PersistedTenantMembership> memberships =
            await new RelationalIdentityCatalog(catalog)
                .ListMembershipsAsync(owner.UserId, default);

        Assert.Equal(2, memberships.Count);
        Assert.All(
            memberships,
            membership => Assert.NotEqual(Guid.Empty, membership.TenantId));
        // A principal with two memberships resolves both without enumerating
        // tenants, so an unrelated tenant never participates in the read.
        Guid unrelatedTenantId = await AddUnrelatedTenantAsync(harness);
        IReadOnlyList<Vistara.Persistence.Tenancy.PersistedTenantMembership> again =
            await new RelationalIdentityCatalog(harness.CreateCatalog())
                .ListMembershipsAsync(owner.UserId, default);
        Assert.Equal(2, again.Count);
        Assert.DoesNotContain(again, m => m.TenantId == unrelatedTenantId);
    }

    [Fact]
    public async Task Describing_a_principal_never_leaks_other_tenants_when_restricted()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        Guid secondTenantId = await AddSecondTenantAsync(harness, owner.UserId);

        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IBrowserSessionPort>();
        Result<CurrentUserView> browser = await sessions.DescribeAsync(
            owner.TenantId,
            owner.UserId,
            includeOtherTenants: true,
            default);
        Result<CurrentUserView> tenantBound = await sessions.DescribeAsync(
            owner.TenantId,
            owner.UserId,
            includeOtherTenants: false,
            default);

        Assert.True(browser.TryGetValue(out CurrentUserView? all));
        Assert.True(tenantBound.TryGetValue(out CurrentUserView? scoped));
        Assert.Equal(2, all.Tenants.Count);
        Assert.Equal(owner.TenantId, Assert.Single(scoped.Tenants).TenantId);
        Assert.DoesNotContain(
            scoped.Tenants,
            tenant => tenant.TenantId == secondTenantId);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_always_returns_a_deletion_cookie()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        Result<BrowserSessionResult> session =
            await LoginAsync(harness, "owner@example.com", Password);
        Assert.True(session.TryGetValue(out BrowserSessionResult? issued));
        string token = ReadCookieValue(issued.SetCookieHeader);

        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IBrowserSessionPort>();
        string first = await sessions.LogoutAsync(token, default);
        string second = await sessions.LogoutAsync(token, default);
        string anonymous = await sessions.LogoutAsync(null, default);

        Assert.Contains("Max-Age=0", first, StringComparison.Ordinal);
        Assert.Contains("Max-Age=0", second, StringComparison.Ordinal);
        Assert.Contains("Max-Age=0", anonymous, StringComparison.Ordinal);
        Assert.Equal(0, await harness.CountCookieSessionsAsync(owner.TenantId, true));
    }

    private static async ValueTask<Result<BrowserSessionResult>> LoginAsync(
        AccountSurfaceHarness harness,
        string login,
        string password,
        Guid? tenantId = null,
        string? existingSessionToken = null)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IBrowserSessionPort>()
            .LoginAsync(
                new BrowserLoginCommand(
                    login,
                    password,
                    tenantId,
                    existingSessionToken),
                default);
    }

    private static async Task<Guid> AddSecondTenantAsync(
        AccountSurfaceHarness harness,
        Guid userId)
    {
        Guid tenantId = Guid.CreateVersion7();
        DateTimeOffset now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
        await using VistaraDbContext context = harness.CreateContext(tenantId);
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = "second",
            Name = "Second",
            Status = "Active",
            SettingsJson = "{}",
            QuotasJson = "{}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        await context.SaveChangesAsync(default);
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = tenantId,
            UserId = userId,
            Role = "Member",
            Status = "Active",
            InvitedAtUtc = now,
            JoinedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        await context.SaveChangesAsync(default);
        return tenantId;
    }

    private static async Task<Guid> AddUnrelatedTenantAsync(AccountSurfaceHarness harness)
    {
        Guid tenantId = Guid.CreateVersion7();
        DateTimeOffset now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
        await using VistaraDbContext context = harness.CreateContext(tenantId);
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = "unrelated",
            Name = "Unrelated",
            Status = "Active",
            SettingsJson = "{}",
            QuotasJson = "{}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        await context.SaveChangesAsync(default);
        return tenantId;
    }

    private static string ReadCookieValue(string setCookieHeader)
    {
        string first = setCookieHeader.Split(';')[0];
        return first[(first.IndexOf('=', StringComparison.Ordinal) + 1)..];
    }
}
