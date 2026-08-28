using Vistara.Domain.Assets;

namespace Vistara.Application.Assets;

public interface IAssetRepository
{
    ValueTask<Asset?> GetAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken);

    ValueTask AddAsync(Asset asset, CancellationToken cancellationToken);

    ValueTask SaveAsync(
        Asset asset,
        long expectedVersion,
        CancellationToken cancellationToken);
}

public interface IBlobMetadataRepository
{
    ValueTask<BlobObjectMetadata?> GetAsync(
        Guid tenantId,
        Guid blobId,
        CancellationToken cancellationToken);

    ValueTask<BlobObjectMetadata?> FindExactAsync(
        TenantBlobDedupeIdentity identity,
        CancellationToken cancellationToken);

    ValueTask AddAsync(
        BlobObjectMetadata blob,
        CancellationToken cancellationToken);
}
