using NetVips;
using Vistara.Application.Common.Imaging;
using Vistara.Imaging.NetVips;
using Xunit;
using VipsImage = NetVips.Image;

namespace Vistara.Imaging.Tests;

public sealed class NetVipsImageProcessorTests
{
    private static readonly ImageDecodeLimits DefaultLimits = new(
        maxEncodedBytes: 5 * 1024 * 1024,
        maxWidth: 4096,
        maxHeight: 4096,
        maxAggregatePixels: 16_777_216,
        maxFrames: 1,
        maxEstimatedDecodedBytes: 128 * 1024 * 1024,
        processingDeadline: TimeSpan.FromSeconds(10));

    [Fact]
    public void Missing_native_libvips_has_a_clear_typed_failure()
    {
        if (NativeVipsAvailability.IsAvailable)
        {
            return;
        }

        ImageProcessorException exception = Assert.Throws<ImageProcessorException>(
            static () => new NetVipsImageProcessor());

        Assert.Equal(ImageProcessorErrorCode.Unsupported, exception.Code);
        Assert.Contains("Native libvips is unavailable", exception.Message, StringComparison.Ordinal);
    }

    [VipsFact]
    public void Capabilities_and_fingerprint_describe_the_deterministic_pipeline()
    {
        var processor = new NetVipsImageProcessor();

        Assert.Equal(
            [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
            processor.Capabilities.InputFormats);
        Assert.Equal(
            [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
            processor.Capabilities.OutputFormats);
        Assert.Equal(1, processor.Capabilities.MaxFrames);
        Assert.True(processor.Capabilities.SupportsAutoOrientation);
        Assert.True(processor.Capabilities.SupportsColorProfileNormalization);
        Assert.True(processor.Capabilities.SupportsSensitiveMetadataStripping);
        Assert.False(processor.Capabilities.StreamRequirements.RequiresSeekableInput);
        Assert.False(processor.Capabilities.StreamRequirements.MayOpenInputMoreThanOnce);

        string fingerprint = processor.PipelineFingerprint.Value;
        Assert.Contains("vistara-pipeline=2", fingerprint, StringComparison.Ordinal);
        Assert.Contains("vistara-recipe-schema=", fingerprint, StringComparison.Ordinal);
        Assert.Contains("netvips=3.2.0", fingerprint, StringComparison.Ordinal);
        Assert.Contains("libvips=", fingerprint, StringComparison.Ordinal);
        Assert.Contains("jpeg[", fingerprint, StringComparison.Ordinal);
        Assert.Contains("png[", fingerprint, StringComparison.Ordinal);
        Assert.Contains("webp[", fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, fingerprint, StringComparison.Ordinal);
    }

    [VipsTheory]
    [InlineData(ImageFormat.Jpeg, "image/jpeg")]
    [InlineData(ImageFormat.Png, "image/png")]
    [InlineData(ImageFormat.WebP, "image/webp")]
    public async Task Inspect_reports_generated_single_frame_images(
        ImageFormat format,
        string mediaType)
    {
        byte[] bytes = ProceduralFixtureFactory.CreateStill(format, 17, 11);
        var processor = new NetVipsImageProcessor();

        ImageInspection inspection = await processor.InspectAsync(
            new MemoryImageSource(bytes),
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal(format, inspection.Format);
        Assert.Equal(mediaType, inspection.ContentType.Value);
        Assert.Equal(17, inspection.Width);
        Assert.Equal(11, inspection.Height);
        Assert.Equal(1, inspection.FrameCount);
        Assert.Equal(187, inspection.AggregatePixels);
        Assert.Equal(bytes.LongLength, inspection.EncodedBytes);
        Assert.Equal(ImageOrientation.Normal, inspection.Orientation);
    }

    [VipsFact]
    public async Task Encoded_length_limit_is_enforced_before_opening_the_source()
    {
        var source = new TrackingImageSource(length: 101);
        var limits = Limits(maxEncodedBytes: 100);
        var processor = new NetVipsImageProcessor();

        ImageProcessorException exception = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                source,
                limits,
                CancellationToken.None));

        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, exception.Code);
        Assert.False(source.WasOpened);
    }

