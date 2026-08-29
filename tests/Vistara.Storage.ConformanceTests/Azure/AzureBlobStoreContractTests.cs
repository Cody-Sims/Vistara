using Vistara.Storage.ConformanceTests.Contract;

namespace Vistara.Storage.ConformanceTests.Azure;

public sealed class AzureBlobStoreContractTests
{
    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_conditional_operations_fail_without_mutation() =>
        BlobStoreContractRules.Conditional_operations_fail_without_mutation_async(
            AzureBlobStoreFixture.Create());

    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_replayable_content_is_opened_per_operation() =>
        BlobStoreContractRules.Replayable_content_is_opened_per_operation_async(
            AzureBlobStoreFixture.Create());

    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_range_and_checksum_results_are_exact() =>
        BlobStoreContractRules.Range_and_checksum_results_are_exact_async(
            AzureBlobStoreFixture.Create());

    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_precancelled_tokens_cancel_every_io_operation() =>
        BlobStoreContractRules.Precancelled_tokens_cancel_every_io_operation_async(
            AzureBlobStoreFixture.Create());

    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_listing_preserves_prefix_order_metadata_and_checksums() =>
        BlobStoreContractRules.Listing_preserves_prefix_order_metadata_and_checksums_async(
            AzureBlobStoreFixture.Create());

    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_direct_and_signed_read_plans_preserve_exact_scope() =>
        BlobStoreContractRules.Direct_and_signed_read_plans_preserve_exact_scope_async(
            AzureBlobStoreFixture.Create());

    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_multipart_plans_complete_and_abort_explicitly() =>
        BlobStoreContractRules.Multipart_plans_complete_and_abort_explicitly_async(
            AzureBlobStoreFixture.Create());

    [Fact]
    [Trait("Provider", "Azurite")]
    public Task Azurite_multipart_completion_requires_canonical_part_order() =>
        BlobStoreContractRules.Multipart_completion_requires_canonical_part_order_async(
            AzureBlobStoreFixture.Create());
}
