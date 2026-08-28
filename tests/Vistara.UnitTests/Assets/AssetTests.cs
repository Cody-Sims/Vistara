using Vistara.Domain.Assets;

namespace Vistara.UnitTests.Assets;

public sealed class AssetTests
{
    private static readonly Guid TenantId = Guid.Parse("0198ef6d-b620-7000-8000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("0198ef6d-b620-7000-8000-000000000002");
    private static readonly Guid AssetId = Guid.Parse("0198ef6d-b620-7000-8000-000000000003");
    private static readonly Guid OwnerId = Guid.Parse("0198ef6d-b620-7000-8000-000000000004");
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Assets_revisions_are_monotonic_tenant_scoped_and_immutable()
    {
        Asset asset = Asset.Create(
            AssetId,
            TenantId,
            OwnerId,
            "Original",
            AssetVisibility.Private,
            CreatedAt);
        AssetRevision first = CreateRevision(TenantId, 1, "01");

        Assert.True(asset.AddRevision(first, CreatedAt.AddMinutes(1)).IsSuccess);

        AssetRevision replacement = CreateRevision(TenantId, 1, "02");
        var duplicate = asset.AddRevision(replacement, CreatedAt.AddMinutes(2));
        AssetRevision wrongTenant = CreateRevision(OtherTenantId, 2, "03");
        var tenantMismatch = asset.AddRevision(wrongTenant, CreatedAt.AddMinutes(2));

        Assert.True(duplicate.IsFailure);
        Assert.Equal("assets.revision_out_of_sequence", duplicate.Error?.Code);
        Assert.True(tenantMismatch.IsFailure);
        Assert.Equal("assets.tenant_mismatch", tenantMismatch.Error?.Code);
        Assert.Same(first, asset.CurrentRevision);
        Assert.Equal(2, asset.Version);
        Assert.Equal(CreatedAt.AddMinutes(1), asset.UpdatedAtUtc);
        Assert.All(
            typeof(AssetRevision).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }

    [Fact]
    public void Assets_require_utc_times_and_increment_version_only_for_changes()
    {
        Asset asset = Asset.Create(
            AssetId,
            TenantId,
            OwnerId,
            "Original",
            AssetVisibility.Private,
            CreatedAt);

        var unchanged = asset.UpdateMetadata(
            "Original",
            null,
            AssetVisibility.Private,
            expectedVersion: 1,
            CreatedAt.AddMinutes(1));

        Assert.True(unchanged.IsSuccess);
        Assert.Equal(1, asset.Version);

        var changed = asset.UpdateMetadata(
            "Renamed",
            "Description",
            AssetVisibility.Tenant,
            expectedVersion: 1,
            CreatedAt.AddMinutes(2));

        Assert.True(changed.IsSuccess);
        Assert.Equal(2, asset.Version);
        Assert.Equal(CreatedAt.AddMinutes(2), asset.UpdatedAtUtc);
        Assert.Throws<ArgumentException>(
            () => Asset.Create(
                AssetId,
                TenantId,
                OwnerId,
                "Original",
                AssetVisibility.Private,
                CreatedAt.ToOffset(TimeSpan.FromHours(-7))));
        Assert.Throws<ArgumentException>(
            () => Asset.Create(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                TenantId,
                OwnerId,
                "Original",
                AssetVisibility.Private,
                CreatedAt));
    }

    [Fact]
    public void Assets_reject_undefined_visibility_on_create_and_update()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Asset.Create(
                AssetId,
                TenantId,
                OwnerId,
                "Original",
                (AssetVisibility)999,
                CreatedAt));

        Asset asset = Asset.Create(
            AssetId,
            TenantId,
            OwnerId,
            "Original",
            AssetVisibility.Private,
            CreatedAt);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => asset.UpdateMetadata(
                "Original",
                null,
                (AssetVisibility)999,
                asset.Version,
                CreatedAt.AddMinutes(1)));
        Assert.Equal(AssetVisibility.Private, asset.Visibility);
        Assert.Equal(1, asset.Version);
    }

    private static AssetRevision CreateRevision(Guid tenantId, long revision, string checksumSuffix)
    {
        BlobObjectMetadata blob = new(
            Guid.Parse($"0198ef6d-b620-7000-8000-0000000000{checksumSuffix}"),
            tenantId,
            "s3",
            "media",
            $"originals/01/{tenantId:N}/{AssetId:N}/{revision}/upload.jpg",
            "version-1",
            new Sha256Checksum(new string('a', 62) + checksumSuffix),
            providerChecksum: "provider-checksum",
            sizeBytes: 1024,
            new MediaContentType("image/jpeg"),
            CreatedAt);
        MediaDescriptor media = new(
            "jpeg",
            new MediaContentType("image/jpeg"),
            new PixelDimensions(1920, 1080),
            frameCount: 1,
            new MediaPrivacyMetadata(
                new Dictionary<string, string> { ["camera"] = "Vistara" },
                new Dictionary<string, string> { ["gps"] = "private" }));

        return new AssetRevision(
            Guid.Parse($"0198ef6d-b620-7000-8000-0000000001{checksumSuffix}"),
            tenantId,
            AssetId,
            revision,
            blob,
            media,
            CreatedAt);
    }
}
