using NetVips;
using Vistara.Application.Common.Imaging;
using Vistara.Imaging.NetVips;
using Xunit;
using VipsImage = NetVips.Image;

namespace Vistara.Imaging.Tests;

/// <summary>
/// libvips only advertises the WebP <c>exact</c> save argument from 8.18, and
/// <c>target_size</c>, <c>passes</c>, and <c>smart_deblock</c> from 8.16.
/// Distributions that ship 8.15 (Ubuntu 24.04's <c>libvips42</c>) therefore
/// reject those arguments, so the encoder has to ask the installed runtime what
/// it supports and say so in the pipeline fingerprint.
/// </summary>
public sealed class WebpExactCapabilityTests
{
    private static readonly string[] JpegArguments =
    [
        "Q",
        "optimize_coding",
        "interlace",
        "trellis_quant",
        "overshoot_deringing",
        "optimize_scans",
        "quant_table",
        "subsample_mode",
        "restart_interval",
        "keep",
        "background",
        "page_height",
        "profile",
    ];

    private static readonly string[] PngArguments =
    [
        "Q",
        "compression",
        "interlace",
        "filter",
        "palette",
        "dither",
        "bitdepth",
        "effort",
        "keep",
        "background",
        "page_height",
        "profile",
    ];

    /// <summary>Optional arguments advertised by libvips 8.15 webpsave_target.</summary>
    private static readonly string[] LibVips815WebpArguments =
    [
        "Q",
        "lossless",
        "preset",
        "smart_subsample",
        "near_lossless",
        "alpha_q",
        "min_size",
        "kmin",
        "kmax",
        "effort",
        "reduction_effort",
        "mixed",
        "keep",
        "background",
        "page_height",
        "profile",
        "strip",
    ];

    /// <summary>Optional arguments advertised by libvips 8.18 webpsave_target.</summary>
    private static readonly string[] LibVips818WebpArguments =
    [
        .. LibVips815WebpArguments,
        "exact",
        "target_size",
        "passes",
        "smart_deblock",
    ];

    private static readonly ImagePrivacyMetadata NoPrivateMetadata =
        new(false, false, false, false, false, false, false);

    private static readonly ImageDecodeLimits DefaultLimits = new(
        maxEncodedBytes: 5 * 1024 * 1024,
        maxWidth: 4096,
        maxHeight: 4096,
        maxAggregatePixels: 16_777_216,
        maxFrames: 1,
        maxEstimatedDecodedBytes: 128 * 1024 * 1024,
        processingDeadline: TimeSpan.FromSeconds(10));

