using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Admin;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// A policy patch must never turn an absent, and therefore unlimited, quota
/// into a hard zero. Zero means "nothing is allowed" to the reservation path,
/// so synthesizing it would silently stop every upload.
/// </summary>
public sealed class PolicyQuotaPreservationTests
{
    [Fact]
    public async Task An_absent_quota_reads_as_unlimited_rather_than_zero()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<TenantPolicyView> policy = await ReadAsync(harness, owner.TenantId);

        Assert.True(policy.TryGetValue(out TenantPolicyView? view));
        Assert.Null(view.StorageBytes);
        Assert.Null(view.DailyTransformPixels);
        Assert.Null(view.ConcurrentUploads);
    }

    [Fact]
    public async Task A_retention_only_patch_leaves_every_quota_absent()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<TenantPolicyView> updated = await PatchAsync(
            harness,
            owner,
            new TenantPolicyPatch(
                14,
                null,
                null,
                null,
                null,
                PatchValue.Absent<long?>(),
                PatchValue.Absent<long?>(),
                PatchValue.Absent<long?>()),
            1);

        Assert.True(updated.TryGetValue(out TenantPolicyView? view));
        Assert.Equal(14, view.TrashRetentionDays);
        Assert.Null(view.StorageBytes);
        Assert.Null(view.ConcurrentUploads);
        await using VistaraDbContext read = harness.CreateContext(owner.TenantId);
        TenantRow tenant = await read.Tenants.AsNoTracking().SingleAsync(default);
        Assert.DoesNotContain("storedBytes", tenant.QuotasJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "concurrentUploads",
            tenant.QuotasJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_upload_reservation_still_succeeds_after_a_retention_patch()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchAsync(
            harness,
            owner,
            new TenantPolicyPatch(
                14,
                null,
                null,
                null,
                null,
                PatchValue.Absent<long?>(),
                PatchValue.Absent<long?>(),
                PatchValue.Absent<long?>()),
            1);

        await using VistaraDbContext context = harness.CreateContext(owner.TenantId);
        var store = new RelationalUploadApplicationStore(
            context,
            new AccountSurfaceHarness.ReachableBlobStore(),
            Vistara.Application.Common.SystemClock.Instance,
            new Vistara.Application.Common.Uuid7Generator(
                Vistara.Application.Common.SystemClock.Instance),
            new UploadPersistenceOptions());
        long maximum =
            await store.GetMaximumUploadBytesAsync(owner.TenantId, default);

        Assert.Equal(new UploadPersistenceOptions().MaximumUploadBytes, maximum);
    }

    [Fact]
    public async Task An_explicit_null_removes_a_stored_quota()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchAsync(
            harness,
            owner,
            Quota(PatchValue.Of<long?>(5_000)),
            1);

        Result<TenantPolicyView> cleared = await PatchAsync(
            harness,
            owner,
            Quota(PatchValue.Of<long?>(null)),
            2);

        Assert.True(cleared.TryGetValue(out TenantPolicyView? view));
        Assert.Null(view.StorageBytes);
        await using VistaraDbContext read = harness.CreateContext(owner.TenantId);
        TenantRow tenant = await read.Tenants.AsNoTracking().SingleAsync(default);
        Assert.DoesNotContain("storedBytes", tenant.QuotasJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_quota_members_survive_a_patch()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await using (VistaraDbContext seed = harness.CreateContext(owner.TenantId))
        {
            TenantRow tenant = await seed.Tenants.SingleAsync(default);
            tenant.QuotasJson = """{"maximumUploadBytes":123456,"budgetUnits":9}""";
            tenant.Version++;
            await seed.SaveChangesAsync(default);
        }

        await PatchAsync(harness, owner, Quota(PatchValue.Of<long?>(77)), 2);

        await using VistaraDbContext read = harness.CreateContext(owner.TenantId);
        TenantRow persisted = await read.Tenants.AsNoTracking().SingleAsync(default);
        Assert.Contains(
            "maximumUploadBytes",
            persisted.QuotasJson,
            StringComparison.Ordinal);
        Assert.Contains("budgetUnits", persisted.QuotasJson, StringComparison.Ordinal);
        Assert.Contains("\"storedBytes\":77", persisted.QuotasJson, StringComparison.Ordinal);
    }

    private static TenantPolicyPatch Quota(PatchValue<long?> storageBytes) =>
        new(
            null,
            null,
            null,
            null,
            null,
            storageBytes,
            PatchValue.Absent<long?>(),
            PatchValue.Absent<long?>());

    private static async ValueTask<Result<TenantPolicyView>> ReadAsync(
        AccountSurfaceHarness harness,
        Guid tenantId)
    {
        await using AsyncServiceScope scope = harness.CreateTenantScope(tenantId);
        return await scope.ServiceProvider
            .GetRequiredService<IAdminPort>()
            .GetPolicyAsync(tenantId, default);
    }

    private static async ValueTask<Result<TenantPolicyView>> PatchAsync(
        AccountSurfaceHarness harness,
        ProvisionedOwnerView owner,
        TenantPolicyPatch patch,
        long expectedVersion)
    {
        await using AsyncServiceScope scope =
            harness.CreateTenantScope(owner.TenantId);
        return await scope.ServiceProvider
            .GetRequiredService<IAdminPort>()
            .UpdatePolicyAsync(
                owner.TenantId,
                owner.UserId,
                patch,
                expectedVersion,
                default);
    }
}
