namespace Vistara.Domain.Uploads;

public sealed class UploadPart
{
    public UploadPart(
        int partNumber,
        string entityTag,
        string? checksum,
        long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityTag);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        PartNumber = partNumber;
        EntityTag = entityTag.Trim();
        Checksum = string.IsNullOrWhiteSpace(checksum) ? null : checksum.Trim();
        SizeBytes = sizeBytes;
    }

    public int PartNumber { get; }

    public string EntityTag { get; }

    public string? Checksum { get; }

    public long SizeBytes { get; }
}