    [VipsFact]
    public async Task Inspection_rejects_dimension_pixel_and_decoded_memory_bombs()
    {
        byte[] compactLargeImage = ProceduralFixtureFactory.CreateStill(
            ImageFormat.Png,
            128,
            96);
        var processor = new NetVipsImageProcessor();

        ImageProcessorException dimension = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                new MemoryImageSource(compactLargeImage),
                Limits(maxWidth: 127, maxAggregatePixels: 12_192),
                CancellationToken.None));
        ImageProcessorException pixels = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                new MemoryImageSource(compactLargeImage),
                Limits(maxAggregatePixels: 12_287),
                CancellationToken.None));
        ImageProcessorException memory = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                new MemoryImageSource(compactLargeImage),
                Limits(maxEstimatedDecodedBytes: 36_863),
                CancellationToken.None));

        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, dimension.Code);
        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, pixels.Code);
        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, memory.Code);
    }

    [VipsTheory]
    [MemberData(nameof(MultiFrameInputs))]
    public async Task Inspection_rejects_multipage_and_animated_inputs(byte[] input)
    {
        var processor = new NetVipsImageProcessor();

        ImageProcessorException exception = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                new MemoryImageSource(input),
                DefaultLimits,
                CancellationToken.None));

        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, exception.Code);
    }

    public static TheoryData<byte[]> MultiFrameInputs =>
        new()
        {
            ProceduralFixtureFactory.CreateTwoPageWebP(),
        };

    [VipsTheory]
    [MemberData(nameof(InvalidInputs))]
    public async Task Inspection_rejects_corrupt_truncated_and_unsupported_inputs(
        byte[] input,
        ImageProcessorErrorCode expectedCode)
    {
        var processor = new NetVipsImageProcessor();

        ImageProcessorException exception = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                new MemoryImageSource(input),
                DefaultLimits,
                CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("secret-fixture-value", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<byte[], ImageProcessorErrorCode> InvalidInputs
    {
        get
        {
            byte[] jpeg = ProceduralFixtureFactory.CreateStill(ImageFormat.Jpeg, 24, 16);
            return new TheoryData<byte[], ImageProcessorErrorCode>
            {
                { [0x00, 0x01, 0x02, 0x03], ImageProcessorErrorCode.MalformedImage },
                { jpeg[..(jpeg.Length / 2)], ImageProcessorErrorCode.MalformedImage },
                {
                    "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>secret-fixture-value</text></svg>"u8.ToArray(),
                    ImageProcessorErrorCode.UnsupportedFormat
                },
                { "%PDF-1.7\n%%EOF"u8.ToArray(), ImageProcessorErrorCode.UnsupportedFormat },
                {
                    [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52, 0x02, 0x00],
                    ImageProcessorErrorCode.UnsupportedFormat
                },
            };
        }
    }

    [VipsTheory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.WebP)]
    public async Task Transform_writes_each_supported_format_without_buffering_the_destination(
        ImageFormat format)
    {
        byte[] input = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 80, 40);
        var destination = new WriteOnlyNonSeekableStream();
        var processor = new NetVipsImageProcessor();
        var recipe = Recipe(
            width: 32,
            height: 32,
            mode: ImageResizeMode.Fit,
            output: format);

        ImageTransformResult result = await processor.TransformAsync(
            new MemoryImageSource(input),
            destination,
            recipe,
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal(32, result.Output.Width);
        Assert.Equal(16, result.Output.Height);
        Assert.Equal(format, result.Output.Format);
        Assert.Equal(destination.BytesWritten, result.BytesWritten);
        Assert.Equal(recipe.Fingerprint, result.RecipeFingerprint);
        Assert.Equal(processor.PipelineFingerprint, result.PipelineFingerprint);
    }

    [VipsTheory]
    [InlineData(ImageResizeMode.Fit, 60, 30)]
    [InlineData(ImageResizeMode.Fill, 60, 60)]
    [InlineData(ImageResizeMode.Crop, 60, 60)]
    [InlineData(ImageResizeMode.Pad, 60, 60)]
    public async Task Transform_applies_canonical_resize_modes(
        ImageResizeMode mode,
        int expectedWidth,
        int expectedHeight)
    {
        byte[] input = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 120, 60);
        using var destination = new MemoryStream();
        var processor = new NetVipsImageProcessor();

        ImageTransformResult result = await processor.TransformAsync(
            new MemoryImageSource(input),
            destination,
            Recipe(60, 60, mode, ImageFormat.Png),
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal(expectedWidth, result.Output.Width);
        Assert.Equal(expectedHeight, result.Output.Height);
    }

    [VipsFact]
    public async Task Transform_crop_preserves_aspect_ratio_then_center_crops()
    {
        byte[] input = ProceduralFixtureFactory.CreateHorizontalBands();
        using var destination = new MemoryStream();
        var processor = new NetVipsImageProcessor();

        _ = await processor.TransformAsync(
            new MemoryImageSource(input),
            destination,
            Recipe(60, 60, ImageResizeMode.Crop, ImageFormat.Png),
            DefaultLimits,
            CancellationToken.None);

        using VipsImage output = VipsImage.NewFromBuffer(
            destination.ToArray(),
            string.Empty);
        using VipsImage red = output.ExtractBand(0);
        using VipsImage green = output.ExtractBand(1);
        using VipsImage blue = output.ExtractBand(2);
        Assert.InRange(red.Avg(), 0, 1);
        Assert.InRange(green.Avg(), 254, 255);
        Assert.InRange(blue.Avg(), 0, 1);
    }

    [VipsFact]
    public async Task Transform_does_not_upscale_pixels_unless_the_recipe_allows_it()
    {
        byte[] input = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 20, 10);
        var processor = new NetVipsImageProcessor();
        using var boundedDestination = new MemoryStream();
        using var upscaleDestination = new MemoryStream();

        ImageTransformResult bounded = await processor.TransformAsync(
            new MemoryImageSource(input),
            boundedDestination,
            Recipe(200, 200, ImageResizeMode.Fit, ImageFormat.Png, allowUpscale: false),
            DefaultLimits,
            CancellationToken.None);
        ImageTransformResult upscaled = await processor.TransformAsync(
            new MemoryImageSource(input),
            upscaleDestination,
            Recipe(200, 200, ImageResizeMode.Fit, ImageFormat.Png, allowUpscale: true),
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal((20, 10), (bounded.Output.Width, bounded.Output.Height));
        Assert.Equal((200, 100), (upscaled.Output.Width, upscaled.Output.Height));
    }

    [VipsFact]
    public async Task Pad_uses_the_canonical_opaque_white_background()
    {
        byte[] input = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 20, 10);
        var processor = new NetVipsImageProcessor();
        using var destination = new MemoryStream();

        await processor.TransformAsync(
            new MemoryImageSource(input),
            destination,
            Recipe(20, 20, ImageResizeMode.Pad, ImageFormat.Png),
            DefaultLimits,
            CancellationToken.None);

        using VipsImage output = VipsImage.NewFromBuffer(destination.ToArray());
        Assert.Equal([255d, 255d, 255d], output[0, 0]);
    }

    [VipsFact]
    public async Task Transform_normalizes_orientation_and_strips_private_metadata()
    {
        byte[] input = ProceduralFixtureFactory.CreateOrientedPrivateJpeg();
        var processor = new NetVipsImageProcessor();
        ImageInspection before = await processor.InspectAsync(
            new MemoryImageSource(input),
            DefaultLimits,
            CancellationToken.None);
        using var destination = new MemoryStream();

        ImageTransformResult result = await processor.TransformAsync(
            new MemoryImageSource(input),
            destination,
            Recipe(20, 20, ImageResizeMode.Fit, ImageFormat.Jpeg),
            DefaultLimits,
            CancellationToken.None);
        byte[] outputBytes = destination.ToArray();
        ImageInspection after = await processor.InspectAsync(
            new MemoryImageSource(outputBytes),
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal(ImageOrientation.Rotate90Clockwise, before.Orientation);
        Assert.True(before.Privacy.HasExif);
        Assert.Equal((2, 3), (result.Output.Width, result.Output.Height));
        Assert.Equal(ImageOrientation.Normal, after.Orientation);
        Assert.Equal(
            new ImagePrivacyMetadata(false, false, false, false, false, false, false),
            after.Privacy);
        Assert.Equal(-1, outputBytes.AsSpan().IndexOf("secret-fixture-value"u8));
    }

    [VipsFact]
    public async Task Inspect_does_not_report_libvips_stream_names_as_embedded_filenames()
    {
        byte[] input = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 8, 8);
        var processor = new NetVipsImageProcessor();

        ImageInspection inspection = await processor.InspectAsync(
            new MemoryImageSource(input),
            DefaultLimits,
            CancellationToken.None);

        Assert.False(inspection.Privacy.HasEmbeddedFileName);
    }

    [VipsFact]
    public async Task Transform_is_byte_for_byte_deterministic()
    {
        byte[] input = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 73, 41);
        var processor = new NetVipsImageProcessor();
        CanonicalTransformRecipe recipe = Recipe(
            31,
            29,
            ImageResizeMode.Crop,
            ImageFormat.WebP,
            quality: 81);
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        ImageTransformResult firstResult = await processor.TransformAsync(
            new MemoryImageSource(input),
            first,
            recipe,
            DefaultLimits,
            CancellationToken.None);
        ImageTransformResult secondResult = await processor.TransformAsync(
            new MemoryImageSource(input),
            second,
            recipe,
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(firstResult.Sha256, secondResult.Sha256);
    }

    [VipsFact]
    public async Task Cancellation_and_deadline_are_observed()
    {
        byte[] input = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 32, 32);
        var processor = new NetVipsImageProcessor();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await processor.InspectAsync(
                new MemoryImageSource(input),
                DefaultLimits,
                cancelled.Token));

        ImageProcessorException deadline = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                new DelayedImageSource(input, TimeSpan.FromMilliseconds(100)),
                Limits(processingDeadline: TimeSpan.FromMilliseconds(5)),
                CancellationToken.None));

        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, deadline.Code);
    }

    private static CanonicalTransformRecipe Recipe(
        int width,
        int height,
        ImageResizeMode mode,
        ImageFormat output,
        bool allowUpscale = false,
        int quality = 82) =>
        new(
            schemaVersion: 1,
            width,
            height,
            mode,
            ImageAnchor.Center,
            allowUpscale,
            output,
            quality,
            ImageMetadataPolicy.StripSensitive);

    private static ImageDecodeLimits Limits(
        long maxEncodedBytes = 5 * 1024 * 1024,
        int maxWidth = 4096,
        int maxHeight = 4096,
        long maxAggregatePixels = 16_777_216,
        int maxFrames = 1,
        long maxEstimatedDecodedBytes = 128 * 1024 * 1024,
        TimeSpan? processingDeadline = null) =>
        new(
            maxEncodedBytes,
            maxWidth,
            maxHeight,
            maxAggregatePixels,
            maxFrames,
            maxEstimatedDecodedBytes,
            processingDeadline ?? TimeSpan.FromSeconds(10));
}