    [Fact]
    public void Runtime_without_webp_exact_reports_the_gap_in_the_fingerprint()
    {
        NetVipsRuntimeState state = State(8, 15, 1, LibVips815WebpArguments);

        Assert.False(state.Savers.WebpExact);
        Assert.False(state.Savers.WebpTargetSize);
        Assert.False(state.Savers.WebpPasses);
        Assert.False(state.Savers.WebpSmartDeblock);
        Assert.Contains("exact=unavailable", state.PipelineFingerprint.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("exact=true", state.PipelineFingerprint.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_with_webp_exact_reports_every_version_gated_argument()
    {
        NetVipsRuntimeState state = State(8, 18, 0, LibVips818WebpArguments);

        Assert.True(state.Savers.WebpExact);
        Assert.True(state.Savers.WebpTargetSize);
        Assert.True(state.Savers.WebpPasses);
        Assert.True(state.Savers.WebpSmartDeblock);
        Assert.Contains("exact=true", state.PipelineFingerprint.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_support_alone_changes_the_pipeline_fingerprint()
    {
        NetVipsRuntimeState withExact = State(8, 18, 0, LibVips818WebpArguments);
        NetVipsRuntimeState withoutExact = State(8, 18, 0, LibVips815WebpArguments);

        Assert.NotEqual(
            withExact.PipelineFingerprint.Value,
            withoutExact.PipelineFingerprint.Value);
        Assert.Contains("libvips=8.18.0", withExact.PipelineFingerprint.Value, StringComparison.Ordinal);
        Assert.Contains("libvips=8.18.0", withoutExact.PipelineFingerprint.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("webpsave_target", "keep")]
    [InlineData("webpsave_target", "effort")]
    [InlineData("jpegsave_target", "subsample_mode")]
    [InlineData("pngsave_target", "bitdepth")]
    public void Missing_required_save_arguments_fail_as_unsupported(
        string operation,
        string argument)
    {
        HashSet<string> jpeg = Arguments(JpegArguments);
        HashSet<string> png = Arguments(PngArguments);
        HashSet<string> webp = Arguments(LibVips818WebpArguments);
        HashSet<string> target = operation switch
        {
            "jpegsave_target" => jpeg,
            "pngsave_target" => png,
            _ => webp,
        };
        _ = target.Remove(argument);

        ImageProcessorException exception = Assert.Throws<ImageProcessorException>(
            () => NetVipsRuntime.CreateState(8, 18, 0, "3.2.0", jpeg, png, webp, true));

        Assert.Equal(ImageProcessorErrorCode.Unsupported, exception.Code);
        Assert.Contains(operation, exception.Message, StringComparison.Ordinal);
        Assert.Contains(argument, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Libvips_below_the_supported_baseline_fails_as_unsupported()
    {
        ImageProcessorException exception = Assert.Throws<ImageProcessorException>(
            () => State(8, 14, 5, LibVips815WebpArguments));

        Assert.Equal(ImageProcessorErrorCode.Unsupported, exception.Code);
        Assert.Contains("8.14.5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("8.15", exception.Message, StringComparison.Ordinal);
    }

    [VipsFact]
    public void Fingerprint_reports_what_the_installed_libvips_actually_advertises()
    {
        bool advertisesExact = NetVipsRuntime
            .OptionalArguments("webpsave_target")
            .Contains("exact");
        var processor = new NetVipsImageProcessor();

        string fingerprint = processor.PipelineFingerprint.Value;

        Assert.Equal(
            advertisesExact,
            fingerprint.Contains("exact=true", StringComparison.Ordinal));
        Assert.Equal(
            !advertisesExact,
            fingerprint.Contains("exact=unavailable", StringComparison.Ordinal));
    }

    [VipsFact]
    public async Task Webp_encodes_without_exact_support_and_stays_deterministic_and_private()
    {
        byte[] input = CreateTransparentPng();
        var processor = new NetVipsImageProcessor(LiveState(withExact: false));

        (byte[] first, ImageTransformResult firstResult) = await TransformAsync(processor, input);
        (byte[] second, ImageTransformResult secondResult) = await TransformAsync(processor, input);
        ImageInspection inspected = await processor.InspectAsync(
            new MemoryImageSource(first),
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(firstResult.Sha256, secondResult.Sha256);
        Assert.Equal(ImageFormat.WebP, inspected.Format);
        Assert.Equal(NoPrivateMetadata, inspected.Privacy);
        AssertNoRetainedMetadata(first);
        Assert.Contains(
            "exact=unavailable",
            firstResult.PipelineFingerprint.Value,
            StringComparison.Ordinal);
    }

    [VipsFact]
    public async Task Exact_support_is_used_only_when_libvips_advertises_it()
    {
        if (!NetVipsRuntime.OptionalArguments("webpsave_target").Contains("exact"))
        {
            return;
        }

        byte[] input = CreateTransparentPng();
        var exactProcessor = new NetVipsImageProcessor(LiveState(withExact: true));
        var fallbackProcessor = new NetVipsImageProcessor(LiveState(withExact: false));

        (byte[] exactBytes, ImageTransformResult exactResult) =
            await TransformAsync(exactProcessor, input);
        (byte[] exactRepeat, _) = await TransformAsync(exactProcessor, input);
        (byte[] fallbackBytes, ImageTransformResult fallbackResult) =
            await TransformAsync(fallbackProcessor, input);
        ImageInspection inspected = await exactProcessor.InspectAsync(
            new MemoryImageSource(exactBytes),
            DefaultLimits,
            CancellationToken.None);

        Assert.Equal(exactBytes, exactRepeat);
        Assert.NotEqual(exactBytes, fallbackBytes);
        Assert.NotEqual(
            exactResult.PipelineFingerprint.Value,
            fallbackResult.PipelineFingerprint.Value);
        Assert.Equal(NoPrivateMetadata, inspected.Privacy);
        AssertNoRetainedMetadata(exactBytes);
        AssertNoRetainedMetadata(fallbackBytes);
    }

    [VipsFact]
    public async Task Alpha_survives_the_webp_derivative_on_both_capability_paths()
    {
        byte[] input = CreateTransparentPng();
        var fallbackProcessor = new NetVipsImageProcessor(LiveState(withExact: false));

        (byte[] fallbackBytes, _) = await TransformAsync(fallbackProcessor, input);

        using VipsImage output = VipsImage.NewFromBuffer(fallbackBytes);
        Assert.True(output.HasAlpha());
        Assert.Equal((32, 32), (output.Width, output.Height));
    }

    [VipsFact]
    public void Every_webp_argument_the_encoder_sets_is_advertised_by_the_installed_libvips()
    {
        IReadOnlySet<string> advertised = NetVipsRuntime.OptionalArguments("webpsave_target");
        var processor = new NetVipsImageProcessor();

        NetVipsSaverSupport savers = NetVipsSaverSupport.FromOptionalArguments(
            NetVipsRuntime.OptionalArguments("jpegsave_target"),
            NetVipsRuntime.OptionalArguments("pngsave_target"),
            advertised);

        Assert.NotNull(processor);
        Assert.All(savers.WebpArgumentsInUse, argument => Assert.Contains(argument, advertised));
    }

    [VipsFact]
    public void Libvips_rejects_save_arguments_it_does_not_advertise()
    {
        using VipsImage image = VipsImage.Black(8, 8, bands: 3)
            .Copy(interpretation: Enums.Interpretation.Srgb);

        ArgumentException rejected = Assert.Throws<ArgumentException>(
            () => Operation.Call(
                "webpsave_buffer",
                new VOption { { "vistara_unadvertised_argument", true } },
                image));

        Assert.Contains(
            "does not support optional argument",
            rejected.Message,
            StringComparison.Ordinal);
        Assert.Contains("vistara_unadvertised_argument", rejected.Message, StringComparison.Ordinal);
    }

    private static async Task<(byte[] Bytes, ImageTransformResult Result)> TransformAsync(
        NetVipsImageProcessor processor,
        byte[] input)
    {
        using var destination = new MemoryStream();
        ImageTransformResult result = await processor.TransformAsync(
            new MemoryImageSource(input),
            destination,
            new CanonicalTransformRecipe(
                schemaVersion: 1,
                width: 32,
                height: 32,
                ImageResizeMode.Fit,
                ImageAnchor.Center,
                allowUpscale: false,
                ImageFormat.WebP,
                quality: 82,
                ImageMetadataPolicy.StripSensitive),
            DefaultLimits,
            CancellationToken.None);
        return (destination.ToArray(), result);
    }

    private static void AssertNoRetainedMetadata(byte[] encoded)
    {
        using VipsImage output = VipsImage.NewFromBuffer(encoded);
        string[] fields = output.GetFields() ?? [];

        Assert.DoesNotContain("exif-data", fields, StringComparer.Ordinal);
        Assert.DoesNotContain("xmp-data", fields, StringComparer.Ordinal);
        Assert.DoesNotContain("iptc-data", fields, StringComparer.Ordinal);
        Assert.DoesNotContain("icc-profile-data", fields, StringComparer.Ordinal);
        Assert.DoesNotContain("orientation", fields, StringComparer.Ordinal);
        Assert.DoesNotContain(
            fields,
            field => field.StartsWith("exif-", StringComparison.Ordinal));
        Assert.Equal(-1, encoded.AsSpan().IndexOf("secret-alpha-fixture"u8));
    }

    private static NetVipsRuntimeState State(
        int major,
        int minor,
        int patch,
        IEnumerable<string> webpArguments) =>
        NetVipsRuntime.CreateState(
            major,
            minor,
            patch,
            "3.2.0",
            Arguments(JpegArguments),
            Arguments(PngArguments),
            Arguments(webpArguments),
            supportsIccTransform: true);

    /// <summary>
    /// Mirrors the installed runtime, optionally hiding the WebP
    /// <c>exact</c> argument so the older-libvips encode path runs natively.
    /// </summary>
    private static NetVipsRuntimeState LiveState(bool withExact)
    {
        HashSet<string> webp = new(
            NetVipsRuntime.OptionalArguments("webpsave_target"),
            StringComparer.Ordinal);
        if (withExact)
        {
            _ = webp.Add("exact");
        }
        else
        {
            _ = webp.Remove("exact");
        }

        return NetVipsRuntime.CreateState(
            global::NetVips.NetVips.Version(0),
            global::NetVips.NetVips.Version(1),
            global::NetVips.NetVips.Version(2),
            "3.2.0",
            NetVipsRuntime.OptionalArguments("jpegsave_target"),
            NetVipsRuntime.OptionalArguments("pngsave_target"),
            webp,
            global::NetVips.NetVips.GetOperations().Any(operation =>
                operation.Contains("icc_transform", StringComparison.OrdinalIgnoreCase)));
    }

    private static HashSet<string> Arguments(IEnumerable<string> names) =>
        new(names, StringComparer.Ordinal);

    private static byte[] CreateTransparentPng()
    {
        const int width = 32;
        const int height = 32;
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < width * height; index++)
        {
            int offset = index * 4;
            pixels[offset] = (byte)(index % 251);
            pixels[offset + 1] = (byte)((index * 3) % 253);
            pixels[offset + 2] = (byte)((index * 7) % 255);
            pixels[offset + 3] = (byte)(index % 3 == 0 ? 0 : 255);
        }

        using VipsImage image = VipsImage.NewFromMemoryCopy(
            pixels,
            width,
            height,
            4,
            Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using VipsImage tagged = image.Mutate(mutable =>
            mutable.Set(GValue.GStrType, "comment", "secret-alpha-fixture"));
        return tagged.PngsaveBuffer(compression: 9, keep: Enums.ForeignKeep.All);
    }
}
