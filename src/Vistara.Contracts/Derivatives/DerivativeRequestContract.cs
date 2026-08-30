using System.Text.Json.Serialization;

namespace Vistara.Contracts.Derivatives;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DerivativeRequestContract
{
    [JsonConstructor]
    public DerivativeRequestContract(
        string preset,
        int revision,
        DerivativeParametersContract? parameters = null)
    {
        preset = ContractGuards.RequiredText(preset, nameof(preset), 64).Trim();
        if (preset.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character == '-')))
        {
            throw new ArgumentException(
                "A preset must be a lowercase ASCII identifier.",
                nameof(preset));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        Preset = preset;
        Revision = revision;
        Parameters = parameters;
    }

    [JsonPropertyName("preset")]
    public string Preset { get; }

    [JsonPropertyName("revision")]
    public int Revision { get; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DerivativeParametersContract? Parameters { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DerivativeParametersContract
{
    public const int MaximumDimension = 8_192;

    [JsonConstructor]
    public DerivativeParametersContract(
        int? width = null,
        int? height = null,
        string? fit = null,
        string? format = null,
        int? quality = null,
        DerivativeFocalPointContract? focalPoint = null,
        DerivativeCropRectangleContract? crop = null,
        string? metadataPolicy = null,
        string? colorPolicy = null)
    {
        ValidateRange(width, 1, MaximumDimension, nameof(width));
        ValidateRange(height, 1, MaximumDimension, nameof(height));
        ValidateRange(quality, 1, 100, nameof(quality));
        Fit = ValidateChoice(fit, nameof(fit), ["contain", "cover", "crop"]);
        Format = ValidateChoice(format, nameof(format), ["jpeg", "png", "webp"]);
        MetadataPolicy = ValidateChoice(
            metadataPolicy,
            nameof(metadataPolicy),
            ["strip-sensitive", "strip-all"]);
        ColorPolicy = ValidateChoice(colorPolicy, nameof(colorPolicy), ["srgb"]);
        Width = width;
        Height = height;
        Quality = quality;
        FocalPoint = focalPoint;
        Crop = crop;
    }

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; }

    [JsonPropertyName("fit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fit { get; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Quality { get; }

    [JsonPropertyName("focalPoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DerivativeFocalPointContract? FocalPoint { get; }

    [JsonPropertyName("crop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DerivativeCropRectangleContract? Crop { get; }

    [JsonPropertyName("metadataPolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataPolicy { get; }

    [JsonPropertyName("colorPolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorPolicy { get; }

    private static void ValidateRange(
        int? value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value is < 1 || value > maximum || value < minimum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The value must be between {minimum} and {maximum}.");
        }
    }

    private static string? ValidateChoice(
        string? value,
        string parameterName,
        IReadOnlyList<string> choices)
    {
        if (value is null)
        {
            return null;
        }

        value = ContractGuards.RequiredText(value, parameterName, 64);
        if (!choices.Contains(value, StringComparer.Ordinal))
        {
            throw new ArgumentException("The value is not supported.", parameterName);
        }

        return value;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DerivativeFocalPointContract
{
    [JsonConstructor]
    public DerivativeFocalPointContract(decimal x, decimal y)
    {
        ValidateUnitInterval(x, nameof(x));
        ValidateUnitInterval(y, nameof(y));
        X = x;
        Y = y;
    }

    [JsonPropertyName("x")]
    public decimal X { get; }

    [JsonPropertyName("y")]
    public decimal Y { get; }

    private static void ValidateUnitInterval(decimal value, string parameterName)
    {
        if (value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DerivativeCropRectangleContract
{
    [JsonConstructor]
    public DerivativeCropRectangleContract(
        decimal x,
        decimal y,
        decimal width,
        decimal height)
    {
        if (x is < 0 or > 1 ||
            y is < 0 or > 1 ||
            width is <= 0 or > 1 ||
            height is <= 0 or > 1 ||
            x + width > 1 ||
            y + height > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "The crop rectangle must be normalized within the source image.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    [JsonPropertyName("x")]
    public decimal X { get; }

    [JsonPropertyName("y")]
    public decimal Y { get; }

    [JsonPropertyName("width")]
    public decimal Width { get; }

    [JsonPropertyName("height")]
    public decimal Height { get; }
}
