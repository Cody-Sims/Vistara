using Vistara.Application.Derivatives;

namespace Vistara.UnitTests.Derivatives;

public sealed class DerivativePresetRegistryTests
{
    [Fact]
    public void Preset_fingerprint_is_independent_of_output_declaration_order()
    {
        DerivativeRecipe smallWebP = Recipe(256, 256, DerivativeFormat.WebP);
        DerivativeRecipe smallJpeg = Recipe(256, 256, DerivativeFormat.Jpeg);

        DerivativePreset first = new(
            new DerivativePresetId("thumb", 1),
            [smallWebP, smallJpeg]);
        DerivativePreset reordered = new(
            new DerivativePresetId("thumb", 1),
            [smallJpeg, smallWebP]);

        Assert.Equal(first.CanonicalForm, reordered.CanonicalForm);
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
    }

    [Fact]
    public void Preset_revision_changes_fingerprint_even_when_recipe_is_unchanged()
    {
        DerivativeRecipe recipe = Recipe(512, 512, DerivativeFormat.WebP);

        DerivativePreset first = new(
            new DerivativePresetId("grid", 1),
            [recipe]);
        DerivativePreset revised = new(
            new DerivativePresetId("grid", 2),
            [recipe]);

        Assert.NotEqual(first.Fingerprint, revised.Fingerprint);
    }

    [Fact]
    public void Registry_fingerprint_is_order_independent_and_changes_with_policy()
    {
        DerivativePreset thumb = Preset("thumb", 1, 256);
        DerivativePreset grid = Preset("grid", 1, 512);

        DerivativePresetRegistry first = new([thumb, grid]);
        DerivativePresetRegistry reordered = new([grid, thumb]);
        DerivativePresetRegistry revised = new([thumb, Preset("grid", 2, 512)]);

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, revised.Fingerprint);
    }

    [Fact]
    public void Registry_rejects_duplicate_named_revisions_and_duplicate_outputs()
    {
        DerivativePreset preset = Preset("thumb", 1, 256);

        Assert.Throws<ArgumentException>(() => new DerivativePresetRegistry([preset, preset]));
        Assert.Throws<ArgumentException>(
            () => new DerivativePreset(
                preset.Id,
                [
                    Recipe(256, 256, DerivativeFormat.WebP),
                    Recipe(256, 256, DerivativeFormat.WebP),
                ]));
    }

    [Fact]
    public void Negotiation_selects_only_an_exact_allowlisted_output()
    {
        DerivativePresetRegistry registry = new(
            [
                new DerivativePreset(
                    new DerivativePresetId("viewer", 3),
                    [
                        Recipe(1_600, 1_600, DerivativeFormat.Jpeg),
                        Recipe(1_600, 1_600, DerivativeFormat.WebP),
                    ]),
            ]);

        DerivativeNegotiationResult selected = registry.Negotiate(
            new DerivativeOutputRequest(
                new DerivativePresetId("viewer", 3),
                new DerivativeDimensions(1_600, 1_600),
                [DerivativeFormat.WebP, DerivativeFormat.Jpeg]));
        DerivativeNegotiationResult arbitrarySize = registry.Negotiate(
            new DerivativeOutputRequest(
                new DerivativePresetId("viewer", 3),
                new DerivativeDimensions(1_599, 1_599),
                [DerivativeFormat.WebP]));
        DerivativeNegotiationResult unsupportedFormat = registry.Negotiate(
            new DerivativeOutputRequest(
                new DerivativePresetId("viewer", 3),
                new DerivativeDimensions(1_600, 1_600),
                [DerivativeFormat.Png]));

        Assert.Equal(DerivativeNegotiationStatus.Selected, selected.Status);
        Assert.Equal(DerivativeFormat.WebP, selected.Recipe?.Format);
        Assert.Equal(DerivativeNegotiationStatus.OutputNotAllowed, arbitrarySize.Status);
        Assert.Null(arbitrarySize.Recipe);
        Assert.Equal(
            DerivativeNegotiationStatus.FormatNotAcceptable,
            unsupportedFormat.Status);
        Assert.Null(unsupportedFormat.Recipe);
    }

    [Fact]
    public void Negotiation_rejects_unknown_presets_and_invalid_format_values()
    {
        DerivativePresetRegistry registry = new([Preset("thumb", 1, 256)]);

        DerivativeNegotiationResult missing = registry.Negotiate(
            new DerivativeOutputRequest(
                new DerivativePresetId("thumb", 2),
                new DerivativeDimensions(256, 256),
                [DerivativeFormat.WebP]));

        Assert.Equal(DerivativeNegotiationStatus.PresetNotFound, missing.Status);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DerivativeOutputRequest(
                new DerivativePresetId("thumb", 1),
                new DerivativeDimensions(256, 256),
                [(DerivativeFormat)99]));
    }

    [Fact]
    public void Standard_registry_exposes_only_versioned_named_bounded_outputs()
    {
        DerivativePresetRegistry registry = DerivativePresetRegistry.Standard;

        DerivativeNegotiationResult thumb = registry.Negotiate(
            new DerivativeOutputRequest(
                new DerivativePresetId("thumb", 1),
                new DerivativeDimensions(256, 256),
                [DerivativeFormat.WebP]));
        DerivativeNegotiationResult oversized = registry.Negotiate(
            new DerivativeOutputRequest(
                new DerivativePresetId("viewer", 1),
                new DerivativeDimensions(2_401, 2_401),
                [DerivativeFormat.WebP]));

        Assert.Equal(DerivativeNegotiationStatus.Selected, thumb.Status);
        Assert.False(thumb.Recipe?.AllowUpscale);
        Assert.Equal(
            DerivativeMetadataBehavior.StripSensitive,
            thumb.Recipe?.MetadataBehavior);
        Assert.Equal(DerivativeNegotiationStatus.OutputNotAllowed, oversized.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Viewer")]
    [InlineData("viewer/private.jpg")]
    public void Preset_names_are_safe_stable_identifiers(string name)
    {
        Assert.Throws<ArgumentException>(() => new DerivativePresetId(name, 1));
    }

    private static DerivativePreset Preset(string name, int revision, int size) =>
        new(
            new DerivativePresetId(name, revision),
            [Recipe(size, size, DerivativeFormat.WebP)]);

    private static DerivativeRecipe Recipe(
        int width,
        int height,
        DerivativeFormat format) =>
        new(
            schemaVersion: 1,
            new DerivativeDimensions(width, height),
            DerivativeFit.Contain,
            format,
            quality: 82,
            format == DerivativeFormat.Jpeg
                ? DerivativeBackground.White
                : DerivativeBackground.Transparent,
            allowUpscale: false,
            DerivativeMetadataBehavior.StripSensitive);
}
