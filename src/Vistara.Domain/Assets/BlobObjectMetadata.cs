namespace Vistara.Domain.Assets;

public sealed record TenantBlobDedupeIdentity
{
    public TenantBlobDedupeIdentity(
        Guid tenantId,
        Sha256Checksum sha256,
        long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("Tenant ID must be UUIDv7.", nameof(tenantId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        TenantId = tenantId;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    public Guid TenantId { get; }

    public Sha256Checksum Sha256 { get; }

    public long SizeBytes { get; }
}

public sealed class BlobObjectMetadata
{
    public BlobObjectMetadata(
        Guid id,
        Guid tenantId,
        string provider,
        string container,
        string objectKey,
        string? providerVersion,
        Sha256Checksum sha256,
        string? providerChecksum,
        long sizeBytes,
        MediaContentType contentType,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty || id.Version != 7)
        {
            throw new ArgumentException("Blob ID must be UUIDv7.", nameof(id));
        }

        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("Tenant ID must be UUIDv7.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentNullException.ThrowIfNull(contentType);
        EnsureUtc(createdAtUtc, nameof(createdAtUtc));

        if (objectKey.Any(character => character is >= 'A' and <= 'Z') ||
            objectKey.Any(character => character > 127))
        {
            throw new ArgumentException(
                "Object keys must contain lowercase ASCII characters only.",
                nameof(objectKey));
        }

        Id = id;
        TenantId = tenantId;
        Provider = provider.Trim();
        Container = container.Trim();
        ObjectKey = objectKey.Trim();
        ProviderVersion = NormalizeOptional(providerVersion);
        Sha256 = sha256;
        ProviderChecksum = NormalizeOptional(providerChecksum);
        SizeBytes = sizeBytes;
        ContentType = contentType;
        CreatedAtUtc = createdAtUtc;
        DedupeIdentity = new TenantBlobDedupeIdentity(tenantId, sha256, sizeBytes);
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public string Provider { get; }

    public string Container { get; }

    public string ObjectKey { get; }

    public string? ProviderVersion { get; }

    public Sha256Checksum Sha256 { get; }

    public string? ProviderChecksum { get; }

    public long SizeBytes { get; }

    public MediaContentType ContentType { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public TenantBlobDedupeIdentity DedupeIdentity { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}
