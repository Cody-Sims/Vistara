using Vistara.Storage.ConformanceTests.Contract;
using Vistara.Storage.S3;

namespace Vistara.Storage.ConformanceTests.S3;

public sealed class S3BlobStoreContractTests
{
    [Fact]
    public Task Aws_replayable_content_is_opened_per_operation() =>
        BlobStoreContractRules.Replayable_content_is_opened_per_operation_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Aws));

    [Fact]
    public Task Minio_replayable_content_is_opened_per_operation() =>
        BlobStoreContractRules.Replayable_content_is_opened_per_operation_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Minio));

    [Fact]
    public Task Aws_range_and_checksum_results_are_exact() =>
        BlobStoreContractRules.Range_and_checksum_results_are_exact_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Aws));

    [Fact]
    public Task Minio_range_and_checksum_results_are_exact() =>
        BlobStoreContractRules.Range_and_checksum_results_are_exact_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Minio));

    [Fact]
    public Task Aws_precancelled_tokens_cancel_every_io_operation() =>
        BlobStoreContractRules.Precancelled_tokens_cancel_every_io_operation_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Aws));

    [Fact]
    public Task Minio_precancelled_tokens_cancel_every_io_operation() =>
        BlobStoreContractRules.Precancelled_tokens_cancel_every_io_operation_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Minio));

    [Fact]
    public Task Aws_listing_preserves_prefix_order_metadata_and_checksums() =>
        BlobStoreContractRules.Listing_preserves_prefix_order_metadata_and_checksums_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Aws));

    [Fact]
    public Task Minio_listing_preserves_prefix_order_metadata_and_checksums() =>
        BlobStoreContractRules.Listing_preserves_prefix_order_metadata_and_checksums_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Minio));

    [Fact]
    public Task Aws_direct_and_signed_read_plans_preserve_exact_scope() =>
        BlobStoreContractRules.Direct_and_signed_read_plans_preserve_exact_scope_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Aws));

    [Fact]
    public Task Minio_direct_and_signed_read_plans_preserve_exact_scope() =>
        BlobStoreContractRules.Direct_and_signed_read_plans_preserve_exact_scope_async(
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Minio));
}
