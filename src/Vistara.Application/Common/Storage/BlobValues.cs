using System.Collections.ObjectModel;

namespace Vistara.Application.Common.Storage;

public sealed record BlobKey
{
    public BlobKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (normalized.Length > 1_024 ||
            normalized[0] == '/' ||
            normalized[^1] == '/' ||
            normalized.Contains("//", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "." or "..") ||
            normalized.Any(character =>
                character > 127 ||
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '/' or '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "Blob keys must be relative lowercase ASCII paths.",
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record BlobVersion
{
    public BlobVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Blob versions cannot exceed 1,024 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record BlobEntityTag
{
    public BlobEntityTag(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Entity tags cannot exceed 1,024 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record BlobMediaType
{
    public BlobMediaType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim().ToLowerInvariant();
        int separator = normalized.IndexOf('/');
        if (separator < 1 ||
            separator == normalized.Length - 1 ||
            normalized.Any(character =>
                character > 127 || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A valid media type is required.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum BlobChecksumAlgorithm
{
    Sha256,
    Md5,
    Crc32,
    Crc32C,
    Crc64Nvme,
    ProviderDefined,
}

public sealed record BlobChecksum
{
    public BlobChecksum(BlobChecksumAlgorithm algorithm, string value)
    {
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (algorithm == BlobChecksumAlgorithm.Sha256)
        {
            if (normalized.Length != 64 ||
                normalized.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ArgumentException(
                    "SHA-256 checksums must contain exactly 64 hexadecimal characters.",
                    nameof(value));
            }

            normalized = normalized.ToLowerInvariant();
        }

        Algorithm = algorithm;
        Value = normalized;
    }

    public BlobChecksumAlgorithm Algorithm { get; }

    public string Value { get; }
}

public sealed class BlobMetadata
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public BlobMetadata(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Dictionary<string, string> copy = new(StringComparer.Ordinal);
        foreach ((string key, string value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            string normalizedKey = key.Trim();
            if (normalizedKey.Length > 128 ||
                normalizedKey.Any(character =>
                    character > 127 ||
                    !(char.IsAsciiLetterLower(character) ||
                      char.IsAsciiDigit(character) ||
                      character is '-' or '_' or '.')))
            {
                throw new ArgumentException(
                    "Metadata keys must be lowercase ASCII identifiers.",
                    nameof(values));
            }

            if (value.Length > 2_048)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    "Metadata values cannot exceed 2,048 characters.");
            }

            if (!copy.TryAdd(normalizedKey, value))
            {
                throw new ArgumentException(
                    $"Duplicate metadata key '{normalizedKey}'.",
                    nameof(values));
            }
        }

        _values = new ReadOnlyDictionary<string, string>(copy);
    }

    public static BlobMetadata Empty { get; } =
        new(Array.Empty<KeyValuePair<string, string>>());

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public string this[string key] => _values[key];

    public bool TryGetValue(string key, out string? value) =>
        _values.TryGetValue(key, out value);

    public IReadOnlyDictionary<string, string> AsReadOnly() => _values;
}

public sealed record BlobIdentity
{
    public BlobIdentity(BlobKey key, BlobVersion version)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(version);
        Key = key;
        Version = version;
    }

    public BlobKey Key { get; }

    public BlobVersion Version { get; }
}

public sealed class BlobProperties
{
    private readonly IReadOnlyList<BlobChecksum> _checksums;

    public BlobProperties(
        long contentLength,
        BlobMediaType contentType,
        DateTimeOffset lastModifiedUtc,
        BlobVersion version,
        BlobEntityTag entityTag,
        IEnumerable<BlobChecksum> checksums,
        BlobMetadata metadata)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(entityTag);
        ArgumentNullException.ThrowIfNull(checksums);
        ArgumentNullException.ThrowIfNull(metadata);
        if (lastModifiedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(lastModifiedUtc));
        }

        BlobChecksum[] checksumCopy = checksums.ToArray();
        if (checksumCopy.Any(checksum => checksum is null) ||
            checksumCopy.Select(checksum => checksum.Algorithm).Distinct().Count() !=
            checksumCopy.Length)
        {
            throw new ArgumentException(
                "Checksum algorithms must be unique.",
                nameof(checksums));
        }

        ContentLength = contentLength;
        ContentType = contentType;
        LastModifiedUtc = lastModifiedUtc;
        Version = version;
        EntityTag = entityTag;
        _checksums = Array.AsReadOnly(checksumCopy);
        Metadata = metadata;
    }

    public long ContentLength { get; }

    public BlobMediaType ContentType { get; }

    public DateTimeOffset LastModifiedUtc { get; }

    public BlobVersion Version { get; }

    public BlobEntityTag EntityTag { get; }

    public IReadOnlyList<BlobChecksum> Checksums => _checksums;

    public BlobMetadata Metadata { get; }
}

public sealed record BlobHead
{
    public BlobHead(BlobIdentity identity, BlobProperties properties)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(properties);
        if (identity.Version != properties.Version)
        {
            throw new ArgumentException(
                "Blob identity and properties must report the same version.",
                nameof(properties));
        }

        Identity = identity;
        Properties = properties;
    }

    public BlobIdentity Identity { get; }

    public BlobProperties Properties { get; }
}
