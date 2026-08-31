using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Admin;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Vistara.Persistence.Administration;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Exercises the shipped administrative store over real persistence: storage
/// consumption, policy concurrency, and redacted audit paging.
/// </summary>
public sealed class AdminAdministrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Storage_usage_separates_originals_from_other_objects()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await SeedBlobsAsync(harness, owner.TenantId, owner.UserId);

        await using VistaraDbContext context = harness.CreateContext(owner.TenantId);
        PersistedStorageUsage usage = await new RelationalAdminStore(context)
            .ReadStorageUsageAsync(owner.TenantId, default);

        Assert.Equal(1_000, usage.OriginalBytes);
        Assert.Equal(1, usage.OriginalObjects);
        Assert.Equal(250, usage.DerivativeBytes);
        Assert.Equal(1, usage.DerivativeObjects);
    }

    [Fact]
    public async Task Storage_usage_never_leaves_the_tenant()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await SeedBlobsAsync(harness, owner.TenantId, owner.UserId);

        await using VistaraDbContext context = harness.CreateContext(owner.TenantId);
        var store = new RelationalAdminStore(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.ReadStorageUsageAsync(Guid.CreateVersion7(), default));
    }

    [Fact]
    public async Task Storage_usage_is_aggregated_in_the_database()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await SeedManyBlobsAsync(harness, owner.TenantId, owner.UserId, 400);

        await using VistaraDbContext context = harness.CreateContext(owner.TenantId);
        var store = new RelationalAdminStore(context);
        string sql = context.Blobs
            .Where(row => context.AssetRevisions.Any(
                revision => revision.BlobId == row.Id))
            .ToQueryString();
        PersistedStorageUsage usage =
            await store.ReadStorageUsageAsync(owner.TenantId, default);

        Assert.Equal(400, usage.OriginalObjects);
        Assert.Equal(400 * 10, usage.OriginalBytes);
        Assert.Equal(400, usage.DerivativeObjects);
        Assert.Equal(400 * 3, usage.DerivativeBytes);
        Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policies_default_when_the_tenant_stored_none()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<TenantPolicyView> policy = await ReadPolicyAsync(harness, owner.TenantId);

        Assert.True(policy.TryGetValue(out TenantPolicyView? view));
        Assert.Equal(30, view.TrashRetentionDays);
        Assert.Equal(7, view.PurgeGraceDays);
        Assert.True(view.PublicLinksEnabled);
        Assert.Null(view.StorageBytes);
        Assert.Equal(1, view.Version);
    }

    [Fact]
    public async Task A_policy_patch_persists_and_moves_the_tenant_version()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<TenantPolicyView> updated = await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(14, 3, false, 7, true, PatchValue.Of<long?>(5_000), PatchValue.Of<long?>(9_000), PatchValue.Of<long?>(6)),
            1);

        Assert.True(updated.TryGetValue(out TenantPolicyView? view));
        Assert.Equal(14, view.TrashRetentionDays);
        Assert.False(view.PublicLinksEnabled);
        Assert.Equal(5_000, view.StorageBytes);
        Assert.Equal(6, view.ConcurrentUploads);
        Assert.Equal(2, view.Version);
        Result<TenantPolicyView> reread = await ReadPolicyAsync(harness, owner.TenantId);
        Assert.True(reread.TryGetValue(out TenantPolicyView? persisted));
        Assert.Equal(14, persisted.TrashRetentionDays);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task A_policy_patch_preserves_unknown_stored_members()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await SetRawPolicyAsync(
            harness,
            owner.TenantId,
            """{"operatorNote":"keep me"}""",
            """{"maximumUploadBytes":123456,"storedBytes":10}""");

        await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(null, null, null, null, null, PatchValue.Of<long?>(42), PatchValue.Absent<long?>(), PatchValue.Absent<long?>()),
            2);

        await using VistaraDbContext read = harness.CreateContext(owner.TenantId);
        TenantRow tenant = await read.Tenants.AsNoTracking().SingleAsync(default);
        Assert.Contains("operatorNote", tenant.SettingsJson, StringComparison.Ordinal);
        Assert.Contains(
            "maximumUploadBytes",
            tenant.QuotasJson,
            StringComparison.Ordinal);
        Assert.Contains("\"storedBytes\":42", tenant.QuotasJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_policy_version_is_refused()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(14, null, null, null, null, PatchValue.Absent<long?>(), PatchValue.Absent<long?>(), PatchValue.Absent<long?>()),
            1);

        Result<TenantPolicyView> stale = await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(21, null, null, null, null, PatchValue.Absent<long?>(), PatchValue.Absent<long?>(), PatchValue.Absent<long?>()),
            1);

        Assert.True(stale.IsFailure);
        Assert.Equal("policies.version_conflict", stale.Error!.Code);
    }

    [Theory]
    [InlineData(0, null, null, "policies.invalid_duration")]
    [InlineData(null, null, -1L, "policies.invalid_quota")]
    [InlineData(null, 5_000, null, "policies.invalid_duration")]
    public async Task Invalid_policy_values_are_refused(
        int? retentionDays,
        int? linkLifetimeDays,
        long? storageBytes,
        string expectedCode)
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<TenantPolicyView> updated = await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(
                retentionDays,
                null,
                null,
                linkLifetimeDays,
                null,
                storageBytes is null
                    ? PatchValue.Absent<long?>()
                    : PatchValue.Of<long?>(storageBytes),
                PatchValue.Absent<long?>(),
                PatchValue.Absent<long?>()),
            1);

        Assert.True(updated.IsFailure);
        Assert.Equal(expectedCode, updated.Error!.Code);
    }

    [Fact]
    public async Task Audit_pages_newest_first_and_stays_inside_the_tenant()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(14, null, null, null, null, PatchValue.Absent<long?>(), PatchValue.Absent<long?>(), PatchValue.Absent<long?>()),
            1);
        await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(21, null, null, null, null, PatchValue.Absent<long?>(), PatchValue.Absent<long?>(), PatchValue.Absent<long?>()),
            2);

        AuditPage first = await ReadAuditAsync(harness, owner.TenantId, limit: 1);
        AuditPage second = await ReadAuditAsync(
            harness,
            owner.TenantId,
            limit: 1,
            after: first);

        Assert.Single(first.Items);
        Assert.NotNull(first.NextId);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.All(
            first.Items.Concat(second.Items),
            item => Assert.False(string.IsNullOrWhiteSpace(item.Action)));
    }

    [Fact]
    public async Task Audit_filters_by_action_and_outcome()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchPolicyAsync(
            harness,
            owner,
            new TenantPolicyPatch(14, null, null, null, null, PatchValue.Absent<long?>(), PatchValue.Absent<long?>(), PatchValue.Absent<long?>()),
            1);

        AuditPage matching = await ReadAuditAsync(
            harness,
            owner.TenantId,
            action: "tenant.policy.updated");
        AuditPage other = await ReadAuditAsync(
            harness,
            owner.TenantId,
            action: "tenant.member.invited");
        AuditPage rejected = await ReadAuditAsync(
            harness,
            owner.TenantId,
            outcome: "Rejected");

        Assert.Single(matching.Items);
        Assert.Empty(other.Items);
        Assert.Empty(rejected.Items);
    }

    private static async Task SeedBlobsAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        Guid ownerId)
    {
        Guid originalBlobId = Guid.CreateVersion7();
        Guid derivativeBlobId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        await using VistaraDbContext context = harness.CreateContext(tenantId);
        context.Blobs.Add(Blob(tenantId, originalBlobId, 1_000, 1));
        context.Blobs.Add(Blob(tenantId, derivativeBlobId, 250, 2));
        await context.SaveChangesAsync(default);
        context.Assets.Add(new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = ownerId,
            Status = "Ready",
            Visibility = "Private",
            Title = "Seed",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync(default);
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = originalBlobId,
            DetectedFormat = "Jpeg",
            DetectedContentType = "image/jpeg",
            Width = 10,
            Height = 10,
            FrameCount = 1,
            CreatedAtUtc = Now,
        });
        await context.SaveChangesAsync(default);
    }

    private static async Task SeedManyBlobsAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        Guid ownerId,
        int count)
    {
        await using VistaraDbContext context = harness.CreateContext(tenantId);
        var assetId = Guid.CreateVersion7();
        context.Assets.Add(new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = ownerId,
            Status = "Ready",
            Visibility = "Private",
            Title = "Seed",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        var originals = new List<Guid>(count);
        for (int index = 0; index < count; index++)
        {
            Guid originalId = Guid.CreateVersion7();
            originals.Add(originalId);
            context.Blobs.Add(Blob(tenantId, originalId, 10, index * 2));
            context.Blobs.Add(Blob(tenantId, Guid.CreateVersion7(), 3, (index * 2) + 1));
        }

        await context.SaveChangesAsync(default);
        for (int index = 0; index < count; index++)
        {
            context.AssetRevisions.Add(new AssetRevisionRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AssetId = assetId,
                RevisionNumber = index + 1,
                BlobId = originals[index],
                DetectedFormat = "Jpeg",
                DetectedContentType = "image/jpeg",
                Width = 10,
                Height = 10,
                FrameCount = 1,
                CreatedAtUtc = Now,
            });
        }

        await context.SaveChangesAsync(default);
    }

    private static BlobRow Blob(Guid tenantId, Guid id, long size, int seed = 0) => new()
    {
        Id = id,
        TenantId = tenantId,
        Provider = "local",
        Container = "media",
        ObjectKey = $"tenants/{tenantId:N}/{id:N}.bin",
        Sha256 = seed.ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
        SizeBytes = size,
        ContentType = "image/jpeg",
        State = "Active",
        CreatedAtUtc = Now,
    };

    private static async Task SetRawPolicyAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        string settingsJson,
        string quotasJson)
    {
        await using VistaraDbContext context = harness.CreateContext(tenantId);
        TenantRow tenant = await context.Tenants.SingleAsync(default);
        tenant.SettingsJson = settingsJson;
        tenant.QuotasJson = quotasJson;
        tenant.Version++;
        await context.SaveChangesAsync(default);
    }

    private static async ValueTask<Result<TenantPolicyView>> ReadPolicyAsync(
        AccountSurfaceHarness harness,
        Guid tenantId)
    {
        await using AsyncServiceScope scope = harness.CreateTenantScope(tenantId);
        return await scope.ServiceProvider
            .GetRequiredService<IAdminPort>()
            .GetPolicyAsync(tenantId, default);
    }

    private static async ValueTask<Result<TenantPolicyView>> PatchPolicyAsync(
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

    private static async ValueTask<AuditPage> ReadAuditAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        string? action = null,
        string? outcome = null,
        int limit = 50,
        AuditPage? after = null)
    {
        await using AsyncServiceScope scope = harness.CreateTenantScope(tenantId);
        return await scope.ServiceProvider
            .GetRequiredService<IAdminPort>()
            .ReadAuditAsync(
                new AuditQuery(
                    tenantId,
                    action,
                    outcome,
                    limit,
                    after?.NextOccurredAtUtc,
                    after?.NextId),
                default);
    }
}
