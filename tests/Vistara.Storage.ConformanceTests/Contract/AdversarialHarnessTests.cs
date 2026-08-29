using Vistara.Storage.ConformanceTests.Fixtures;
using Xunit.Sdk;

namespace Vistara.Storage.ConformanceTests.Contract;

public sealed class AdversarialHarnessTests
{
    [Fact]
    public Task Harness_rejects_ignored_preconditions() =>
        AssertContractFailsAsync(
            () => BlobStoreContractRules.Conditional_operations_fail_without_mutation_async(
                AdversarialBlobStoreFixture.IgnoresPreconditions()));

    [Fact]
    public Task Harness_rejects_non_replayable_content_misuse() =>
        AssertContractFailsAsync(
            () => BlobStoreContractRules.Replayable_content_is_opened_per_operation_async(
                AdversarialBlobStoreFixture.ReusesContentStream()));

    [Fact]
    public Task Harness_rejects_incorrect_ranges() =>
        AssertContractFailsAsync(
            () => BlobStoreContractRules.Range_and_checksum_results_are_exact_async(
                AdversarialBlobStoreFixture.ReturnsIncorrectRange()));

    [Fact]
    public Task Harness_rejects_incorrect_checksums() =>
        AssertContractFailsAsync(
            () => BlobStoreContractRules.Range_and_checksum_results_are_exact_async(
                AdversarialBlobStoreFixture.ReturnsIncorrectChecksum()));

    [Fact]
    public Task Harness_rejects_unsupported_operation_fallback() =>
        AssertContractFailsAsync(
            () => BlobStoreContractRules.Unsupported_conditions_never_fall_back_async(
                AdversarialBlobStoreFixture.FallsBackWhenUnsupported()));

    [Fact]
    public Task Harness_rejects_multipart_reordering() =>
        AssertContractFailsAsync(
            () => BlobStoreContractRules.Multipart_completion_requires_canonical_part_order_async(
                AdversarialBlobStoreFixture.ReordersMultipartParts()));

    [Fact]
    public Task Harness_rejects_ignored_cancellation() =>
        AssertContractFailsAsync(
            () => BlobStoreContractRules.Precancelled_tokens_cancel_every_io_operation_async(
                AdversarialBlobStoreFixture.IgnoresCancellation()));

    private static async Task AssertContractFailsAsync(Func<Task> contract)
    {
        Exception error = await Assert.ThrowsAnyAsync<Exception>(contract);

        Assert.IsAssignableFrom<XunitException>(error);
    }
}
