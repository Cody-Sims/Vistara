using Vistara.Application.Common.Storage;

namespace Vistara.Storage.S3;

public enum S3ProviderKind
{
    Aws,
    CloudflareR2,
    BackblazeB2,
    Minio,
}

public sealed record S3ProviderProfile
{
    internal S3ProviderProfile(
        S3ProviderKind kind,
        string name,
        BlobStoreCapabilities capabilities,
        long maxSinglePutBytes,
        bool requiresUniformMultipartParts,
        bool supportsConditionalCopySource,
        IReadOnlyList<BlobChecksumAlgorithm>
            multipartFullObjectChecksumAlgorithms)
    {
        Kind = kind;
        Name = name;
        Capabilities = capabilities;
        MaxSinglePutBytes = maxSinglePutBytes;
        RequiresUniformMultipartParts = requiresUniformMultipartParts;
        SupportsConditionalCopySource = supportsConditionalCopySource;
        MultipartFullObjectChecksumAlgorithms =
            Array.AsReadOnly(
                multipartFullObjectChecksumAlgorithms.Distinct().ToArray());
    }

    public S3ProviderKind Kind { get; }

    public string Name { get; }

    public BlobStoreCapabilities Capabilities { get; }

    public long MaxSinglePutBytes { get; }

    public bool RequiresUniformMultipartParts { get; }

    public bool SupportsConditionalCopySource { get; }

    public IReadOnlyList<BlobChecksumAlgorithm> MultipartFullObjectChecksumAlgorithms
    {
        get;
    }
}

public static class S3ProviderProfiles
{
    private const long MinPartBytes = 5L * 1024 * 1024;
    private const long MaxPartBytes = 5L * 1024 * 1024 * 1024;
    private const long StandardMaxObjectBytes = MaxPartBytes * 10_000;
    private const long R2MaxObjectBytes = 5L * 1024 * 1024 * 1024 * 1024;
    private const long B2MaxObjectBytes = 10_000_000_000_000;

    private static readonly S3ProviderProfile Aws = Create(
        S3ProviderKind.Aws,
        "aws-s3",
        conditionalWrites: true,
        conditionalDelete: true,
        conditionalMultipartCompletion: true,
        objectVersioning: false,
        maxObjectBytes: StandardMaxObjectBytes,
        maxSinglePutBytes: MaxPartBytes,
        requiresUniformMultipartParts: false,
        supportsConditionalCopySource: true,
        listConsistency: BlobConsistencyModel.Strong,
        multipartFullObjectChecksums: [],
        [BlobChecksumAlgorithm.Sha256]);

    private static readonly S3ProviderProfile R2 = Create(
        S3ProviderKind.CloudflareR2,
        "cloudflare-r2",
        conditionalWrites: true,
        conditionalDelete: false,
        conditionalMultipartCompletion: false,
        objectVersioning: false,
        maxObjectBytes: R2MaxObjectBytes,
        maxSinglePutBytes: MaxPartBytes,
        requiresUniformMultipartParts: true,
        supportsConditionalCopySource: true,
        listConsistency: BlobConsistencyModel.Strong,
        multipartFullObjectChecksums: [BlobChecksumAlgorithm.Crc64Nvme],
        [BlobChecksumAlgorithm.Crc64Nvme]);

    private static readonly S3ProviderProfile B2 = Create(
        S3ProviderKind.BackblazeB2,
        "backblaze-b2",
        conditionalWrites: false,
        conditionalDelete: false,
        conditionalMultipartCompletion: false,
        objectVersioning: false,
        maxObjectBytes: B2MaxObjectBytes,
        maxSinglePutBytes: MaxPartBytes,
        requiresUniformMultipartParts: false,
        supportsConditionalCopySource: false,
        listConsistency: BlobConsistencyModel.Eventual,
        multipartFullObjectChecksums: [],
        [BlobChecksumAlgorithm.Md5]);

    private static readonly S3ProviderProfile Minio = Create(
        S3ProviderKind.Minio,
        "minio",
        conditionalWrites: true,
        conditionalDelete: true,
        conditionalMultipartCompletion: false,
        objectVersioning: false,
        maxObjectBytes: StandardMaxObjectBytes,
        maxSinglePutBytes: MaxPartBytes,
        requiresUniformMultipartParts: false,
        supportsConditionalCopySource: true,
        listConsistency: BlobConsistencyModel.Strong,
        multipartFullObjectChecksums: [],
        [BlobChecksumAlgorithm.Sha256]);

    public static S3ProviderProfile Get(S3ProviderKind kind) =>
        kind switch
        {
            S3ProviderKind.Aws => Aws,
            S3ProviderKind.CloudflareR2 => R2,
            S3ProviderKind.BackblazeB2 => B2,
            S3ProviderKind.Minio => Minio,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static S3ProviderProfile Create(
        S3ProviderKind kind,
        string name,
        bool conditionalWrites,
        bool conditionalDelete,
        bool conditionalMultipartCompletion,
        bool objectVersioning,
        long maxObjectBytes,
        long maxSinglePutBytes,
        bool requiresUniformMultipartParts,
        bool supportsConditionalCopySource,
        BlobConsistencyModel listConsistency,
        IReadOnlyList<BlobChecksumAlgorithm> multipartFullObjectChecksums,
        IReadOnlyList<BlobChecksumAlgorithm> checksums) =>
        new(
            kind,
            name,
            new BlobStoreCapabilities
            {
                SupportsDirectUpload = true,
                SupportsMultipartUpload = true,
                SupportsRangeReads = true,
                SupportsConditionalRead = true,
                SupportsConditionalCreate = conditionalWrites,
                SupportsConditionalReplace = conditionalWrites,
                SupportsConditionalCopy = false,
                SupportsConditionalDelete = conditionalDelete,
                SupportsConditionalMultipartCompletion =
                    conditionalMultipartCompletion,
                SupportsServerSideCopy = true,
                SupportsObjectVersioning = objectVersioning,
                SupportsSignedRead = true,
                ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
                ListAfterWriteConsistency = listConsistency,
                NativeChecksumAlgorithms = checksums,
                Limits = new BlobStoreLimits(
                    maxObjectBytes,
                    1_024,
                    10_000,
                    MinPartBytes,
                    MaxPartBytes),
            },
            maxSinglePutBytes,
            requiresUniformMultipartParts,
            supportsConditionalCopySource,
            multipartFullObjectChecksums);
}
