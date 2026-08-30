using System.Diagnostics;
using NetVips;
using Vistara.Application.Common.Imaging;
using Vistara.Imaging.NetVips;
using VipsImage = NetVips.Image;

namespace Vistara.PerformanceTests;

internal sealed class TransformFixture
{
    private readonly byte[] _jpeg;
    private readonly CanonicalTransformRecipe _recipe = new(
        1,
        1200,
        1200,
        ImageResizeMode.Fit,
        ImageAnchor.Center,
        allowUpscale: false,
        ImageFormat.WebP,
        82,
        ImageMetadataPolicy.StripSensitive);
    private readonly ImageDecodeLimits _limits = new(
        50 * 1024 * 1024,
        10_000,
        10_000,
        40_000_000,
        1,
        512 * 1024 * 1024,
        TimeSpan.FromSeconds(30));

    internal TransformFixture()
    {
        _jpeg = CreateTwoMegapixelJpeg();
    }

    internal async Task<(double Milliseconds, double AllocatedMiB, long OutputBytes)>
        TransformAsync()
    {
        var processor = new NetVipsImageProcessor();
        var source = new MemoryImageSource(_jpeg);
        await using var destination = new MemoryStream();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        ImageTransformResult result = await processor.TransformAsync(
            source,
            destination,
            _recipe,
            _limits,
            CancellationToken.None);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (result.Output.Width != 1200 ||
            result.Output.Height != 600 ||
            result.Output.Format != ImageFormat.WebP ||
            destination.Length != result.BytesWritten)
        {
            throw new InvalidOperationException(
                "The production image processor returned an unexpected derivative.");
        }

        return (
            stopwatch.Elapsed.TotalMilliseconds,
            allocated / 1024d / 1024d,
            result.BytesWritten);
    }

    private static byte[] CreateTwoMegapixelJpeg()
    {
        const int width = 2000;
        const int height = 1000;
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
        return image.JpegsaveBuffer(
            q: 90,
            optimizeCoding: true,
            keep: Enums.ForeignKeep.None,
            subsampleMode: Enums.ForeignSubsample.Off);
    }

    private sealed class MemoryImageSource(byte[] bytes) : IReplayableImageSource
    {
        public long? Length => bytes.LongLength;

        public bool OpensSeekableStreams => true;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(
                new MemoryStream(bytes, writable: false));
        }
    }
}
