using System.Globalization;
using System.Security.Cryptography;
using Vistara.Application.Common.Imaging;
using VipsEnums = global::NetVips.Enums;
using VipsException = global::NetVips.VipsException;
using VipsImage = global::NetVips.Image;

namespace Vistara.Imaging.NetVips;

public sealed class NetVipsImageProcessor : IImageProcessor
{
    private static readonly IProgress<int> NoProgress = new Progress<int>(_ => { });
    private static readonly ImageProcessorCapabilities ProcessorCapabilities = new()
    {
        InputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
        OutputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
        MaxFrames = 1,
        SupportsAutoOrientation = true,
        SupportsColorProfileNormalization = true,
        SupportsSensitiveMetadataStripping = true,
        StreamRequirements = new ImageStreamRequirements(false, false),
    };

    private readonly NetVipsRuntimeState _runtime;

    public NetVipsImageProcessor()
        : this(NetVipsRuntime.State)
    {
    }

    internal NetVipsImageProcessor(NetVipsRuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public ImageProcessorCapabilities Capabilities => ProcessorCapabilities;

    public ImagePipelineFingerprint PipelineFingerprint => _runtime.PipelineFingerprint;

    public async ValueTask<ImageInspection> InspectAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);

        using var deadline = new CancellationTokenSource(limits.ProcessingDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using LoadedImage loaded = await LoadAsync(source, limits, linked.Token);
            ImageInspection inspection = InspectLoaded(loaded, limits);
            ForceDecode(loaded.Image, linked.Token);
            loaded.CompleteRead();
            return CopyWithEncodedBytes(inspection, loaded.EncodedBytes);
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw DeadlineExceeded();
        }
        catch (VipsException) when (linked.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw DeadlineExceeded();
        }
        catch (VipsException)
        {
            throw MalformedImage();
        }
    }

    public async ValueTask<ImageTransformResult> TransformAsync(
        IReplayableImageSource source,
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateRecipe(destination, recipe, limits);

        using var deadline = new CancellationTokenSource(limits.ProcessingDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using LoadedImage loaded = await LoadAsync(source, limits, linked.Token);
            _ = InspectLoaded(loaded, limits);

            var intermediates = new List<VipsImage>();
            try
            {
                VipsImage current = Add(intermediates, loaded.Image.Autorot());
                current = Add(intermediates, NormalizeColor(current));
                current = Add(intermediates, PrepareForOutput(current, recipe.OutputFormat));
                current = ApplyResize(intermediates, current, recipe);
                current = Add(intermediates, StripMetadata(current));
                ValidateOutputDimensions(current, limits);
                current.SetProgress(NoProgress, linked.Token);
                linked.Token.ThrowIfCancellationRequested();

                using var hashingDestination = new HashingWriteStream(destination);
                WriteDeterministically(current, hashingDestination, recipe, _runtime.Savers);
                loaded.CompleteRead();
                linked.Token.ThrowIfCancellationRequested();

                long bytesWritten = hashingDestination.BytesWritten;
                if (bytesWritten <= 0)
                {
                    throw new ImageProcessorException(
                        ImageProcessorErrorCode.MalformedImage,
                        "The image encoder produced no output.");
                }

                ImageInspection output = InspectOutput(
                    current,
                    recipe.OutputFormat,
                    bytesWritten);
                return new ImageTransformResult(
                    output,
                    bytesWritten,
                    new ImageSha256(hashingDestination.GetSha256()),
                    recipe.Fingerprint,
                    PipelineFingerprint);
            }
            finally
            {
                for (int index = intermediates.Count - 1; index >= 0; index--)
                {
                    intermediates[index].Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw DeadlineExceeded();
        }
        catch (VipsException) when (linked.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw DeadlineExceeded();
        }
        catch (VipsException)
        {
            throw MalformedImage();
        }
    }

    private static async ValueTask<LoadedImage> LoadAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        if (source.Length is > 0 and var declaredLength &&
            declaredLength > limits.MaxEncodedBytes)
        {
            throw LimitExceeded("The encoded image exceeds the configured byte limit.");
        }

        Stream stream = await source.OpenReadAsync(cancellationToken);
        if (stream is null || !stream.CanRead)
        {
            stream?.Dispose();
            throw new ImageProcessorException(
                ImageProcessorErrorCode.InputNotReplayable,
                "The image source did not provide a readable stream.");
        }

        long? encodedLength = GetEncodedLength(source, stream);
        if (encodedLength > limits.MaxEncodedBytes)
        {
            stream.Dispose();
            throw LimitExceeded("The encoded image exceeds the configured byte limit.");
        }

        var limitedStream = new LimitedReadStream(
            stream,
            encodedLength.HasValue ? long.MaxValue : limits.MaxEncodedBytes,
            cancellationToken);
        var replayStream = new ProbeReplayStream(limitedStream);
        VipsImage? image = null;
        try
        {
            string? loader = VipsImage.FindLoadStream(replayStream);
            ReadOnlyMemory<byte> probe = replayStream.GetProbePrefix(4096);
            replayStream.RewindAfterProbe();
            ImageFormat format = MapLoader(loader, probe.Span);
            var options = new global::NetVips.VOption();
            if (format == ImageFormat.WebP)
            {
                options.Add("n", -1);
            }

            image = VipsImage.NewFromStream(
                replayStream,
                string.Empty,
                access: VipsEnums.Access.Sequential,
                failOn: VipsEnums.FailOn.Warning,
                kwargs: options);
            return new LoadedImage(
                image,
                format,
                replayStream,
                encodedLength);
        }
        catch
        {
            image?.Dispose();
            replayStream.Dispose();
            throw;
        }
    }

    private static ImageInspection InspectLoaded(
        LoadedImage loaded,
        ImageDecodeLimits limits)
    {
        VipsImage image = loaded.Image;
        int frameCount = GetFrameCount(image);
        int pageHeight = frameCount > 1 ? image.PageHeight : image.Height;
        int width = image.Width;
        long aggregatePixels;
        long estimatedDecodedBytes;
        try
        {
            aggregatePixels = checked((long)width * pageHeight * frameCount);
            estimatedDecodedBytes = checked(
                aggregatePixels * Math.Max(1, image.Bands) * BytesPerSample(image.Format));
        }
        catch (OverflowException)
        {
            throw LimitExceeded("The decoded image dimensions exceed configured limits.");
        }

        if (width > limits.MaxWidth ||
            pageHeight > limits.MaxHeight ||
            frameCount > limits.MaxFrames ||
            aggregatePixels > limits.MaxAggregatePixels ||
            estimatedDecodedBytes > limits.MaxEstimatedDecodedBytes)
        {
            throw LimitExceeded("The decoded image exceeds configured limits.");
        }

        return new ImageInspection(
            loaded.Format,
            MediaTypeFor(loaded.Format),
            width,
            pageHeight,
            frameCount,
            aggregatePixels,
            GetPixelFormat(image),
            GetOrientation(image),
            InspectPrivacy(image),
            Math.Max(1, loaded.EncodedBytes),
            estimatedDecodedBytes);
    }

    private static void ForceDecode(VipsImage image, CancellationToken cancellationToken)
    {
        image.SetProgress(NoProgress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _ = image.Avg();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private VipsImage NormalizeColor(VipsImage image)
    {
        if (image.Contains("icc-profile-data"))
        {
            if (!_runtime.SupportsIccTransform)
            {
                throw new ImageProcessorException(
                    ImageProcessorErrorCode.Unsupported,
                    "The native imaging runtime cannot normalize embedded color profiles.");
            }

            return image.IccTransform(
                "srgb",
                intent: VipsEnums.Intent.Relative,
                blackPointCompensation: true,
                embedded: true,
                depth: 8);
        }

        VipsImage normalized = image.Colourspace(VipsEnums.Interpretation.Srgb);
        if (normalized.Format == VipsEnums.BandFormat.Uchar)
        {
            return normalized;
        }

        using (normalized)
        {
            return normalized.Cast(
                VipsEnums.BandFormat.Uchar,
                shift: normalized.Format == VipsEnums.BandFormat.Ushort);
        }
    }

    private static VipsImage PrepareForOutput(
        VipsImage image,
        ImageFormat outputFormat)
    {
        if (outputFormat == ImageFormat.Jpeg && image.HasAlpha())
        {
            return image.Flatten(background: [255, 255, 255]);
        }

        return image.Copy();
    }

    private static VipsImage ApplyResize(
        List<VipsImage> intermediates,
        VipsImage image,
        CanonicalTransformRecipe recipe)
    {
        return recipe.ResizeMode switch
        {
            ImageResizeMode.Fit => ResizeFit(intermediates, image, recipe),
            ImageResizeMode.Fill => ResizeFill(intermediates, image, recipe),
            ImageResizeMode.Crop => ResizeCrop(intermediates, image, recipe),
            ImageResizeMode.Pad => ResizePad(intermediates, image, recipe),
            _ => throw InvalidRecipe(),
        };
    }

    private static VipsImage ResizeFit(
        List<VipsImage> intermediates,
        VipsImage image,
        CanonicalTransformRecipe recipe)
    {
        double scale = Math.Min(
            (double)recipe.Width / image.Width,
            (double)recipe.Height / image.Height);
        if (!recipe.AllowUpscale)
        {
            scale = Math.Min(scale, 1);
        }

        int width = ScaledDimension(image.Width, scale);
        int height = ScaledDimension(image.Height, scale);
        return ResizeExact(intermediates, image, width, height);
    }

    private static VipsImage ResizeFill(
        List<VipsImage> intermediates,
        VipsImage image,
        CanonicalTransformRecipe recipe)
    {
        int width = recipe.AllowUpscale
            ? recipe.Width
            : Math.Min(recipe.Width, image.Width);
        int height = recipe.AllowUpscale
            ? recipe.Height
            : Math.Min(recipe.Height, image.Height);
        return ResizeExact(intermediates, image, width, height);
    }

    private static VipsImage ResizeCrop(
        List<VipsImage> intermediates,
        VipsImage image,
        CanonicalTransformRecipe recipe)
    {
        double scale = Math.Max(
            (double)recipe.Width / image.Width,
            (double)recipe.Height / image.Height);
        if (!recipe.AllowUpscale)
        {
            scale = Math.Min(scale, 1);
        }

        int scaledWidth = ScaledDimension(image.Width, scale);
        int scaledHeight = ScaledDimension(image.Height, scale);
        VipsImage scaled = ResizeExact(
            intermediates,
            image,
            scaledWidth,
            scaledHeight);
        int cropWidth = Math.Min(recipe.Width, scaled.Width);
        int cropHeight = Math.Min(recipe.Height, scaled.Height);
        int left = AnchorOffset(scaled.Width - cropWidth, recipe.Anchor, horizontal: true);
        int top = AnchorOffset(scaled.Height - cropHeight, recipe.Anchor, horizontal: false);
        return Add(intermediates, scaled.Crop(left, top, cropWidth, cropHeight));
    }

    private static VipsImage ResizePad(
        List<VipsImage> intermediates,
        VipsImage image,
        CanonicalTransformRecipe recipe)
    {
        VipsImage fitted = ResizeFit(intermediates, image, recipe);
        int left = AnchorOffset(recipe.Width - fitted.Width, recipe.Anchor, horizontal: true);
        int top = AnchorOffset(recipe.Height - fitted.Height, recipe.Anchor, horizontal: false);
        double[] background = Enumerable.Repeat(255d, fitted.Bands).ToArray();
        return Add(
            intermediates,
            fitted.Embed(
                left,
                top,
                recipe.Width,
                recipe.Height,
                extend: VipsEnums.Extend.Background,
                background: background));
    }

    private static VipsImage ResizeExact(
        List<VipsImage> intermediates,
        VipsImage image,
        int width,
        int height)
    {
        if (image.Width == width && image.Height == height)
        {
            return image;
        }

        return Add(
            intermediates,
            image.Resize(
                (double)width / image.Width,
                kernel: VipsEnums.Kernel.Lanczos3,
                vscale: (double)height / image.Height));
    }

    private static VipsImage StripMetadata(VipsImage image)
    {
        string[] fields = image.GetFields() ?? [];
        return image.Mutate(mutable =>
        {
            foreach (string field in fields)
            {
                if (IsRemovableMetadata(field))
                {
                    _ = mutable.Remove(field);
                }
            }
        });
    }

    private static void WriteDeterministically(
        VipsImage image,
        Stream destination,
        CanonicalTransformRecipe recipe,
        NetVipsSaverSupport savers)
    {
        switch (recipe.OutputFormat)
        {
            case ImageFormat.Jpeg:
                image.JpegsaveStream(
                    destination,
                    q: recipe.Quality,
                    optimizeCoding: true,
                    interlace: false,
                    trellisQuant: false,
                    overshootDeringing: false,
                    optimizeScans: false,
                    quantTable: 0,
                    subsampleMode: VipsEnums.ForeignSubsample.On,
                    restartInterval: 0,
                    keep: VipsEnums.ForeignKeep.None);
                break;
            case ImageFormat.Png:
                image.PngsaveStream(
                    destination,
                    compression: 9,
                    interlace: false,
                    filter: VipsEnums.ForeignPngFilter.All,
                    palette: false,
                    q: recipe.Quality,
                    dither: 0,
                    bitdepth: 8,
                    effort: 7,
                    keep: VipsEnums.ForeignKeep.None);
                break;
            case ImageFormat.WebP:
                // Arguments the installed libvips does not advertise are left
                // unset rather than sent and rejected, and the set in use is
                // declared by NetVipsSaverSupport.WebpArgumentsInUse. Only
                // `exact` changes the encoded bytes when unset, which is why the
                // pipeline fingerprint carries its state; `target_size`,
                // `smart_deblock`, and `passes` are set to the values libvips
                // already applies when those arguments are absent.
                image.WebpsaveStream(
                    destination,
                    q: recipe.Quality,
                    lossless: false,
                    exact: savers.WebpExact ? true : null,
                    preset: VipsEnums.ForeignWebpPreset.Default,
                    smartSubsample: false,
                    nearLossless: false,
                    alphaQ: recipe.Quality,
                    minSize: false,
                    effort: 4,
                    targetSize: savers.WebpTargetSize ? 0 : null,
                    mixed: false,
                    smartDeblock: savers.WebpSmartDeblock ? false : null,
                    passes: savers.WebpPasses ? 1 : null,
                    keep: VipsEnums.ForeignKeep.None);
                break;
            default:
                throw InvalidRecipe();
        }
    }

    private static ImageInspection InspectOutput(
        VipsImage image,
        ImageFormat format,
        long encodedBytes)
    {
        long pixels = checked((long)image.Width * image.Height);
        long estimatedBytes = checked(
            pixels * Math.Max(1, image.Bands) * BytesPerSample(image.Format));
        return new ImageInspection(
            format,
            MediaTypeFor(format),
            image.Width,
            image.Height,
            1,
            pixels,
            GetPixelFormat(image),
            ImageOrientation.Normal,
            new ImagePrivacyMetadata(false, false, false, false, false, false, false),
            encodedBytes,
            estimatedBytes);
    }

    private static void ValidateRecipe(
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits)
    {
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The image destination must be writable.", nameof(destination));
        }

        if (recipe.SchemaVersion != 1 ||
            recipe.Width > limits.MaxWidth ||
            recipe.Height > limits.MaxHeight ||
            checked((long)recipe.Width * recipe.Height) > limits.MaxAggregatePixels)
        {
            throw InvalidRecipe();
        }
    }

    private static void ValidateOutputDimensions(
        VipsImage image,
        ImageDecodeLimits limits)
    {
        long pixels = checked((long)image.Width * image.Height);
        long bytes = checked(
            pixels * Math.Max(1, image.Bands) * BytesPerSample(image.Format));
        if (image.Width > limits.MaxWidth ||
            image.Height > limits.MaxHeight ||
            pixels > limits.MaxAggregatePixels ||
            bytes > limits.MaxEstimatedDecodedBytes)
        {
            throw LimitExceeded("The transformed image exceeds configured limits.");
        }
    }

    private static int GetFrameCount(VipsImage image)
    {
        int pages = 1;
        if (image.Contains("n-pages"))
        {
            pages = Convert.ToInt32(image.Get("n-pages"), CultureInfo.InvariantCulture);
        }
        else if (image.PageHeight > 0 && image.Height > image.PageHeight)
        {
            pages = image.Height / image.PageHeight;
        }

        if (pages <= 0 || image.PageHeight <= 0 || image.Height % image.PageHeight != 0)
        {
            throw MalformedImage();
        }

        return pages;
    }

    private static ImageOrientation GetOrientation(VipsImage image)
    {
        if (!image.Contains("orientation"))
        {
            return ImageOrientation.Normal;
        }

        int value = Convert.ToInt32(image.Get("orientation"), CultureInfo.InvariantCulture);
        return Enum.IsDefined(typeof(ImageOrientation), value)
            ? (ImageOrientation)value
            : throw MalformedImage();
    }

    private static ImagePrivacyMetadata InspectPrivacy(VipsImage image)
    {
        string[] fields = image.GetFields() ?? [];
        bool Has(string value) => fields.Any(field =>
            field.Contains(value, StringComparison.OrdinalIgnoreCase));
        return new ImagePrivacyMetadata(
            Has("exif") || image.Contains("orientation"),
            Has("gps"),
            Has("xmp"),
            Has("iptc"),
            Has("comment") || Has("description"),
            Has("thumbnail"),
            fields.Any(field =>
                field.Contains("document-name", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("image-title", StringComparison.OrdinalIgnoreCase)));
    }

    private static ImagePixelFormat GetPixelFormat(VipsImage image)
    {
        bool sixteenBit = image.Format is VipsEnums.BandFormat.Ushort
            or VipsEnums.BandFormat.Short;
        bool alpha = image.HasAlpha();
        bool gray = image.Bands <= 2 &&
            image.Interpretation is VipsEnums.Interpretation.Bw
                or VipsEnums.Interpretation.Grey16;
        return (gray, alpha, sixteenBit) switch
        {
            (true, false, false) => ImagePixelFormat.Gray8,
            (true, true, false) => ImagePixelFormat.GrayAlpha8,
            (false, false, false) => ImagePixelFormat.Rgb8,
            (false, true, false) => ImagePixelFormat.Rgba8,
            (true, false, true) => ImagePixelFormat.Rgb16,
            (true, true, true) => ImagePixelFormat.Rgba16,
            (false, false, true) => ImagePixelFormat.Rgb16,
            (false, true, true) => ImagePixelFormat.Rgba16,
        };
    }

    private static int BytesPerSample(VipsEnums.BandFormat format) =>
        format switch
        {
            VipsEnums.BandFormat.Uchar or VipsEnums.BandFormat.Char => 1,
            VipsEnums.BandFormat.Ushort or VipsEnums.BandFormat.Short => 2,
            VipsEnums.BandFormat.Uint or VipsEnums.BandFormat.Int or
                VipsEnums.BandFormat.Float => 4,
            VipsEnums.BandFormat.Double or VipsEnums.BandFormat.Complex => 8,
            VipsEnums.BandFormat.Dpcomplex => 16,
            _ => 16,
        };

    private static ImageFormat MapLoader(string? loader, ReadOnlySpan<byte> probe)
    {
        if (string.IsNullOrWhiteSpace(loader))
        {
            if (IsKnownUnsupported(probe))
            {
                throw new ImageProcessorException(
                    ImageProcessorErrorCode.UnsupportedFormat,
                    "The image format is not supported.");
            }

            throw MalformedImage();
        }

        if (loader.Contains("jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFormat.Jpeg;
        }

        if (loader.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFormat.Png;
        }

        if (loader.Contains("webp", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFormat.WebP;
        }

        throw new ImageProcessorException(
            ImageProcessorErrorCode.UnsupportedFormat,
            "The image format is not supported.");
    }

    private static bool IsKnownUnsupported(ReadOnlySpan<byte> probe)
    {
        if (probe.StartsWith("%PDF-"u8) ||
            probe.StartsWith("GIF87a"u8) ||
            probe.StartsWith("GIF89a"u8) ||
        probe.StartsWith("II*\0"u8) ||
        probe.StartsWith("MM\0*"u8))
        {
            return true;
        }

        string prefix = System.Text.Encoding.UTF8.GetString(probe);
        return prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static long? GetEncodedLength(
        IReplayableImageSource source,
        Stream stream)
    {
        if (source.Length is >= 0)
        {
            return source.Length;
        }

        if (!stream.CanSeek)
        {
            return null;
        }

        try
        {
            return checked(stream.Length - stream.Position);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static int ScaledDimension(int dimension, double scale) =>
        Math.Max(1, (int)Math.Round(dimension * scale, MidpointRounding.AwayFromZero));

    private static int AnchorOffset(
        int available,
        ImageAnchor anchor,
        bool horizontal)
    {
        if (available <= 0)
        {
            return 0;
        }

        if (horizontal)
        {
            return anchor switch
            {
                ImageAnchor.Left => 0,
                ImageAnchor.Right => available,
                _ => available / 2,
            };
        }

        return anchor switch
        {
            ImageAnchor.Top => 0,
            ImageAnchor.Bottom => available,
            _ => available / 2,
        };
    }

    private static bool IsRemovableMetadata(string field) =>
        field.Equals("filename", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("orientation", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("exif", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("gps", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("xmp", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("iptc", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("comment", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("description", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("document-name", StringComparison.OrdinalIgnoreCase);

    private static ImageMediaType MediaTypeFor(ImageFormat format) =>
        new(format switch
        {
            ImageFormat.Jpeg => "image/jpeg",
            ImageFormat.Png => "image/png",
            ImageFormat.WebP => "image/webp",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        });

    private static ImageInspection CopyWithEncodedBytes(
        ImageInspection inspection,
        long encodedBytes) =>
        new(
            inspection.Format,
            inspection.ContentType,
            inspection.Width,
            inspection.Height,
            inspection.FrameCount,
            inspection.AggregatePixels,
            inspection.PixelFormat,
            inspection.Orientation,
            inspection.Privacy,
            encodedBytes,
            inspection.EstimatedDecodedBytes);

    private static VipsImage Add(List<VipsImage> images, VipsImage image)
    {
        images.Add(image);
        return image;
    }

    private static ImageProcessorException LimitExceeded(string message) =>
        new(ImageProcessorErrorCode.DecodeLimitExceeded, message);

    private static ImageProcessorException DeadlineExceeded() =>
        LimitExceeded("The image processing deadline was exceeded.");

    private static ImageProcessorException InvalidRecipe() =>
        new(
            ImageProcessorErrorCode.InvalidRecipe,
            "The canonical image transform recipe is not supported.");

    private static ImageProcessorException MalformedImage() =>
        new(
            ImageProcessorErrorCode.MalformedImage,
            "The encoded image is malformed or truncated.");
}
