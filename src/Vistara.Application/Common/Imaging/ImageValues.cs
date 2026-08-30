using System.Security.Cryptography;
using System.Text;

namespace Vistara.Application.Common.Imaging;

public enum ImageFormat
{
    Jpeg,
    Png,
    WebP,
}

public enum ImagePixelFormat
{
    Gray8,
    GrayAlpha8,
    Rgb8,
    Rgba8,
    Rgb16,
    Rgba16,
}

public enum ImageOrientation
{
    Normal = 1,
    MirrorHorizontal = 2,
    Rotate180 = 3,
    MirrorVertical = 4,
    MirrorHorizontalRotate270Clockwise = 5,
    Rotate90Clockwise = 6,
    MirrorHorizontalRotate90Clockwise = 7,
    Rotate270Clockwise = 8,
}

public sealed record ImageMediaType
{
    public ImageMediaType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim().ToLowerInvariant();
        if (!normalized.StartsWith("image/", StringComparison.Ordinal) ||
            normalized.Length == "image/".Length ||
            normalized.Any(character =>
                character > 127 || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A valid image media type is required.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }
}

public sealed record ImagePrivacyMetadata(
    bool HasExif,
    bool HasGps,
    bool HasXmp,
    bool HasIptc,
    bool HasComments,
    bool HasEmbeddedThumbnail,
    bool HasEmbeddedFileName);

public sealed record ImageInspection
{
    public ImageInspection(
        ImageFormat format,
        ImageMediaType contentType,
        int width,
        int height,
        int frameCount,
        long aggregatePixels,
        ImagePixelFormat pixelFormat,
        ImageOrientation orientation,
        ImagePrivacyMetadata privacy,
        long encodedBytes,
        long estimatedDecodedBytes)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aggregatePixels);
        if (!Enum.IsDefined(pixelFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        }

        if (!Enum.IsDefined(orientation))
        {
            throw new ArgumentOutOfRangeException(nameof(orientation));
        }

        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(estimatedDecodedBytes);
        long framePixels = checked((long)width * height);
        long maximumAggregate = checked(framePixels * frameCount);
        if (aggregatePixels < framePixels || aggregatePixels > maximumAggregate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregatePixels),
                "Aggregate pixels must describe the decoded frames.");
        }

        Format = format;
        ContentType = contentType;
        Width = width;
        Height = height;
        FrameCount = frameCount;
        AggregatePixels = aggregatePixels;
        PixelFormat = pixelFormat;
        Orientation = orientation;
        Privacy = privacy;
        EncodedBytes = encodedBytes;
        EstimatedDecodedBytes = estimatedDecodedBytes;
    }

    public ImageFormat Format { get; }

    public ImageMediaType ContentType { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount { get; }

    public long AggregatePixels { get; }

    public ImagePixelFormat PixelFormat { get; }

    public ImageOrientation Orientation { get; }

    public ImagePrivacyMetadata Privacy { get; }

    public long EncodedBytes { get; }

    public long EstimatedDecodedBytes { get; }
}

public sealed record ImageSha256
{
    public ImageSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Image SHA-256 values must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }
}

public sealed record ImagePipelineFingerprint
{
    public ImagePipelineFingerprint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
}

public enum ImageResizeMode
{
    Fit,
    Fill,
    Crop,
    Pad,
}

public enum ImageAnchor
{
    Center,
    Top,
    Bottom,
    Left,
    Right,
}

public enum ImageMetadataPolicy
{
    StripSensitive,
    StripAll,
}

public sealed record CanonicalTransformRecipe
{
    public CanonicalTransformRecipe(
        int schemaVersion,
        int width,
        int height,
        ImageResizeMode resizeMode,
        ImageAnchor anchor,
        bool allowUpscale,
        ImageFormat outputFormat,
        int quality,
        ImageMetadataPolicy metadataPolicy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!Enum.IsDefined(resizeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(resizeMode));
        }

        if (!Enum.IsDefined(anchor))
        {
            throw new ArgumentOutOfRangeException(nameof(anchor));
        }

        if (!Enum.IsDefined(outputFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(outputFormat));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        if (!Enum.IsDefined(metadataPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(metadataPolicy));
        }

        SchemaVersion = schemaVersion;
        Width = width;
        Height = height;
        ResizeMode = resizeMode;
        Anchor = anchor;
        AllowUpscale = allowUpscale;
        OutputFormat = outputFormat;
        Quality = quality;
        MetadataPolicy = metadataPolicy;
        string canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{schemaVersion}|{width}|{height}|{(int)resizeMode}|{(int)anchor}|{allowUpscale}|{(int)outputFormat}|{quality}|{(int)metadataPolicy}");
        Fingerprint = new ImageSha256(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    public int SchemaVersion { get; }

    public int Width { get; }

    public int Height { get; }

    public ImageResizeMode ResizeMode { get; }

    public ImageAnchor Anchor { get; }

    public bool AllowUpscale { get; }

    public ImageFormat OutputFormat { get; }

    public int Quality { get; }

    public ImageMetadataPolicy MetadataPolicy { get; }

    public ImageSha256 Fingerprint { get; }
}

public sealed record ImageTransformPreset
{
    public ImageTransformPreset(
        string name,
        int revision,
        CanonicalTransformRecipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Trim();
        if (normalized.Length > 64 ||
            normalized.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '-')))
        {
            throw new ArgumentException(
                "Preset names must be lowercase ASCII identifiers.",
                nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentNullException.ThrowIfNull(recipe);
        Name = normalized;
        Revision = revision;
        Recipe = recipe;
    }

    public string Name { get; }

    public int Revision { get; }

    public CanonicalTransformRecipe Recipe { get; }
}
