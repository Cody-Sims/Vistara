using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Vistara.Application.Common.Storage;
using Vistara.Storage.S3;

namespace Vistara.Storage.ConformanceTests.S3;

public sealed class AwsS3TransportTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sdk_configuration_uses_one_bounded_standard_retry_layer()
    {
        S3ValidatedOptions options = MinioOptions().Validate();

        AmazonS3Config config = AwsS3Transport.CreateConfig(options);

        Assert.Equal(RequestRetryMode.Standard, config.RetryMode);
        Assert.Equal(2, config.MaxErrorRetry);
        Assert.Equal(RequestChecksumCalculation.WHEN_REQUIRED, config.RequestChecksumCalculation);
        Assert.Equal(ResponseChecksumValidation.WHEN_REQUIRED, config.ResponseChecksumValidation);
        Assert.Equal(options.ServiceUrl!.AbsoluteUri, config.ServiceURL);
        Assert.Equal(options.Region, config.AuthenticationRegion);
        Assert.True(config.ForcePathStyle);
    }

    [Fact]
    public async Task Aws_sdk_presigner_preserves_exact_path_method_and_required_headers()
    {
        await using S3BlobStore store = new(
            MinioOptions(),
            new BasicAWSCredentials("test-access-key", "test-secret-key"),
            new FixedTimeProvider(Now));
        BlobKey key = new("staging/01/tenant/exact-key.jpg");
        BlobChecksum checksum = new(BlobChecksumAlgorithm.Sha256, new string('c', 64));

        DirectUploadPlan plan = await store.CreateDirectUploadAsync(
            new DirectUploadRequest(
                key,
                12,
                new BlobMediaType("image/jpeg"),
                checksum,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(10),
                BlobMetadata.Empty),
            CancellationToken.None);

        Assert.Equal(HttpMethodKind.Put, plan.Request.Method);
        Assert.Equal(
            $"/vistara-test/{key.Value}",
            Uri.UnescapeDataString(plan.Request.Url.AbsolutePath));
        Assert.Contains(
            "content-length%3Bcontent-type%3Bhost%3Bif-none-match%3Bx-amz-checksum-sha256",
            plan.Request.Url.Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "test-secret-key",
            plan.Request.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aws_sdk_presigner_signs_the_requested_read_range()
    {
        await using S3BlobStore store = new(
            MinioOptions(),
            new BasicAWSCredentials("test-access-key", "test-secret-key"),
            new FixedTimeProvider(Now));
        BlobKey key = new("originals/01/exact-key.jpg");

        SignedAccessPlan plan = await store.CreateReadGrantAsync(
            key,
            new ReadGrantOptions(
                TimeSpan.FromMinutes(10),
                new BlobRange(20, 10)),
            CancellationToken.None);

        Assert.Equal("bytes=20-29", plan.Request.Headers["Range"]);
        Assert.Contains(
            "X-Amz-SignedHeaders=host%3Brange",
            plan.Request.Url.Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            $"/vistara-test/{key.Value}",
            Uri.UnescapeDataString(plan.Request.Url.AbsolutePath));
    }

    [Fact]
    public async Task Aws_sdk_presigner_emits_one_exact_multipart_part_scope()
    {
        S3ValidatedOptions options = MinioOptions().Validate();
        await using AwsS3Transport transport = AwsS3Transport.Create(
            options,
            new BasicAWSCredentials("test-access-key", "test-secret-key"));

        Uri url = await transport.PresignAsync(
            new S3PresignCommand(
                HttpMethodKind.Put,
                "staging/01/multipart",
                Now.AddMinutes(10),
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                "upload-id",
                7),
            CancellationToken.None);

        string query = Uri.UnescapeDataString(url.Query);
        Assert.Equal(1, CountOccurrences(query, "partNumber=7"));
        Assert.Equal(1, CountOccurrences(query, "uploadId=upload-id"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", "NotFound")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchUpload", "InvalidRequest")]
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "PreconditionFailed")]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange", "InvalidRange")]
    [InlineData(HttpStatusCode.BadRequest, "BadDigest", "IntegrityMismatch")]
    [InlineData(HttpStatusCode.NotImplemented, "NotImplemented", "Unsupported")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", "OutcomeUnknown")]
    public void Aws_sdk_errors_are_classified_without_inspecting_response_bodies(
        HttpStatusCode statusCode,
        string errorCode,
        string expected)
    {
        AmazonS3Exception exception = new("provider detail")
        {
            StatusCode = statusCode,
            ErrorCode = errorCode,
        };

        Assert.Equal(expected, AwsS3Transport.Classify(exception).ToString());
    }

    [Fact]
    public void No_such_upload_after_completion_is_treated_as_ambiguous()
    {
        AmazonS3Exception exception = new("provider detail")
        {
            StatusCode = HttpStatusCode.NotFound,
            ErrorCode = "NoSuchUpload",
        };

        Assert.Equal(
            "OutcomeUnknown",
            AwsS3Transport.ClassifyCompletion(exception).ToString());
    }

    private static S3BlobStoreOptions MinioOptions() =>
        new(S3ProviderKind.Minio, "vistara-test", "us-east-1")
        {
            ServiceUrl = new Uri("https://minio.example"),
            ForcePathStyle = true,
            AllowedEndpointHosts = ["minio.example"],
        };

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
