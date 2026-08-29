using Vistara.Storage.ConformanceTests.Contract;

namespace Vistara.Storage.ConformanceTests.Local;

public sealed class LocalBlobStoreContractTests
{
    [Fact]
    public Task Local_conditional_operations_fail_without_mutation() =>
        BlobStoreContractRules.Conditional_operations_fail_without_mutation_async(
            LocalBlobStoreFixture.Create());

    [Fact]
    public Task Local_replayable_content_is_opened_per_operation() =>
        BlobStoreContractRules.Replayable_content_is_opened_per_operation_async(
            LocalBlobStoreFixture.Create());

    [Fact]
    public Task Local_range_and_checksum_results_are_exact() =>
        BlobStoreContractRules.Range_and_checksum_results_are_exact_async(
            LocalBlobStoreFixture.Create());

    [Fact]
    public Task Local_precancelled_tokens_cancel_every_io_operation() =>
        BlobStoreContractRules.Precancelled_tokens_cancel_every_io_operation_async(
            LocalBlobStoreFixture.Create());

    [Fact]
    public Task Local_listing_preserves_prefix_order_metadata_and_checksums() =>
        BlobStoreContractRules.Listing_preserves_prefix_order_metadata_and_checksums_async(
            LocalBlobStoreFixture.Create());
}
