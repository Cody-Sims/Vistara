using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vistara.Application.Common.Imaging;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Derivatives;

public sealed record DerivativeGenerationDescriptorV1
{
    public const int CurrentVersion = 1;

    [JsonConstructor]
    public DerivativeGenerationDescriptorV1(
        int descriptorVersion,
        Guid tenantId,
        Guid assetId,
        Guid revisionId,
        long revisionNumber,
        string sourceSha256,
        string presetName,
        int presetRevision,
        int recipeSchemaVersion,
        int width,
        int height,
        string fit,
        string format,
        int quality,
        string background,
        bool allowUpscale,
        string metadataBehavior,
        string recipeSha256,
        string pipelineFingerprint,
        string generationIdentity,
        string cacheKey)
    {
        if (descriptorVersion != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptorVersion),
                "The derivative generation descriptor version is unsupported.");
        }

        DerivativeGenerationRequest generation = CreateGeneration(
            tenantId,
            assetId,
            revisionId,
            revisionNumber,
            sourceSha256,
            presetName,
            presetRevision,
            recipeSchemaVersion,
            width,
            height,
            fit,
            format,
            quality,
            background,
            allowUpscale,
            metadataBehavior,
            pipelineFingerprint);
        EnsureExact(
            recipeSha256,
            generation.Recipe.Fingerprint,
            nameof(recipeSha256));
        EnsureExact(
            generationIdentity,
            generation.Identity.Value,
            nameof(generationIdentity));
        EnsureExact(cacheKey, generation.CacheKey.Value, nameof(cacheKey));

        DescriptorVersion = CurrentVersion;
        TenantId = generation.Source.TenantId;
        AssetId = generation.Source.AssetId;
        RevisionId = generation.Source.RevisionId;
        RevisionNumber = generation.Source.RevisionNumber;
        SourceSha256 = generation.Source.SourceSha256.Value;
        PresetName = generation.Preset.Id.Name;
        PresetRevision = generation.Preset.Id.Revision;
        RecipeSchemaVersion = generation.Recipe.SchemaVersion;
        Width = generation.Recipe.Dimensions.Width;
        Height = generation.Recipe.Dimensions.Height;
        Fit = ToCanonicalName(generation.Recipe.Fit);
        Format = ToCanonicalName(generation.Recipe.Format);
        Quality = generation.Recipe.Quality;
        Background = ToCanonicalName(generation.Recipe.Background);
        AllowUpscale = generation.Recipe.AllowUpscale;
        MetadataBehavior = ToCanonicalName(generation.Recipe.MetadataBehavior);
        RecipeSha256 = generation.Recipe.Fingerprint;
        PipelineFingerprint = generation.PipelineFingerprint.Value;
        GenerationIdentity = generation.Identity.Value;
        CacheKey = generation.CacheKey.Value;
    }

    public int DescriptorVersion { get; }

    public Guid TenantId { get; }

    public Guid AssetId { get; }

    public Guid RevisionId { get; }

    public long RevisionNumber { get; }

    public string SourceSha256 { get; }

    public string PresetName { get; }

    public int PresetRevision { get; }

    public int RecipeSchemaVersion { get; }

    public int Width { get; }

    public int Height { get; }

    public string Fit { get; }

    public string Format { get; }

    public int Quality { get; }

    public string Background { get; }

    public bool AllowUpscale { get; }

    public string MetadataBehavior { get; }

    public string RecipeSha256 { get; }

    public string PipelineFingerprint { get; }

    public string GenerationIdentity { get; }

    public string CacheKey { get; }

    [JsonIgnore]
    public string PipelineIdentity =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(PipelineFingerprint)));

    public static DerivativeGenerationDescriptorV1 Create(
        DerivativeGenerationRequest generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return new(
            CurrentVersion,
            generation.Source.TenantId,
            generation.Source.AssetId,
            generation.Source.RevisionId,
            generation.Source.RevisionNumber,
            generation.Source.SourceSha256.Value,
            generation.Preset.Id.Name,
            generation.Preset.Id.Revision,
            generation.Recipe.SchemaVersion,
            generation.Recipe.Dimensions.Width,
            generation.Recipe.Dimensions.Height,
            ToCanonicalName(generation.Recipe.Fit),
            ToCanonicalName(generation.Recipe.Format),
            generation.Recipe.Quality,
            ToCanonicalName(generation.Recipe.Background),
            generation.Recipe.AllowUpscale,
            ToCanonicalName(generation.Recipe.MetadataBehavior),
            generation.Recipe.Fingerprint,
            generation.PipelineFingerprint.Value,
            generation.Identity.Value,
            generation.CacheKey.Value);
    }

    public DerivativeGenerationRequest ToGenerationRequest() =>
        CreateGeneration(
            TenantId,
            AssetId,
            RevisionId,
            RevisionNumber,
            SourceSha256,
            PresetName,
            PresetRevision,
            RecipeSchemaVersion,
            Width,
            Height,
            Fit,
            Format,
            Quality,
            Background,
            AllowUpscale,
            MetadataBehavior,
            PipelineFingerprint);

    private static DerivativeGenerationRequest CreateGeneration(
        Guid tenantId,
        Guid assetId,
        Guid revisionId,
        long revisionNumber,
        string sourceSha256,
        string presetName,
        int presetRevision,
        int recipeSchemaVersion,
        int width,
        int height,
        string fit,
        string format,
        int quality,
        string background,
        bool allowUpscale,
        string metadataBehavior,
        string pipelineFingerprint)
    {
        var source = new DerivativeSourceIdentity(
            tenantId,
            assetId,
            revisionId,
            revisionNumber,
            new ImageSha256(sourceSha256));
        var recipe = new DerivativeRecipe(
            recipeSchemaVersion,
            new DerivativeDimensions(width, height),
            ParseFit(fit),
            ParseFormat(format),
            quality,
            ParseBackground(background),
            allowUpscale,
            ParseMetadataBehavior(metadataBehavior));
        var preset = new DerivativePreset(
            new DerivativePresetId(presetName, presetRevision),
            [recipe]);
        return new DerivativeGenerationRequest(
            source,
            preset,
            recipe,
            new ImagePipelineFingerprint(pipelineFingerprint));
    }

    private static void EnsureExact(
        string observed,
        string expected,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observed);
        if (!string.Equals(observed, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The derivative generation descriptor is not canonical.",
                parameterName);
        }
    }

    private static DerivativeFit ParseFit(string value) => value switch
    {
        "contain" => DerivativeFit.Contain,
        "cover" => DerivativeFit.Cover,
        "crop" => DerivativeFit.Crop,
        _ => throw new ArgumentException("The derivative fit is invalid.", nameof(value)),
    };

    private static DerivativeFormat ParseFormat(string value) => value switch
    {
        "jpeg" => DerivativeFormat.Jpeg,
        "png" => DerivativeFormat.Png,
        "webp" => DerivativeFormat.WebP,
        _ => throw new ArgumentException("The derivative format is invalid.", nameof(value)),
    };

    private static DerivativeBackground ParseBackground(string value) => value switch
    {
        "transparent" => DerivativeBackground.Transparent,
        "white" => DerivativeBackground.White,
        _ => throw new ArgumentException(
            "The derivative background is invalid.",
            nameof(value)),
    };

    private static DerivativeMetadataBehavior ParseMetadataBehavior(string value) =>
        value switch
        {
            "strip-sensitive" => DerivativeMetadataBehavior.StripSensitive,
            "strip-all" => DerivativeMetadataBehavior.StripAll,
            _ => throw new ArgumentException(
                "The derivative metadata behavior is invalid.",
                nameof(value)),
        };

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

    private static string ToCanonicalName(DerivativeMetadataBehavior value) =>
        value switch
        {
            DerivativeMetadataBehavior.StripSensitive => "strip-sensitive",
            DerivativeMetadataBehavior.StripAll => "strip-all",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
}

