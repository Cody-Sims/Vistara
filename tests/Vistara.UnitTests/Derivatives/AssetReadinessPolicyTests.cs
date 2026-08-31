using Vistara.Application.Derivatives;
using Xunit;

namespace Vistara.UnitTests.Derivatives;

public sealed class AssetReadinessPolicyTests
{
    [Fact]
    public void Required_presets_match_the_specification_standard_set()
    {
        Assert.Equal(
            ["thumb", "grid", "viewer", "download-web"],
            AssetReadinessPolicy.RequiredPresetNames);
    }

    [Fact]
    public void Every_required_preset_satisfies_readiness()
    {
        Assert.True(
            AssetReadinessPolicy.IsSatisfiedBy(
                AssetReadinessPolicy.RequiredPresetNames));
    }

    [Theory]
    [InlineData("thumb")]
    [InlineData("grid")]
    [InlineData("viewer")]
    [InlineData("download-web")]
    public void A_missing_required_preset_withholds_readiness(string missing)
    {
        string[] present = [.. AssetReadinessPolicy.RequiredPresetNames
            .Where(preset => preset != missing)];

        Assert.False(AssetReadinessPolicy.IsSatisfiedBy(present));
    }

    [Fact]
    public void An_empty_derivative_set_withholds_readiness() =>
        Assert.False(AssetReadinessPolicy.IsSatisfiedBy([]));

    [Fact]
    public void Duplicate_and_unrelated_presets_do_not_substitute_for_required_ones()
    {
        Assert.False(
            AssetReadinessPolicy.IsSatisfiedBy(
                ["thumb", "thumb", "grid", "social-card"]));
        Assert.True(
            AssetReadinessPolicy.IsSatisfiedBy(
                ["thumb", "thumb", "grid", "viewer", "download-web", "social-card"]));
    }

    [Fact]
    public void Preset_matching_is_case_sensitive_and_ordinal() =>
        Assert.False(
            AssetReadinessPolicy.IsSatisfiedBy(
                ["Thumb", "Grid", "Viewer", "Download-Web"]));
}
