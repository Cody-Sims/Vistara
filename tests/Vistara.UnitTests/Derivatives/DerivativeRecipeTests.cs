using System.Globalization;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;

namespace Vistara.UnitTests.Derivatives;

public sealed class DerivativeRecipeTests
{
    [Fact]
    public void Canonical_equivalent_recipes_serialize_and_hash_identically()
    {
        DerivativeRecipe first = Recipe();
        DerivativeRecipe same = Recipe();

        Assert.Equal(first.CanonicalForm, same.CanonicalForm);
        Assert.Equal(first.Fingerprint, same.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length);
    }

    [Fact]
    public void Every_output_behavior_participates_in_the_fingerprint()
    {
        DerivativeRecipe baseline = Recipe();

        Assert.NotEqual(baseline.Fingerprint, Recipe(schemaVersion: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Recipe(width: 1_601).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Recipe(height: 1_201).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Recipe(fit: DerivativeFit.Cover).Fingerprint);
        Assert.NotEqual(
            baseline.Fingerprint,
            Recipe(
                format: DerivativeFormat.Jpeg,
                background: DerivativeBackground.White).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Recipe(quality: 81).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, Recipe(allowUpscale: true).Fingerprint);
        Assert.NotEqual(
            baseline.Fingerprint,
            Recipe(metadataBehavior: DerivativeMetadataBehavior.StripAll).Fingerprint);
    }

    [Fact]
    public void Canonicalization_is_independent_of_current_culture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            DerivativeRecipe localized = Recipe();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            DerivativeRecipe anotherCulture = Recipe();

            Assert.Equal(localized.CanonicalForm, anotherCulture.CanonicalForm);
            Assert.Equal(localized.Fingerprint, anotherCulture.Fingerprint);
            Assert.Equal(
                "{\"schema\":1,\"width\":1600,\"height\":1200,\"fit\":\"contain\"," +
                "\"format\":\"webp\",\"quality\":82,\"background\":\"transparent\"," +
                "\"upscale\":false,\"metadata\":\"strip-sensitive\"}",
                localized.CanonicalForm);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(8_193, 100)]
    [InlineData(100, 8_193)]
    public void Dimensions_reject_values_outside_the_derivative_limit(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DerivativeDimensions(width, height));
    }

    [Fact]
    public void Recipe_rejects_invalid_enums_quality_and_transparent_jpeg()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Recipe(fit: (DerivativeFit)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Recipe(format: (DerivativeFormat)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Recipe(background: (DerivativeBackground)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Recipe(metadataBehavior: (DerivativeMetadataBehavior)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Recipe(quality: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Recipe(quality: 101));
        Assert.Throws<ArgumentException>(
            () => Recipe(
                format: DerivativeFormat.Jpeg,
                background: DerivativeBackground.Transparent));
        Assert.Throws<ArgumentException>(
            () => Recipe(background: DerivativeBackground.White));
    }

    [Fact]
    public void Recipe_maps_to_the_existing_image_processor_contract()
    {
        DerivativeRecipe recipe = Recipe(fit: DerivativeFit.Cover);

        CanonicalTransformRecipe processorRecipe = recipe.ProcessorRecipe;

        Assert.Equal(ImageResizeMode.Crop, processorRecipe.ResizeMode);
        Assert.Equal(ImageAnchor.Center, processorRecipe.Anchor);
        Assert.Equal(ImageFormat.WebP, processorRecipe.OutputFormat);
        Assert.Equal(ImageMetadataPolicy.StripSensitive, processorRecipe.MetadataPolicy);
        Assert.Equal(recipe.Dimensions.Width, processorRecipe.Width);
        Assert.Equal(recipe.Dimensions.Height, processorRecipe.Height);
    }

    private static DerivativeRecipe Recipe(
        int schemaVersion = 1,
        int width = 1_600,
        int height = 1_200,
        DerivativeFit fit = DerivativeFit.Contain,
        DerivativeFormat format = DerivativeFormat.WebP,
        int quality = 82,
        DerivativeBackground background = DerivativeBackground.Transparent,
        bool allowUpscale = false,
        DerivativeMetadataBehavior metadataBehavior =
            DerivativeMetadataBehavior.StripSensitive) =>
        new(
            schemaVersion,
            new DerivativeDimensions(width, height),
            fit,
            format,
            quality,
            background,
            allowUpscale,
            metadataBehavior);
}
