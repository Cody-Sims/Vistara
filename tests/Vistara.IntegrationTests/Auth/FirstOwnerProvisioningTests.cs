using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

public sealed class FirstOwnerProvisioningTests
{
    private const string Password = "correct-horse-battery";

    private static readonly string[] ContentionCodes =
    [
        "setup.already_provisioned",
        "setup.provisioning_contended",
    ];

    [Fact]
    public async Task Setup_availability_closes_once_an_owner_exists()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        Assert.True(await IsAvailableAsync(harness));
        _ = await harness.ProvisionAsync();
        Assert.False(await IsAvailableAsync(harness));
    }

    private static async Task<bool> IsAvailableAsync(AccountSurfaceHarness harness)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IFirstOwnerProvisioningPort>()
            .IsAvailableAsync(default);
    }

    [Fact]
    public async Task Provisioning_commits_the_whole_owner_in_one_transaction()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        await using VistaraDbContext read = harness.CreateContext(owner.TenantId);
        Assert.Equal(1, await read.Tenants.CountAsync(default));
        Assert.Equal(1, await read.Users.CountAsync(default));
        Assert.Equal(1, await read.LocalIdentities.CountAsync(default));
        Assert.Equal(1, await read.LocalCredentials.CountAsync(default));
        Assert.Equal(1, await read.AuditEvents.CountAsync(default));
        Assert.Equal(1, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal("Active", await read.TenantMemberships
            .Where(row => row.UserId == owner.UserId)
            .Select(row => row.Status)
            .SingleAsync(default));
        string storedHash = await read.LocalCredentials
            .Select(row => row.PasswordHash)
            .SingleAsync(default);
        Assert.DoesNotContain(Password, storedHash, StringComparison.Ordinal);
        Assert.StartsWith("pbkdf2-sha256$", storedHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_attempt_with_a_different_slug_and_email_is_refused()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<ProvisionedOwnerView> second = await ProvisionAsync(
            harness,
            "second",
            "second@example.com");

        Assert.True(second.IsFailure);
        Assert.Equal("setup.already_provisioned", second.Error!.Code);
        Assert.Equal(ErrorCategory.Conflict, second.Error.Category);
        await AssertSingleOwnerAsync(harness, owner.TenantId, "acme");
    }

    [Fact]
    public async Task Concurrent_attempts_with_distinct_slugs_produce_one_winner()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        Task<Result<ProvisionedOwnerView>>[] attempts =
        [
            Task.Run(() => ProvisionAsync(harness, "alpha", "alpha@example.com").AsTask()),
            Task.Run(() => ProvisionAsync(harness, "bravo", "bravo@example.com").AsTask()),
            Task.Run(() => ProvisionAsync(harness, "charlie", "charlie@example.com").AsTask()),
        ];
        Result<ProvisionedOwnerView>[] results = await Task.WhenAll(attempts);

        Result<ProvisionedOwnerView> winner = Assert.Single(
            results,
            result => result.IsSuccess);
        Assert.All(
            results.Where(result => result.IsFailure),
            result =>
            {
                Assert.Equal(ErrorCategory.Conflict, result.Error!.Category);
                Assert.Contains(
                    result.Error.Code,
                    ContentionCodes,
                    StringComparer.Ordinal);
            });
        Assert.True(winner.TryGetValue(out ProvisionedOwnerView? owner));
        await AssertSingleOwnerAsync(harness, owner.TenantId, owner.TenantSlug);
    }

    [Fact]
    public async Task An_injected_failure_before_commit_rolls_back_every_row()
    {
        var guard = new ThrowingProvisioningGuard();
        await using AccountSurfaceHarness harness = await AccountSurfaceHarness.CreateAsync(
            services => services.AddSingleton<IFirstOwnerProvisioningGuard>(guard));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ProvisionAsync(harness, "acme", "owner@example.com"));

        Assert.True(guard.Invoked);
        await AssertEmptyAsync(harness);
    }

    [Fact]
    public async Task A_cancellation_before_commit_rolls_back_every_row()
    {
        using var cancellation = new CancellationTokenSource();
        var guard = new CancellingProvisioningGuard(cancellation);
        await using AccountSurfaceHarness harness = await AccountSurfaceHarness.CreateAsync(
            services => services.AddSingleton<IFirstOwnerProvisioningGuard>(guard));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ProvisionAsync(
                harness,
                "acme",
                "owner@example.com",
                cancellationToken: cancellation.Token));

        Assert.True(guard.Invoked);
        await AssertEmptyAsync(harness);
    }

    [Fact]
    public async Task Provisioning_remains_retryable_after_a_rolled_back_attempt()
    {
        var guard = new ThrowingProvisioningGuard();
        await using AccountSurfaceHarness harness = await AccountSurfaceHarness.CreateAsync(
            services => services.AddSingleton<IFirstOwnerProvisioningGuard>(guard));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ProvisionAsync(harness, "acme", "owner@example.com"));
        guard.Disarm();

        Result<ProvisionedOwnerView> retry =
            await ProvisionAsync(harness, "acme", "owner@example.com");

        Assert.True(retry.TryGetValue(out ProvisionedOwnerView? owner));
        await AssertSingleOwnerAsync(harness, owner.TenantId, "acme");
    }

    [Fact]
    public async Task Provisioning_rejects_a_short_password_before_writing_anything()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        Result<ProvisionedOwnerView> provisioned = await ProvisionAsync(
            harness,
            "acme",
            "owner@example.com",
            password: "short");

        Assert.True(provisioned.IsFailure);
        Assert.Equal("setup.weak_password", provisioned.Error!.Code);
        await AssertEmptyAsync(harness);
    }

    [Fact]
    public void Unrelated_write_failures_are_never_read_as_a_completed_bootstrap()
    {
        Assert.False(
            RelationalFirstOwnerProvisioningStore.IsContentionOrConstraint(
                new InvalidOperationException("boom")));
        Assert.False(
            RelationalFirstOwnerProvisioningStore.IsContentionOrConstraint(
                new DbUpdateException(
                    "boom",
                    new InvalidOperationException("inner"))));
        Assert.True(
            RelationalFirstOwnerProvisioningStore.IsContentionOrConstraint(
                new DbUpdateException(
                    "constraint",
                    new Microsoft.Data.Sqlite.SqliteException("unique", 19))));
    }

    private static ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
        AccountSurfaceHarness harness,
        string slug,
        string email,
        string password = Password,
        CancellationToken cancellationToken = default) =>
        ProvisionCoreAsync(harness, slug, email, password, cancellationToken);

    private static async ValueTask<Result<ProvisionedOwnerView>> ProvisionCoreAsync(
        AccountSurfaceHarness harness,
        string slug,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope =
            harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IFirstOwnerProvisioningPort>()
            .ProvisionAsync(
                new FirstOwnerProvisioningCommand(slug, slug, email, "Owner", password),
                cancellationToken);
    }

    private static async Task AssertEmptyAsync(AccountSurfaceHarness harness)
    {
        await using VistaraDbContext read =
            harness.CreateContext(Guid.CreateVersion7());
        Assert.Equal(0, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal(0, await read.Users.CountAsync(default));
        Assert.Equal(0, await read.LocalIdentities.CountAsync(default));
        Assert.Equal(0, await read.LocalCredentials.CountAsync(default));
        await using IdentityCatalogDbContext catalog = harness.CreateCatalog();
        Assert.Equal(0, await catalog.Tenants.CountAsync(default));
        Assert.Equal(0, await catalog.TenantMemberships.CountAsync(default));
    }

    private static async Task AssertSingleOwnerAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        string slug)
    {
        await using IdentityCatalogDbContext catalog = harness.CreateCatalog();
        Assert.Equal(1, await catalog.Tenants.CountAsync(default));
        Assert.Equal(1, await catalog.Users.CountAsync(default));
        Assert.Equal(1, await catalog.LocalCredentials.CountAsync(default));
        Assert.Equal(1, await catalog.TenantMemberships.CountAsync(default));
        Assert.Equal(
            slug,
            await catalog.Tenants.Select(row => row.Slug).SingleAsync(default));
        await using VistaraDbContext read = harness.CreateContext(tenantId);
        Assert.Equal(1, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal(
            tenantId,
            await read.PlatformBootstrap
                .Select(row => row.OwnerTenantId)
                .SingleAsync(default));
    }

    private sealed class ThrowingProvisioningGuard : IFirstOwnerProvisioningGuard
    {
        private bool _armed = true;

        public bool Invoked { get; private set; }

        public void Disarm() => _armed = false;

        public ValueTask BeforeCommitAsync(CancellationToken cancellationToken)
        {
            if (!_armed)
            {
                return ValueTask.CompletedTask;
            }

            Invoked = true;
            throw new InvalidOperationException(
                "Injected provisioning failure before commit.");
        }
    }

    private sealed class CancellingProvisioningGuard(CancellationTokenSource source)
        : IFirstOwnerProvisioningGuard
    {
        public bool Invoked { get; private set; }

        public async ValueTask BeforeCommitAsync(CancellationToken cancellationToken)
        {
            Invoked = true;
            await source.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
