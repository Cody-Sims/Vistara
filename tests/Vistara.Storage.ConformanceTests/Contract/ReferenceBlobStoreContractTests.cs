using Vistara.Storage.ConformanceTests.Fixtures;

namespace Vistara.Storage.ConformanceTests.Contract;

public sealed class ReferenceBlobStoreContractTests
{
    [Fact]
    public Task Conditional_operations_fail_without_mutation() =>
        BlobStoreContractRules.Conditional_operations_fail_without_mutation_async(
            InMemoryBlobStoreFixture.Create());

    [Fact]
    public Task Replayable_content_is_opened_per_operation() =>
        BlobStoreContractRules.Replayable_content_is_opened_per_operation_async(
            InMemoryBlobStoreFixture.Create());

    [Fact]
    public Task Range_and_checksum_results_are_exact() =>
        BlobStoreContractRules.Range_and_checksum_results_are_exact_async(
            InMemoryBlobStoreFixture.Create());

    [Fact]
    public Task Unsupported_conditions_never_fall_back() =>
        BlobStoreContractRules.Unsupported_conditions_never_fall_back_async(
            InMemoryBlobStoreFixture.CreateWithoutConditionalCreate());

    [Fact]
    public Task Multipart_completion_requires_canonical_part_order() =>
        BlobStoreContractRules.Multipart_completion_requires_canonical_part_order_async(
            InMemoryBlobStoreFixture.Create());

    [Fact]
    public Task Precancelled_tokens_cancel_every_io_operation() =>
        BlobStoreContractRules.Precancelled_tokens_cancel_every_io_operation_async(
            InMemoryBlobStoreFixture.Create());

    [Fact]
    public Task Listing_preserves_prefix_order_metadata_and_checksums() =>
        BlobStoreContractRules.Listing_preserves_prefix_order_metadata_and_checksums_async(
            InMemoryBlobStoreFixture.Create());

    [Fact]
    public Task Direct_and_signed_read_plans_preserve_exact_scope() =>
        BlobStoreContractRules.Direct_and_signed_read_plans_preserve_exact_scope_async(
            InMemoryBlobStoreFixture.Create());

    [Fact]
    public Task Multipart_plans_complete_and_abort_explicitly() =>
        BlobStoreContractRules.Multipart_plans_complete_and_abort_explicitly_async(
            InMemoryBlobStoreFixture.Create());
}
