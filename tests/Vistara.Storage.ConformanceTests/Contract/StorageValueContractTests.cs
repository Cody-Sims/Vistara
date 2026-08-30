using Vistara.Application.Common.Storage;

namespace Vistara.Storage.ConformanceTests.Contract;

public sealed class StorageValueContractTests
{
    [Fact]
    public void Blob_keys_are_canonical_lowercase_ascii_values()
    {
        BlobKey key = new("originals/01/asset/revision/upload.jpg");

        Assert.Equal("originals/01/asset/revision/upload.jpg", key.Value);
        Assert.Throws<ArgumentException>(() => new BlobKey("/absolute"));
        Assert.Throws<ArgumentException>(() => new BlobKey("Originals/upper"));
        Assert.Throws<ArgumentException>(() => new BlobKey("originals/../escape"));
        Assert.Throws<ArgumentException>(() => new BlobKey("originals/ümlaut"));
    }

    [Fact]
    public void Blob_metadata_and_checksum_collections_are_defensively_copied()
    {
        Dictionary<string, string> values = new()
        {
            ["vistara-tenant"] = "tenant-01",
        };
        BlobMetadata metadata = new(values);
        BlobChecksum[] checksums =
        [
            new(BlobChecksumAlgorithm.Sha256, new string('a', 64)),
        ];
        BlobProperties properties = new(
            8,
            new BlobMediaType("IMAGE/JPEG"),
            new DateTimeOffset(2026, 8, 28, 20, 0, 0, TimeSpan.Zero),
            new BlobVersion("version-1"),
            new BlobEntityTag("etag-1"),
            checksums,
            metadata);

        values.Clear();
        checksums[0] = new BlobChecksum(BlobChecksumAlgorithm.Crc32C, "changed");

        Assert.Equal("tenant-01", properties.Metadata["vistara-tenant"]);
        Assert.Equal(BlobChecksumAlgorithm.Sha256, properties.Checksums[0].Algorithm);
        Assert.Equal("image/jpeg", properties.ContentType.Value);
    }

    [Fact]
    public void Conflicting_preconditions_are_rejected_instead_of_implicitly_prioritized()
    {
        Assert.Throws<ArgumentException>(
            () => new BlobRequestConditions(
                new BlobVersion("version-1"),
                requireMissing: true));
        Assert.Throws<ArgumentException>(
            () => new BlobRequestConditions(
                requireMissing: true,
                ifEntityTagMatch: new BlobEntityTag("etag-1")));
    }

    [Fact]
    public void Signed_request_debug_output_never_contains_bearer_urls_or_headers()
    {
        SignedHttpRequest request = new(
            HttpMethodKind.Put,
            new Uri("https://storage.invalid/object?signature=secret"),
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer secret",
            });

        string text = request.ToString();

        Assert.DoesNotContain("signature", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
    }
}
