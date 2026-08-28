namespace Vistara.Domain.Assets;

public sealed class AssetRevision
{
    public AssetRevision(
        Guid id,
        Guid tenantId,
        Guid assetId,
        long revisionNumber,
        BlobObjectMetadata original,
        MediaDescriptor media,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty || id.Version != 7)
        {
            throw new ArgumentException("Revision ID must be UUIDv7.", nameof(id));
        }

        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("Tenant ID must be UUIDv7.", nameof(tenantId));
        }

        if (assetId == Guid.Empty || assetId.Version != 7)
        {
            throw new ArgumentException("Asset ID must be UUIDv7.", nameof(assetId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revisionNumber);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(media);
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(createdAtUtc));
        }

        if (original.TenantId != tenantId)
        {
            throw new ArgumentException(
                "Original blob and revision must belong to the same tenant.",
                nameof(original));
        }

        Id = id;
        TenantId = tenantId;
        AssetId = assetId;
        RevisionNumber = revisionNumber;
        Original = original;
        Media = media;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid AssetId { get; }

    public long RevisionNumber { get; }

    public BlobObjectMetadata Original { get; }

    public MediaDescriptor Media { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}
