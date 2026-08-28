using System.Collections.ObjectModel;

namespace Vistara.Domain.Assets;

public enum AssetStatus
{
    Processing,
    Ready,
    Failed,
    Trashed,
    Purged,
}

public enum AssetVisibility
{
    Private,
    Tenant,
    Public,
}

public sealed record Sha256Checksum
{
    public Sha256Checksum(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A SHA-256 checksum must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record MediaContentType
{
    public MediaContentType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim().ToLowerInvariant();
        int separator = normalized.IndexOf('/');
        if (separator <= 0 ||
            separator == normalized.Length - 1 ||
            normalized.Contains(';') ||
            normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "A media content type must contain a type and subtype without parameters.",
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record PixelDimensions
{
    public PixelDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

public sealed class MediaPrivacyMetadata
{
    public MediaPrivacyMetadata(
        IReadOnlyDictionary<string, string>? safeProperties = null,
        IReadOnlyDictionary<string, string>? privateProperties = null)
    {
        SafeProperties = CopyProperties(safeProperties);
        PrivateProperties = CopyProperties(privateProperties);
    }

    public IReadOnlyDictionary<string, string> SafeProperties { get; }

    public IReadOnlyDictionary<string, string> PrivateProperties { get; }

    private static ReadOnlyDictionary<string, string> CopyProperties(
        IReadOnlyDictionary<string, string>? source)
    {
        Dictionary<string, string> copy = new(StringComparer.Ordinal);
        if (source is not null)
        {
            foreach ((string key, string value) in source)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                ArgumentNullException.ThrowIfNull(value);
                copy.Add(key, value);
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}

public sealed class MediaDescriptor
{
    public MediaDescriptor(
        string detectedFormat,
        MediaContentType detectedContentType,
        PixelDimensions dimensions,
        int frameCount,
        MediaPrivacyMetadata privacyMetadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detectedFormat);
        ArgumentNullException.ThrowIfNull(detectedContentType);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        ArgumentNullException.ThrowIfNull(privacyMetadata);

        DetectedFormat = detectedFormat.Trim().ToLowerInvariant();
        DetectedContentType = detectedContentType;
        Dimensions = dimensions;
        FrameCount = frameCount;
        PrivacyMetadata = privacyMetadata;
    }

    public string DetectedFormat { get; }

    public MediaContentType DetectedContentType { get; }

    public PixelDimensions Dimensions { get; }

    public int FrameCount { get; }

    public MediaPrivacyMetadata PrivacyMetadata { get; }
}
