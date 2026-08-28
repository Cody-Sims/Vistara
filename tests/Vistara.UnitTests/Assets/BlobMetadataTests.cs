using Vistara.Domain.Assets;

namespace Vistara.UnitTests.Assets;

public sealed class BlobMetadataTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 28, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Assets_sha256_requires_exact_hex_and_normalizes_case()
    {
        Sha256Checksum checksum = new(new string('A', 64));

        Assert.Equal(new string('a', 64), checksum.Value);
        Assert.Throws<ArgumentException>(() => new Sha256Checksum(new string('a', 63)));
        Assert.Throws<ArgumentException>(() => new Sha256Checksum(new string('z', 64)));
    }

    [Fact]
    public void Assets_blob_dedupe_identity_is_scoped_by_tenant_checksum_and_size()
    {
        Guid tenantOne = Guid.Parse("0198ef6d-b620-7000-8000-000000000001");
        Guid tenantTwo = Guid.Parse("0198ef6d-b620-7000-8000-000000000002");
        Sha256Checksum checksum = new(new string('b', 64));

        TenantBlobDedupeIdentity first = new(tenantOne, checksum, 4096);
        TenantBlobDedupeIdentity same = new(tenantOne, new Sha256Checksum(new string('B', 64)), 4096);
        TenantBlobDedupeIdentity otherTenant = new(tenantTwo, checksum, 4096);
        TenantBlobDedupeIdentity otherSize = new(tenantOne, checksum, 4097);

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherTenant);
        Assert.NotEqual(first, otherSize);
    }

    [Fact]
    public void Assets_media_metadata_copies_safe_and_private_properties()
    {
        Dictionary<string, string> safe = new() { ["camera"] = "Vistara" };
        Dictionary<string, string> privateValues = new() { ["gps"] = "hidden" };
        MediaPrivacyMetadata metadata = new(safe, privateValues);

        safe["camera"] = "mutated";
        privateValues.Clear();

        Assert.Equal("Vistara", metadata.SafeProperties["camera"]);
        Assert.Equal("hidden", metadata.PrivateProperties["gps"]);
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, string>)metadata.SafeProperties).Add("new", "value"));
    }

    [Fact]
    public void Assets_blob_metadata_enforces_size_content_key_and_utc_invariants()
    {
        Guid tenantId = Guid.Parse("0198ef6d-b620-7000-8000-000000000001");
        Sha256Checksum checksum = new(new string('c', 64));

        BlobObjectMetadata blob = new(
            Guid.Parse("0198ef6d-b620-7000-8000-000000000003"),
            tenantId,
            "azure",
            "media",
            "originals/01/asset/revision/upload.jpg",
            "etag",
            checksum,
            "crc64",
            2048,
            new MediaContentType("IMAGE/JPEG"),
            Timestamp);

        Assert.Equal("image/jpeg", blob.ContentType.Value);
        Assert.Equal(new TenantBlobDedupeIdentity(tenantId, checksum, 2048), blob.DedupeIdentity);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlobObjectMetadata(
                blob.Id,
                tenantId,
                "azure",
                "media",
                blob.ObjectKey,
                "etag",
                checksum,
                null,
                0,
                blob.ContentType,
                Timestamp));
        Assert.Throws<ArgumentException>(
            () => new BlobObjectMetadata(
                blob.Id,
                tenantId,
                "azure",
                "media",
                "Originals/UPPERCASE",
                "etag",
                checksum,
                null,
                1,
                blob.ContentType,
                Timestamp));
    }
}