public sealed class VipsFactAttribute : FactAttribute
{
    public VipsFactAttribute()
    {
        if (!NativeVipsAvailability.IsAvailable)
        {
            Skip = NativeVipsAvailability.SkipReason;
        }
    }
}

public sealed class VipsTheoryAttribute : TheoryAttribute
{
    public VipsTheoryAttribute()
    {
        if (!NativeVipsAvailability.IsAvailable)
        {
            Skip = NativeVipsAvailability.SkipReason;
        }
    }
}

internal static class NativeVipsAvailability
{
    public static bool IsAvailable { get; }

    public static string SkipReason { get; }

    static NativeVipsAvailability()
    {
        try
        {
            int major = global::NetVips.NetVips.Version(0);
            IsAvailable = major > 0;
            SkipReason = IsAvailable
                ? string.Empty
                : "Native libvips returned an invalid version.";
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            SkipReason =
                $"Native libvips is unavailable ({exception.GetType().Name}); imaging runtime tests were not run.";
        }
    }
}

internal static class ProceduralFixtureFactory
{
    private static readonly int[] AnimationDelay = [100, 100];

    public static byte[] CreateStill(ImageFormat format, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 3)];
        for (int index = 0; index < pixels.Length; index += 3)
        {
            int pixel = index / 3;
            pixels[index] = (byte)(pixel % 251);
            pixels[index + 1] = (byte)((pixel * 3) % 253);
            pixels[index + 2] = (byte)((pixel * 7) % 255);
        }

        using VipsImage image = VipsImage.NewFromMemoryCopy(
            pixels,
            width,
            height,
            3,
            Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        return Save(image, format);
    }

    public static byte[] CreateHorizontalBands()
    {
        const int width = 120;
        const int height = 60;
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 3;
                if (x < 30)
                {
                    pixels[offset] = 255;
                }
                else if (x < 90)
                {
                    pixels[offset + 1] = 255;
                }
                else
                {
                    pixels[offset + 2] = 255;
                }
            }
        }

        using VipsImage image = VipsImage.NewFromMemoryCopy(
            pixels,
            width,
            height,
            3,
            Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        return Save(image, ImageFormat.Png);
    }

    public static byte[] CreateTwoPageWebP()
    {
        using VipsImage first = VipsImage.Black(16, 12, bands: 3)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        using VipsImage second = (first + 127).Cast(Enums.BandFormat.Uchar);
        using VipsImage pages = VipsImage.Arrayjoin(
            [first, second],
            across: 1).Mutate(mutable =>
            {
                mutable.Set(GValue.GIntType, "page-height", 12);
                mutable.Set(GValue.ArrayIntType, "delay", AnimationDelay);
                mutable.Set(GValue.GIntType, "loop", 0);
            });
        return pages.WebpsaveBuffer(
            q: 80,
            effort: 4,
            keep: Enums.ForeignKeep.All,
            pageHeight: 12);
    }

    public static byte[] CreateOrientedPrivateJpeg()
    {
        byte[] pixels =
        [
            255, 0, 0, 0, 255, 0, 0, 0, 255,
            255, 255, 0, 0, 255, 255, 255, 0, 255,
        ];
        using VipsImage image = VipsImage.NewFromMemoryCopy(
            pixels,
            3,
            2,
            3,
            Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using VipsImage privateImage = image.Mutate(mutable =>
        {
            mutable.Set(GValue.GIntType, "orientation", 6);
            mutable.Set(GValue.GStrType, "comment", "secret-fixture-value");
            mutable.Set(GValue.GStrType, "filename", "secret-fixture-value.jpg");
            mutable.Set(
                GValue.GStrType,
                "exif-ifd3-GPSLatitude",
                "1/1 2/1 3/1 (secret-fixture-value, ASCII, 24 components, 24 bytes)");
        });
        return privateImage.JpegsaveBuffer(
            q: 90,
            keep: Enums.ForeignKeep.All,
            subsampleMode: Enums.ForeignSubsample.Off);
    }

    private static byte[] Save(VipsImage image, ImageFormat format) =>
        format switch
        {
            ImageFormat.Jpeg => image.JpegsaveBuffer(
                q: 90,
                optimizeCoding: true,
                keep: Enums.ForeignKeep.None,
                subsampleMode: Enums.ForeignSubsample.Off),
            ImageFormat.Png => image.PngsaveBuffer(
                compression: 9,
                filter: Enums.ForeignPngFilter.All,
                keep: Enums.ForeignKeep.None),
            ImageFormat.WebP => image.WebpsaveBuffer(
                q: 90,
                effort: 4,
                keep: Enums.ForeignKeep.None),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
}