public sealed record DerivativeJobPayloadV1
{
    [JsonConstructor]
    public DerivativeJobPayloadV1(DerivativeGenerationDescriptorV1 generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        Generation = generation;
    }

    public DerivativeGenerationDescriptorV1 Generation { get; }

    [JsonIgnore]
    public Guid AssetId => Generation.AssetId;

    [JsonIgnore]
    public Guid RevisionId => Generation.RevisionId;

    [JsonIgnore]
    public string Preset => Generation.PresetName;
}

public static class DerivativeJobContract
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public const string TypeName = "asset.derivative.generate";
    public const int PayloadVersion = 2;

    public static JobType Type { get; } = new(TypeName);

    public static DerivativeJobPayloadV1 CreatePayload(
        DerivativeGenerationRequest generation) =>
        new(DerivativeGenerationDescriptorV1.Create(generation));

    public static string Serialize(DerivativeJobPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static bool TryParse(
        JobType type,
        int payloadVersion,
        string json,
        out DerivativeJobPayloadV1? payload)
    {
        payload = null;
        if (type != Type || payloadVersion != PayloadVersion)
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<DerivativeJobPayloadV1>(
                json,
                JsonOptions);
            return payload is not null;
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException or
                InvalidOperationException)
        {
            return false;
        }
    }

    public static JobDedupeKey CreateDedupeKey(DerivativeJobPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new JobDedupeKey(
            $"derivative:{payload.Generation.GenerationIdentity}");
    }
}
