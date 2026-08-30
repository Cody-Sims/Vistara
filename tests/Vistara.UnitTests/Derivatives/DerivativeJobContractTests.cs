using System.Security.Cryptography;
using System.Text.Json;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Xunit;

namespace Vistara.UnitTests.Derivatives;

public sealed class DerivativeJobContractTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void Exact_generation_descriptor_round_trips_with_canonical_dedupe()
    {
        DerivativeGenerationRequest generation = Resolve(
            presetRevision: 3,
            recipeSchemaVersion: 4,
            quality: 87);
        DerivativeJobPayloadV1 expected =
            DerivativeJobContract.CreatePayload(generation);
        string json = DerivativeJobContract.Serialize(expected);

        bool parsed = DerivativeJobContract.TryParse(
            DerivativeJobContract.Type,
            DerivativeJobContract.PayloadVersion,
            json,
            out DerivativeJobPayloadV1? payload);

        Assert.True(parsed);
        Assert.Equal(expected, payload);
        Assert.Equal(2, DerivativeJobContract.PayloadVersion);
        Assert.Equal(3, payload?.Generation.PresetRevision);
        Assert.Equal(4, payload?.Generation.RecipeSchemaVersion);
        Assert.Equal(1_600, payload?.Generation.Width);
        Assert.Equal("webp", payload?.Generation.Format);
        Assert.Equal(87, payload?.Generation.Quality);
        Assert.Equal("pipeline-1", payload?.Generation.PipelineFingerprint);
        Assert.Equal(
            generation.DedupeIdentity.Key,
            DerivativeJobContract.CreateDedupeKey(payload!));
    }

    [Theory]
    [InlineData("derivative.generate", 1)]
    [InlineData("asset.derivative.generate", 3)]
    public void Unsupported_job_alias_or_payload_version_is_rejected(
        string type,
        int payloadVersion)
    {
        string json = JsonSerializer.Serialize(
            new
            {
                assetId = Guid.CreateVersion7(),
                revisionId = Guid.CreateVersion7(),
                preset = "thumb",
            });

        Assert.False(DerivativeJobContract.TryParse(
            new JobType(type),
            payloadVersion,
            json,
            out _));
    }

    [Fact]
    public void Legacy_preset_only_payload_is_rejected()
    {
        string json = JsonSerializer.Serialize(
            new
            {
                assetId = Guid.CreateVersion7(),
                revisionId = Guid.CreateVersion7(),
                preset = "thumb",
            },
            JsonOptions);

        Assert.False(DerivativeJobContract.TryParse(
            DerivativeJobContract.Type,
            payloadVersion: 1,
            json,
            out _));
    }

    [Theory]
    [InlineData("thumb", 256)]
    [InlineData("grid", 512)]
    [InlineData("viewer", 1024)]
    [InlineData("download-web", 1024)]
    public void Default_ingest_derivative_selects_smallest_preferred_webp(
        string preset,
        int expectedDimension)
    {
        var source = new DerivativeSourceIdentity(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            revisionNumber: 1,
            new ImageSha256(Convert.ToHexStringLower(SHA256.HashData("source"u8))));

        DerivativeResolutionResult result = DerivativePresetRegistry.Standard.ResolveDefault(
            source,
            new DerivativePresetId(preset, 1),
            new ImagePipelineFingerprint("pipeline"));

        Assert.Equal(DerivativeNegotiationStatus.Selected, result.Status);
        Assert.Equal(expectedDimension, result.GenerationRequest?.Recipe.Dimensions.Width);
        Assert.Equal(expectedDimension, result.GenerationRequest?.Recipe.Dimensions.Height);
        Assert.Equal(DerivativeFormat.WebP, result.GenerationRequest?.Recipe.Format);
    }

    [Fact]
    public void Recipe_and_preset_revisions_change_the_canonical_job_identity()
    {
        DerivativeJobPayloadV1 baseline =
            DerivativeJobContract.CreatePayload(
                Resolve(presetRevision: 3, recipeSchemaVersion: 4, quality: 82));
        DerivativeJobPayloadV1 recipeRevision =
            DerivativeJobContract.CreatePayload(
                Resolve(presetRevision: 3, recipeSchemaVersion: 5, quality: 82));
        DerivativeJobPayloadV1 presetRevision =
            DerivativeJobContract.CreatePayload(
                Resolve(presetRevision: 4, recipeSchemaVersion: 4, quality: 82));

        Assert.NotEqual(
            DerivativeJobContract.CreateDedupeKey(baseline),
            DerivativeJobContract.CreateDedupeKey(recipeRevision));
        Assert.NotEqual(
            DerivativeJobContract.CreateDedupeKey(baseline),
            DerivativeJobContract.CreateDedupeKey(presetRevision));
    }

    [Theory]
    [InlineData("\"quality\":87", "\"quality\":86")]
    [InlineData("\"width\":1600", "\"width\":1024")]
    [InlineData("\"pipelineFingerprint\":\"pipeline-1\"", "\"pipelineFingerprint\":\"pipeline-2\"")]
    public void Tampered_generation_fields_are_rejected(
        string original,
        string replacement)
    {
        string json = DerivativeJobContract.Serialize(
            DerivativeJobContract.CreatePayload(
                Resolve(
                    presetRevision: 3,
                    recipeSchemaVersion: 4,
                    quality: 87)));

        Assert.False(DerivativeJobContract.TryParse(
            DerivativeJobContract.Type,
            DerivativeJobContract.PayloadVersion,
            json.Replace(original, replacement, StringComparison.Ordinal),
            out _));
    }

    private static DerivativeGenerationRequest Resolve(
        int presetRevision,
        int recipeSchemaVersion,
        int quality)
    {
        var source = new DerivativeSourceIdentity(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            revisionNumber: 1,
            new ImageSha256(Convert.ToHexStringLower(SHA256.HashData("source"u8))));
        var recipe = new DerivativeRecipe(
            recipeSchemaVersion,
            new DerivativeDimensions(1_600, 1_600),
            DerivativeFit.Contain,
            DerivativeFormat.WebP,
            quality,
            DerivativeBackground.Transparent,
            allowUpscale: false,
            DerivativeMetadataBehavior.StripSensitive);
        var registry = new DerivativePresetRegistry(
            [
                new DerivativePreset(
                    new DerivativePresetId("viewer", presetRevision),
                    [recipe]),
            ]);
        return Assert.IsType<DerivativeGenerationRequest>(
            registry.ResolveDefault(
                source,
                new DerivativePresetId("viewer", presetRevision),
                new ImagePipelineFingerprint("pipeline-1"))
            .GenerationRequest);
    }
}
