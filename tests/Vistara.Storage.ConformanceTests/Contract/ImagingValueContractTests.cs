using Vistara.Application.Common.Imaging;

namespace Vistara.Storage.ConformanceTests.Contract;

public sealed class ImagingValueContractTests
{
    [Fact]
    public void Decode_limits_reject_unbounded_or_internally_inconsistent_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ImageDecodeLimits(
                0,
                20_000,
                20_000,
                40_000_000,
                1,
                512 * 1024 * 1024,
                TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(
            () => new ImageDecodeLimits(
                50 * 1024 * 1024,
                20_000,
                20_000,
                500_000_000,
                1,
                512 * 1024 * 1024,
                TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Canonical_recipes_have_stable_fingerprints_and_no_arbitrary_operation_string()
    {
        CanonicalTransformRecipe first = new(
            schemaVersion: 1,
            width: 1_200,
            height: 900,
            ImageResizeMode.Fit,
            ImageAnchor.Center,
            allowUpscale: false,
            ImageFormat.WebP,
            quality: 82,
            ImageMetadataPolicy.StripSensitive);
        CanonicalTransformRecipe same = new(
            schemaVersion: 1,
            width: 1_200,
            height: 900,
            ImageResizeMode.Fit,
            ImageAnchor.Center,
            allowUpscale: false,
            ImageFormat.WebP,
            quality: 82,
            ImageMetadataPolicy.StripSensitive);
        CanonicalTransformRecipe changed = new(
            schemaVersion: 1,
            width: 1_024,
            height: 900,
            ImageResizeMode.Fit,
            ImageAnchor.Center,
            allowUpscale: false,
            ImageFormat.WebP,
            quality: 82,
            ImageMetadataPolicy.StripSensitive);

        Assert.Equal(first.Fingerprint, same.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Value.Length);
    }

    [Fact]
    public void Inspection_exposes_orientation_frames_pixels_and_privacy_metadata()
    {
        ImageInspection inspection = new(
            ImageFormat.Jpeg,
            new ImageMediaType("image/jpeg"),
            4_000,
            3_000,
            1,
            12_000_000,
            ImagePixelFormat.Rgb8,
            ImageOrientation.Rotate90Clockwise,
            new ImagePrivacyMetadata(
                HasExif: true,
                HasGps: true,
                HasXmp: false,
                HasIptc: false,
                HasComments: true,
                HasEmbeddedThumbnail: true,
                HasEmbeddedFileName: true),
            encodedBytes: 2_048_000,
            estimatedDecodedBytes: 36_000_000);

        Assert.Equal(1, inspection.FrameCount);
        Assert.Equal(12_000_000, inspection.AggregatePixels);
        Assert.Equal(ImageOrientation.Rotate90Clockwise, inspection.Orientation);
        Assert.True(inspection.Privacy.HasGps);
    }
}
