using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistara.Application.Common.Imaging;

namespace Vistara.Application.Derivatives;

public enum DerivativeFit
{
    Contain,
    Cover,
    Crop,
}

public enum DerivativeFormat
{
    Jpeg,
    Png,
    WebP,
}

public enum DerivativeBackground
{
    Transparent,
    White,
}

public enum DerivativeMetadataBehavior
{
    StripSensitive,
    StripAll,
}

public sealed record DerivativeDimensions
{
    public const int MaximumDimension = 8_192;

    public DerivativeDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaximumDimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaximumDimension);
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

public sealed record DerivativeRecipe
{
    public DerivativeRecipe(
        int schemaVersion,
        DerivativeDimensions dimensions,
        DerivativeFit fit,
        DerivativeFormat format,
        int quality,
        DerivativeBackground background,
        bool allowUpscale,
        DerivativeMetadataBehavior metadataBehavior)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentNullException.ThrowIfNull(dimensions);
        EnsureDefined(fit, nameof(fit));
        EnsureDefined(format, nameof(format));
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        EnsureDefined(background, nameof(background));
        EnsureDefined(metadataBehavior, nameof(metadataBehavior));
        if (format == DerivativeFormat.Jpeg &&
            background == DerivativeBackground.Transparent)
        {
            throw new ArgumentException(
                "JPEG outputs require an opaque background.",
                nameof(background));
        }

        if (format != DerivativeFormat.Jpeg &&
            background != DerivativeBackground.Transparent)
        {
            throw new ArgumentException(
                "Alpha-capable outputs must preserve a transparent background.",
                nameof(background));
        }

        SchemaVersion = schemaVersion;
        Dimensions = dimensions;
        Fit = fit;
        Format = format;
        Quality = quality;
        Background = background;
        AllowUpscale = allowUpscale;
        MetadataBehavior = metadataBehavior;
        ProcessorRecipe = new CanonicalTransformRecipe(
            schemaVersion,
            dimensions.Width,
            dimensions.Height,
            fit switch
            {
                DerivativeFit.Contain => ImageResizeMode.Fit,
                DerivativeFit.Cover => ImageResizeMode.Crop,
                DerivativeFit.Crop => ImageResizeMode.Crop,
                _ => throw new ArgumentOutOfRangeException(nameof(fit)),
            },
            ImageAnchor.Center,
            allowUpscale,
            format switch
            {
                DerivativeFormat.Jpeg => ImageFormat.Jpeg,
                DerivativeFormat.Png => ImageFormat.Png,
                DerivativeFormat.WebP => ImageFormat.WebP,
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            },
            quality,
            metadataBehavior switch
            {
                DerivativeMetadataBehavior.StripSensitive =>
                    ImageMetadataPolicy.StripSensitive,
                DerivativeMetadataBehavior.StripAll => ImageMetadataPolicy.StripAll,
                _ => throw new ArgumentOutOfRangeException(nameof(metadataBehavior)),
            });
        CanonicalForm = SerializeCanonical();
        Fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalForm)));
    }

    public int SchemaVersion { get; }

    public DerivativeDimensions Dimensions { get; }

    public DerivativeFit Fit { get; }

    public DerivativeFormat Format { get; }

    public int Quality { get; }

    public DerivativeBackground Background { get; }

    public bool AllowUpscale { get; }

    public DerivativeMetadataBehavior MetadataBehavior { get; }

    public CanonicalTransformRecipe ProcessorRecipe { get; }

    public string CanonicalForm { get; }

    public string Fingerprint { get; }

    private string SerializeCanonical()
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", SchemaVersion);
            writer.WriteNumber("width", Dimensions.Width);
            writer.WriteNumber("height", Dimensions.Height);
            writer.WriteString("fit", ToCanonicalName(Fit));
            writer.WriteString("format", ToCanonicalName(Format));
            writer.WriteNumber("quality", Quality);
            writer.WriteString("background", ToCanonicalName(Background));
            writer.WriteBoolean("upscale", AllowUpscale);
            writer.WriteString("metadata", ToCanonicalName(MetadataBehavior));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ToCanonicalName(DerivativeFit value) => value switch
    {
        DerivativeFit.Contain => "contain",
        DerivativeFit.Cover => "cover",
        DerivativeFit.Crop => "crop",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToCanonicalName(DerivativeFormat value) => value switch
    {
        DerivativeFormat.Jpeg => "jpeg",
        DerivativeFormat.Png => "png",
        DerivativeFormat.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToCanonicalName(DerivativeBackground value) => value switch
    {
        DerivativeBackground.Transparent => "transparent",
        DerivativeBackground.White => "white",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToCanonicalName(DerivativeMetadataBehavior value) => value switch
    {
        DerivativeMetadataBehavior.StripSensitive => "strip-sensitive",
        DerivativeMetadataBehavior.StripAll => "strip-all",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void EnsureDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
