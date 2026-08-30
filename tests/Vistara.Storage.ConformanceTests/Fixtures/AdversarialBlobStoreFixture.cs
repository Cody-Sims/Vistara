namespace Vistara.Storage.ConformanceTests.Fixtures;

public static class AdversarialBlobStoreFixture
{
    public static IBlobStoreFixture IgnoresPreconditions() =>
        InMemoryBlobStoreFixture.CreateAdversarial(
            InMemoryBlobStoreFault.IgnorePreconditions);

    public static IBlobStoreFixture ReusesContentStream() =>
        InMemoryBlobStoreFixture.CreateAdversarial(
            InMemoryBlobStoreFault.ReuseContentStream);

    public static IBlobStoreFixture ReturnsIncorrectRange() =>
        InMemoryBlobStoreFixture.CreateAdversarial(
            InMemoryBlobStoreFault.IncorrectRange);

    public static IBlobStoreFixture ReturnsIncorrectChecksum() =>
        InMemoryBlobStoreFixture.CreateAdversarial(
            InMemoryBlobStoreFault.IncorrectChecksum);

    public static IBlobStoreFixture FallsBackWhenUnsupported() =>
        InMemoryBlobStoreFixture.CreateAdversarial(
            InMemoryBlobStoreFault.FallbackWhenUnsupported,
            conditionalCreate: false);

    public static IBlobStoreFixture ReordersMultipartParts() =>
        InMemoryBlobStoreFixture.CreateAdversarial(
            InMemoryBlobStoreFault.ReorderMultipartParts);

    public static IBlobStoreFixture IgnoresCancellation() =>
        InMemoryBlobStoreFixture.CreateAdversarial(
            InMemoryBlobStoreFault.IgnoreCancellation);
}
