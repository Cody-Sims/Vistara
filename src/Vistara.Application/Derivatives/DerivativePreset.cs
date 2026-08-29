using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistara.Application.Common.Imaging;

namespace Vistara.Application.Derivatives;

public sealed record DerivativePresetId
{
    public DerivativePresetId(string name, int revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Trim();
        if (normalized.Length > 64 ||
            normalized.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character == '-')))
        {
            throw new ArgumentException(
                "Preset names must be lowercase ASCII identifiers.",
                nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        Name = normalized;
        Revision = revision;
    }

    public string Name { get; }

    public int Revision { get; }
}

public sealed class DerivativePreset
{
    private readonly IReadOnlyList<DerivativeRecipe> _outputs;

    public DerivativePreset(
        DerivativePresetId id,
        IEnumerable<DerivativeRecipe> outputs)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(outputs);
        DerivativeRecipe[] normalized = outputs
            .OrderBy(output => output.Dimensions.Width)
            .ThenBy(output => output.Dimensions.Height)
            .ThenBy(output => output.Format)
            .ThenBy(output => output.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0 || normalized.Any(output => output is null))
        {
            throw new ArgumentException(
                "A preset must declare at least one output.",
                nameof(outputs));
        }

        bool hasDuplicate = normalized
            .GroupBy(output => (
                output.Dimensions.Width,
                output.Dimensions.Height,
                output.Format))
            .Any(group => group.Count() > 1);
        if (hasDuplicate)
        {
            throw new ArgumentException(
                "A preset cannot declare ambiguous duplicate outputs.",
                nameof(outputs));
        }

        Id = id;
        _outputs = new ReadOnlyCollection<DerivativeRecipe>(normalized);
        CanonicalForm = SerializeCanonical();
        Fingerprint = Hash(CanonicalForm);
    }

    public DerivativePresetId Id { get; }

    public IReadOnlyList<DerivativeRecipe> Outputs => _outputs;

    public string CanonicalForm { get; }

    public string Fingerprint { get; }

    private string SerializeCanonical()
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", Id.Name);
            writer.WriteNumber("revision", Id.Revision);
            writer.WriteStartArray("outputs");
            foreach (DerivativeRecipe output in _outputs)
            {
                writer.WriteStringValue(output.Fingerprint);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public enum DerivativeNegotiationStatus
{
    Selected,
    PresetNotFound,
    OutputNotAllowed,
    FormatNotAcceptable,
}

public sealed class DerivativeOutputRequest
{
    private readonly IReadOnlyList<DerivativeFormat> _acceptedFormats;

    public DerivativeOutputRequest(
        DerivativePresetId preset,
        DerivativeDimensions dimensions,
        IEnumerable<DerivativeFormat> acceptedFormats)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(acceptedFormats);
        DerivativeFormat[] normalized = acceptedFormats.Distinct().ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "At least one accepted format is required.",
                nameof(acceptedFormats));
        }

        foreach (DerivativeFormat format in normalized)
        {
            if (!Enum.IsDefined(format))
            {
                throw new ArgumentOutOfRangeException(nameof(acceptedFormats));
            }
        }

        Preset = preset;
        Dimensions = dimensions;
        _acceptedFormats = new ReadOnlyCollection<DerivativeFormat>(normalized);
    }

    public DerivativePresetId Preset { get; }

    public DerivativeDimensions Dimensions { get; }

    public IReadOnlyList<DerivativeFormat> AcceptedFormats => _acceptedFormats;
}

public sealed record DerivativeNegotiationResult(
    DerivativeNegotiationStatus Status,
    DerivativePreset? Preset,
    DerivativeRecipe? Recipe);

public sealed partial class DerivativePresetRegistry
{
    private readonly ReadOnlyDictionary<DerivativePresetId, DerivativePreset> _presets;

    public DerivativePresetRegistry(IEnumerable<DerivativePreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        DerivativePreset[] normalized = presets
            .OrderBy(preset => preset.Id.Name, StringComparer.Ordinal)
            .ThenBy(preset => preset.Id.Revision)
            .ToArray();
        if (normalized.Length == 0 || normalized.Any(preset => preset is null))
        {
            throw new ArgumentException(
                "A registry must contain at least one preset.",
                nameof(presets));
        }

        Dictionary<DerivativePresetId, DerivativePreset> byId = [];
        foreach (DerivativePreset preset in normalized)
        {
            if (!byId.TryAdd(preset.Id, preset))
            {
                throw new ArgumentException(
                    "Preset names and revisions must be unique.",
                    nameof(presets));
            }
        }

        _presets = new ReadOnlyDictionary<DerivativePresetId, DerivativePreset>(byId);
        Fingerprint = HashRegistry(normalized);
    }

