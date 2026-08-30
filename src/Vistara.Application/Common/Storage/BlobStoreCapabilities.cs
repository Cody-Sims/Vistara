namespace Vistara.Application.Common.Storage;

public enum BlobConsistencyModel
{
    Unspecified,
    Eventual,
    Strong,
}

public sealed record BlobStoreLimits
{
    public BlobStoreLimits(
        long maxObjectBytes,
        int maxKeyBytes,
        int maxMultipartParts,
        long minMultipartPartBytes,
        long maxMultipartPartBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObjectBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxKeyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMultipartParts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minMultipartPartBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMultipartPartBytes);
        if (minMultipartPartBytes > maxMultipartPartBytes)
        {
            throw new ArgumentException(
                "The multipart minimum cannot exceed the multipart maximum.");
        }

        MaxObjectBytes = maxObjectBytes;
        MaxKeyBytes = maxKeyBytes;
        MaxMultipartParts = maxMultipartParts;
        MinMultipartPartBytes = minMultipartPartBytes;
        MaxMultipartPartBytes = maxMultipartPartBytes;
    }

    public static BlobStoreLimits Conservative { get; } =
        new(long.MaxValue, 1_024, 1, 1, long.MaxValue);

    public long MaxObjectBytes { get; }

    public int MaxKeyBytes { get; }

    public int MaxMultipartParts { get; }

    public long MinMultipartPartBytes { get; }

    public long MaxMultipartPartBytes { get; }
}

public sealed record BlobStoreCapabilities
{
    private IReadOnlyList<BlobChecksumAlgorithm> _nativeChecksumAlgorithms =
        Array.Empty<BlobChecksumAlgorithm>();

    public static BlobStoreCapabilities None { get; } = new();

    public bool SupportsDirectUpload { get; init; }

    public bool SupportsMultipartUpload { get; init; }

    public bool SupportsRangeReads { get; init; }

    public bool SupportsConditionalRead { get; init; }

    public bool SupportsConditionalCreate { get; init; }

    public bool SupportsConditionalReplace { get; init; }

    public bool SupportsConditionalCopy { get; init; }

    public bool SupportsConditionalDelete { get; init; }

    public bool SupportsConditionalMultipartCompletion { get; init; }

    public bool SupportsServerSideCopy { get; init; }

    public bool SupportsObjectVersioning { get; init; }

    public bool SupportsSignedRead { get; init; }

    public BlobConsistencyModel ReadAfterWriteConsistency { get; init; }

    public BlobConsistencyModel ListAfterWriteConsistency { get; init; }

    public IReadOnlyList<BlobChecksumAlgorithm> NativeChecksumAlgorithms
    {
        get => _nativeChecksumAlgorithms;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _nativeChecksumAlgorithms = Array.AsReadOnly(value.Distinct().ToArray());
        }
    }

    public BlobStoreLimits Limits { get; init; } = BlobStoreLimits.Conservative;
}
