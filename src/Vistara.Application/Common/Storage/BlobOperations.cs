namespace Vistara.Application.Common.Storage;

public sealed record BlobRequestConditions
{
    public BlobRequestConditions(
        BlobVersion? ifMatch = null,
        bool requireMissing = false,
        BlobEntityTag? ifEntityTagMatch = null)
    {
        if ((ifMatch is not null || ifEntityTagMatch is not null) && requireMissing)
        {
            throw new ArgumentException(
                "A request cannot require both a matching version and a missing object.");
        }

        IfMatch = ifMatch;
        IfEntityTagMatch = ifEntityTagMatch;
        RequireMissing = requireMissing;
    }

    public static BlobRequestConditions None { get; } = new();

    public static BlobRequestConditions CreateOnly { get; } =
        new(requireMissing: true);

    public BlobVersion? IfMatch { get; }

    public BlobEntityTag? IfEntityTagMatch { get; }

    public bool RequireMissing { get; }

    public bool HasPrecondition =>
        IfMatch is not null || IfEntityTagMatch is not null || RequireMissing;
}

public sealed record BlobRange
{
    public BlobRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _ = checked(offset + length);
        Offset = offset;
        Length = length;
    }

    public long Offset { get; }

    public long Length { get; }
}

public sealed record BlobContentRange
{
    public BlobContentRange(long offset, long length, long totalLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLength);
        if (checked(offset + length) > totalLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The content range exceeds the object length.");
        }

        Offset = offset;
        Length = length;
        TotalLength = totalLength;
    }

    public long Offset { get; }

    public long Length { get; }

    public long TotalLength { get; }
}

public sealed record BlobReadOptions(
    BlobRange? Range = null,
    BlobRequestConditions? Conditions = null)
{
    public static BlobReadOptions Full { get; } = new();

    public BlobRequestConditions EffectiveConditions =>
        Conditions ?? BlobRequestConditions.None;
}

public sealed class BlobWriteOptions
{
    private readonly IReadOnlyList<BlobChecksum> _checksums;

    public BlobWriteOptions(
        BlobMediaType? contentType = null,
        BlobMetadata? metadata = null,
        IEnumerable<BlobChecksum>? checksums = null,
        BlobRequestConditions? conditions = null)
    {
        BlobChecksum[] checksumCopy = checksums?.ToArray() ?? [];
        if (checksumCopy.Select(checksum => checksum.Algorithm).Distinct().Count() !=
            checksumCopy.Length)
        {
            throw new ArgumentException(
                "Checksum algorithms must be unique.",
                nameof(checksums));
        }

        ContentType = contentType;
        Metadata = metadata ?? BlobMetadata.Empty;
        _checksums = Array.AsReadOnly(checksumCopy);
        Conditions = conditions ?? BlobRequestConditions.None;
    }

    public static BlobWriteOptions None { get; } = new();

    public BlobMediaType? ContentType { get; }

    public BlobMetadata Metadata { get; }

    public IReadOnlyList<BlobChecksum> Checksums => _checksums;

    public BlobRequestConditions Conditions { get; }
}

public sealed record BlobCopyOptions(
    BlobRequestConditions? SourceConditions = null,
    BlobRequestConditions? DestinationConditions = null,
    BlobMetadata? ReplacementMetadata = null)
{
    public static BlobCopyOptions None { get; } = new();

    public BlobRequestConditions EffectiveSourceConditions =>
        SourceConditions ?? BlobRequestConditions.None;

    public BlobRequestConditions EffectiveDestinationConditions =>
        DestinationConditions ?? BlobRequestConditions.None;
}

public sealed record BlobDeleteOptions(BlobRequestConditions? Conditions = null)
{
    public static BlobDeleteOptions None { get; } = new();

    public BlobRequestConditions EffectiveConditions =>
        Conditions ?? BlobRequestConditions.None;
}

public sealed record BlobListOptions(
    string? Prefix = null,
    bool IncludeVersions = false)
{
    public static BlobListOptions All { get; } = new();
}

public sealed record BlobWriteResult(BlobHead Head, bool Created);

public sealed record BlobCopyResult(BlobHead Head, BlobIdentity Source);

public sealed record BlobDeleteResult(bool Deleted, BlobIdentity? DeletedIdentity);

public sealed class BlobReadHandle : IAsyncDisposable
{
    public BlobReadHandle(
        Stream content,
        BlobHead head,
        BlobContentRange? contentRange = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(head);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        Content = content;
        Head = head;
        ContentRange = contentRange;
    }

    public Stream Content { get; }

    public BlobHead Head { get; }

    public BlobContentRange? ContentRange { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IReplayableBlobContent
{
    long Length { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}