    public static DerivativePresetRegistry Standard { get; } = CreateStandard();

    public string Fingerprint { get; }

    public DerivativeNegotiationResult Negotiate(DerivativeOutputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_presets.TryGetValue(request.Preset, out DerivativePreset? preset))
        {
            return new(
                DerivativeNegotiationStatus.PresetNotFound,
                null,
                null);
        }

        DerivativeRecipe[] dimensions = preset.Outputs
            .Where(output =>
                output.Dimensions == request.Dimensions)
            .ToArray();
        if (dimensions.Length == 0)
        {
            return new(
                DerivativeNegotiationStatus.OutputNotAllowed,
                preset,
                null);
        }

        foreach (DerivativeFormat format in request.AcceptedFormats)
        {
            DerivativeRecipe? selected = dimensions
                .SingleOrDefault(output => output.Format == format);
            if (selected is not null)
            {
                return new(
                    DerivativeNegotiationStatus.Selected,
                    preset,
                    selected);
            }
        }

        return new(
            DerivativeNegotiationStatus.FormatNotAcceptable,
            preset,
            null);
    }

    public DerivativeResolutionResult ResolveDefault(
        DerivativeSourceIdentity source,
        DerivativePresetId presetId,
        ImagePipelineFingerprint pipelineFingerprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(presetId);
        ArgumentNullException.ThrowIfNull(pipelineFingerprint);
        if (!_presets.TryGetValue(presetId, out DerivativePreset? preset))
        {
            return new DerivativeResolutionResult(
                DerivativeNegotiationStatus.PresetNotFound,
                null);
        }

        DerivativeDimensions dimensions = preset.Outputs
            .OrderBy(output => output.Dimensions.Width)
            .ThenBy(output => output.Dimensions.Height)
            .Select(output => output.Dimensions)
            .First();
        var output = new DerivativeOutputRequest(
            presetId,
            dimensions,
            [DerivativeFormat.WebP, DerivativeFormat.Jpeg, DerivativeFormat.Png]);
        return Resolve(new DerivativeRequest(source, output, pipelineFingerprint));
    }

    private static DerivativePresetRegistry CreateStandard() =>
        new(
            [
                CreatePreset("thumb", DerivativeFit.Cover, [256, 512], includePng: false),
                CreatePreset("grid", DerivativeFit.Cover, [512, 1_024], includePng: false),
                CreatePreset(
                    "viewer",
                    DerivativeFit.Contain,
                    [1_024, 1_600, 2_400],
                    includePng: false),
                CreatePreset(
                    "download-web",
                    DerivativeFit.Contain,
                    [1_024, 1_600, 2_400],
                    includePng: true),
            ]);

    private static DerivativePreset CreatePreset(
        string name,
        DerivativeFit fit,
        IEnumerable<int> sizes,
        bool includePng)
    {
        List<DerivativeRecipe> outputs = [];
        foreach (int size in sizes)
        {
            outputs.Add(CreateRecipe(size, fit, DerivativeFormat.WebP));
            outputs.Add(CreateRecipe(size, fit, DerivativeFormat.Jpeg));
            if (includePng)
            {
                outputs.Add(CreateRecipe(size, fit, DerivativeFormat.Png));
            }
        }

        return new DerivativePreset(new DerivativePresetId(name, 1), outputs);
    }

    private static DerivativeRecipe CreateRecipe(
        int size,
        DerivativeFit fit,
        DerivativeFormat format) =>
        new(
            schemaVersion: 1,
            new DerivativeDimensions(size, size),
            fit,
            format,
            quality: format == DerivativeFormat.Png ? 100 : 82,
            format == DerivativeFormat.Jpeg
                ? DerivativeBackground.White
                : DerivativeBackground.Transparent,
            allowUpscale: false,
            DerivativeMetadataBehavior.StripSensitive);

    private static string HashRegistry(IEnumerable<DerivativePreset> presets)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteStartArray("presets");
            foreach (DerivativePreset preset in presets)
            {
                writer.WriteStringValue(preset.Fingerprint);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
