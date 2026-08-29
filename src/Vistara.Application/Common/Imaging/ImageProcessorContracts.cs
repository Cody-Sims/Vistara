namespace Vistara.Application.Common.Imaging;

public sealed record ImageDecodeLimits
{
    public ImageDecodeLimits(
        long maxEncodedBytes,
        int maxWidth,
        int maxHeight,
        long maxAggregatePixels,
        int maxFrames,
        long maxEstimatedDecodedBytes,
        TimeSpan processingDeadline)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAggregatePixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEstimatedDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            processingDeadline,
            TimeSpan.Zero);
        if (maxAggregatePixels > checked((long)maxWidth * maxHeight * maxFrames))
        {
            throw new ArgumentException(
                "Aggregate pixel limits cannot exceed the dimension and frame ceiling.",
                nameof(maxAggregatePixels));
        }

        MaxEncodedBytes = maxEncodedBytes;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        MaxAggregatePixels = maxAggregatePixels;
        MaxFrames = maxFrames;
        MaxEstimatedDecodedBytes = maxEstimatedDecodedBytes;
        ProcessingDeadline = processingDeadline;
    }

    public long MaxEncodedBytes { get; }

    public int MaxWidth { get; }

    public int MaxHeight { get; }

    public long MaxAggregatePixels { get; }

    public int MaxFrames { get; }

    public long MaxEstimatedDecodedBytes { get; }

    public TimeSpan ProcessingDeadline { get; }
}

public interface IReplayableImageSource
{
    long? Length { get; }

    bool OpensSeekableStreams { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

public sealed record ImageStreamRequirements(
    bool RequiresSeekableInput,
    bool MayOpenInputMoreThanOnce);

public sealed record ImageProcessorCapabilities
{
    private IReadOnlyList<ImageFormat> _inputFormats = Array.Empty<ImageFormat>();
    private IReadOnlyList<ImageFormat> _outputFormats = Array.Empty<ImageFormat>();

    public IReadOnlyList<ImageFormat> InputFormats
    {
        get => _inputFormats;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _inputFormats = Array.AsReadOnly(value.Distinct().ToArray());
        }
    }

    public IReadOnlyList<ImageFormat> OutputFormats
    {
        get => _outputFormats;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _outputFormats = Array.AsReadOnly(value.Distinct().ToArray());
        }
    }

    public int MaxFrames { get; init; } = 1;

    public bool SupportsAutoOrientation { get; init; }

    public bool SupportsColorProfileNormalization { get; init; }

    public bool SupportsSensitiveMetadataStripping { get; init; }

    public ImageStreamRequirements StreamRequirements { get; init; } =
        new(false, false);
}

public sealed record ImageTransformResult
{
    public ImageTransformResult(
        ImageInspection output,
        long bytesWritten,
        ImageSha256 sha256,
        ImageSha256 recipeFingerprint,
        ImagePipelineFingerprint pipelineFingerprint)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesWritten);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(recipeFingerprint);
        ArgumentNullException.ThrowIfNull(pipelineFingerprint);
        Output = output;
        BytesWritten = bytesWritten;
        Sha256 = sha256;
        RecipeFingerprint = recipeFingerprint;
        PipelineFingerprint = pipelineFingerprint;
    }

    public ImageInspection Output { get; }

    public long BytesWritten { get; }

    public ImageSha256 Sha256 { get; }

    public ImageSha256 RecipeFingerprint { get; }

    public ImagePipelineFingerprint PipelineFingerprint { get; }
}

public enum ImageProcessorErrorCode
{
    Unsupported,
    UnsupportedFormat,
    DecodeLimitExceeded,
    InvalidRecipe,
    MalformedImage,
    InputNotReplayable,
    SeekableInputRequired,
}

public sealed class ImageProcessorException : Exception
{
    public ImageProcessorException(
        ImageProcessorErrorCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Code = code;
    }

    public ImageProcessorErrorCode Code { get; }
}

public interface IImageProcessor
{
    ImageProcessorCapabilities Capabilities { get; }

    ImagePipelineFingerprint PipelineFingerprint { get; }

    ValueTask<ImageInspection> InspectAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken);

    ValueTask<ImageTransformResult> TransformAsync(
        IReplayableImageSource source,
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken);
}
