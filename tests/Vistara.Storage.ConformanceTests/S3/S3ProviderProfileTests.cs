using Vistara.Application.Common.Storage;
using Vistara.Storage.S3;

namespace Vistara.Storage.ConformanceTests.S3;

public sealed class S3ProviderProfileTests
{
    [Theory]
    [InlineData(S3ProviderKind.Aws, "aws-s3")]
    [InlineData(S3ProviderKind.CloudflareR2, "cloudflare-r2")]
    [InlineData(S3ProviderKind.BackblazeB2, "backblaze-b2")]
    [InlineData(S3ProviderKind.Minio, "minio")]
    public void Profiles_are_explicit_immutable_and_named(
        S3ProviderKind kind,
        string expectedName)
    {
        S3ProviderProfile profile = S3ProviderProfiles.Get(kind);

        Assert.Equal(expectedName, profile.Name);
        Assert.Same(profile, S3ProviderProfiles.Get(kind));
        Assert.Equal(BlobConsistencyModel.Strong, profile.Capabilities.ReadAfterWriteConsistency);
    }

    [Fact]
    public void Profiles_report_only_documented_conditional_and_checksum_support()
    {
        S3ProviderProfile aws = S3ProviderProfiles.Get(S3ProviderKind.Aws);
        S3ProviderProfile r2 = S3ProviderProfiles.Get(S3ProviderKind.CloudflareR2);
        S3ProviderProfile b2 = S3ProviderProfiles.Get(S3ProviderKind.BackblazeB2);
        S3ProviderProfile minio = S3ProviderProfiles.Get(S3ProviderKind.Minio);

        Assert.True(aws.Capabilities.SupportsConditionalCreate);
        Assert.True(aws.Capabilities.SupportsConditionalMultipartCompletion);
        Assert.Contains(BlobChecksumAlgorithm.Sha256, aws.Capabilities.NativeChecksumAlgorithms);

        Assert.False(r2.Capabilities.SupportsConditionalMultipartCompletion);
        Assert.True(r2.Capabilities.SupportsConditionalCreate);
        Assert.True(r2.RequiresUniformMultipartParts);
        Assert.Equal(5L * 1024 * 1024 * 1024 * 1024, r2.Capabilities.Limits.MaxObjectBytes);
        Assert.Equal(
            [BlobChecksumAlgorithm.Crc64Nvme],
            r2.Capabilities.NativeChecksumAlgorithms);

        Assert.False(b2.Capabilities.SupportsConditionalCreate);
        Assert.False(b2.Capabilities.SupportsConditionalDelete);
        Assert.Equal(10_000_000_000_000, b2.Capabilities.Limits.MaxObjectBytes);
        Assert.Equal(
            BlobConsistencyModel.Eventual,
            b2.Capabilities.ListAfterWriteConsistency);
        Assert.Equal(
            [BlobChecksumAlgorithm.Md5],
            b2.Capabilities.NativeChecksumAlgorithms);

        Assert.True(minio.Capabilities.SupportsConditionalCreate);
        Assert.False(minio.Capabilities.SupportsConditionalMultipartCompletion);
        Assert.False(aws.Capabilities.SupportsObjectVersioning);
        Assert.False(minio.Capabilities.SupportsObjectVersioning);
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Unsafe_or_profile_inconsistent_configuration_is_rejected(
        S3BlobStoreOptions options)
    {
        S3ConfigurationException error = Assert.Throws<S3ConfigurationException>(
            options.Validate);

        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<S3BlobStoreOptions> InvalidOptions() =>
        new()
        {
            new(S3ProviderKind.Aws, "bucket", "us-east-1")
            {
                ServiceUrl = new Uri("https://127.0.0.1:9000"),
            },
            new(S3ProviderKind.CloudflareR2, "bucket", "auto")
            {
                ServiceUrl = new Uri("https://example.com"),
            },
            new(S3ProviderKind.CloudflareR2, "bucket", "auto")
            {
                ServiceUrl = new Uri(
                    "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com:8443"),
            },
            new(S3ProviderKind.BackblazeB2, "bucket", "us-east-005")
            {
                ServiceUrl = new Uri("http://s3.us-east-005.backblazeb2.com"),
            },
            new(S3ProviderKind.Minio, "bucket", "us-east-1")
            {
                ServiceUrl = new Uri("http://127.0.0.1:9000"),
                AllowedEndpointHosts = ["127.0.0.1"],
            },
            new(S3ProviderKind.Minio, "bucket", "us-east-1")
            {
                ServiceUrl = new Uri("https://minio.example/object-prefix"),
                AllowedEndpointHosts = ["minio.example"],
            },
            new(S3ProviderKind.Minio, "bucket/key", "us-east-1")
            {
                ServiceUrl = new Uri("https://minio.example"),
                AllowedEndpointHosts = ["minio.example"],
            },
            new(S3ProviderKind.Minio, "127.0.0.1", "us-east-1")
            {
                ServiceUrl = new Uri("https://minio.example"),
                ForcePathStyle = true,
                AllowedEndpointHosts = ["minio.example"],
            },
            new(S3ProviderKind.Minio, "bucket", "us-east-1")
            {
                ServiceUrl = new Uri("https://127.0.0.1:9000"),
                AllowedEndpointHosts = ["127.0.0.1"],
            },
        };

    [Fact]
    public void Explicitly_allowlisted_local_minio_http_endpoint_is_valid()
    {
        S3BlobStoreOptions options = new(S3ProviderKind.Minio, "vistara-test", "us-east-1")
        {
            ServiceUrl = new Uri("http://127.0.0.1:9000"),
            ForcePathStyle = true,
            AllowInsecureHttp = true,
            AllowedEndpointHosts = ["127.0.0.1"],
        };

        S3ValidatedOptions validated = options.Validate();

        Assert.Equal(new Uri("http://127.0.0.1:9000"), validated.ServiceUrl);
        Assert.True(validated.ForcePathStyle);
    }
}
