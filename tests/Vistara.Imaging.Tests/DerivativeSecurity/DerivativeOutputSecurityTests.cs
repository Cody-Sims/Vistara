using System.Buffers.Binary;
using System.Text;
using NetVips;
using Vistara.Application.Common.Imaging;
using Vistara.Imaging.NetVips;
using Xunit;
using VipsImage = NetVips.Image;

namespace Vistara.Imaging.Tests.DerivativeSecurity;

public sealed class DerivativeOutputSecurityTests
{
    private const string ExifSecret = "vistara-private-exif-7f83";
    private const string GpsSecret = "vistara-private-gps-28a1";
    private const string IptcSecret = "vistara-private-iptc-51c9";
    private const string XmpSecret = "vistara-private-xmp-b462";
    private const string CommentSecret = "vistara-private-comment-c810";
    private const string FileNameSecret = "private-family-name-19e4.jpg";
    private static readonly ImageDecodeLimits DefaultLimits = new(
        maxEncodedBytes: 5 * 1024 * 1024,
        maxWidth: 4096,
        maxHeight: 4096,
        maxAggregatePixels: 16_777_216,
        maxFrames: 1,
        maxEstimatedDecodedBytes: 128 * 1024 * 1024,
        processingDeadline: TimeSpan.FromSeconds(10));

    [VipsTheory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.WebP)]
    public async Task Derivative_bytes_strip_all_private_metadata_categories(
        ImageFormat outputFormat)
    {
        byte[] input = CreatePrivateMetadataJpeg();
        AssertPrivateFixtureContainsIndependentEvidence(input);
        var processor = new NetVipsImageProcessor();
        using var destination = new MemoryStream();

        await processor.TransformAsync(
            new MemoryImageSource(input),
            destination,
            Recipe(outputFormat),
            DefaultLimits,
            CancellationToken.None);

        byte[] output = destination.ToArray();
        using VipsImage decoded = VipsImage.NewFromBuffer(output);
        string[] fields = decoded.GetFields() ?? [];
        string[] forbiddenFieldFragments =
        [
            "exif",
            "gps",
            "iptc",
            "xmp",
            "comment",
            "description",
            "thumbnail",
            "document-name",
            "image-title",
        ];

        Assert.DoesNotContain(
            fields,
            field => forbiddenFieldFragments.Any(fragment =>
                field.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        if (decoded.Contains("filename"))
        {
            Assert.DoesNotContain(
                FileNameSecret,
                Convert.ToString(decoded.Get("filename"), System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        foreach (string secret in PrivateSecrets)
        {
            Assert.Equal(-1, output.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)));
        }
    }

    [VipsTheory]
    [MemberData(nameof(AdversarialInputs))]
    public async Task Malformed_and_truncated_corpus_returns_typed_failures(
        byte[] input,
        ImageProcessorErrorCode expectedCode)
    {
        var processor = new NetVipsImageProcessor();

        ImageProcessorException error = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                    new MemoryImageSource(input),
                    DefaultLimits,
                    CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(expectedCode, error.Code);
    }

    [VipsFact]
    public async Task Compact_dimension_bomb_is_rejected_before_any_output_is_written()
    {
        byte[] compactBomb;
        using (VipsImage image = VipsImage.Black(4096, 4096, bands: 3)
                   .Copy(interpretation: Enums.Interpretation.Srgb))
        {
            compactBomb = image.PngsaveBuffer(
                compression: 9,
                filter: Enums.ForeignPngFilter.All,
                keep: Enums.ForeignKeep.None);
        }

        Assert.True(compactBomb.Length < 1024 * 1024);
        var processor = new NetVipsImageProcessor();
        using var destination = new CountingWriteStream();
        ImageDecodeLimits limits = Limits(
            maxWidth: 1024,
            maxHeight: 1024,
            maxAggregatePixels: 1_048_576,
            maxEstimatedDecodedBytes: 4 * 1024 * 1024);

        ImageProcessorException error = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.TransformAsync(
                    new MemoryImageSource(compactBomb),
                    destination,
                    Recipe(ImageFormat.WebP),
                    limits,
                    CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, error.Code);
        Assert.Equal(0, destination.BytesWritten);
    }

    [VipsFact]
    public async Task Unknown_length_encoded_bomb_is_stopped_at_a_structural_read_bound()
    {
        byte[] image = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 32, 32);
        byte[] padded = new byte[512 * 1024];
        image.CopyTo(padded, 0);
        const int byteLimit = 32 * 1024;
        var source = new ChunkedUnknownLengthSource(padded, maximumChunkBytes: 4096);
        var processor = new NetVipsImageProcessor();

        ImageProcessorException error = await Assert.ThrowsAsync<ImageProcessorException>(
            async () => await processor.InspectAsync(
                    source,
                    Limits(maxEncodedBytes: byteLimit),
                    CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(ImageProcessorErrorCode.DecodeLimitExceeded, error.Code);
        Assert.InRange(source.BytesRead, byteLimit + 1, byteLimit + 4096);
    }

    public static TheoryData<byte[], ImageProcessorErrorCode> AdversarialInputs
    {
        get
        {
            byte[] jpeg = ProceduralFixtureFactory.CreateStill(ImageFormat.Jpeg, 31, 19);
            byte[] png = ProceduralFixtureFactory.CreateStill(ImageFormat.Png, 31, 19);
            byte[] webp = ProceduralFixtureFactory.CreateStill(ImageFormat.WebP, 31, 19);
            return new TheoryData<byte[], ImageProcessorErrorCode>
            {
                { [], ImageProcessorErrorCode.MalformedImage },
                { [0xFF, 0xD8, 0xFF, 0xE0], ImageProcessorErrorCode.MalformedImage },
                { jpeg[..Math.Max(4, jpeg.Length / 3)], ImageProcessorErrorCode.MalformedImage },
                { png[..Math.Max(12, png.Length / 2)], ImageProcessorErrorCode.MalformedImage },
                { webp[..Math.Max(16, webp.Length / 2)], ImageProcessorErrorCode.MalformedImage },
                { CreatePngWithInvalidChunkLength(), ImageProcessorErrorCode.MalformedImage },
            };
        }
    }

    private static IReadOnlyList<string> PrivateSecrets { get; } =
    [
        ExifSecret,
        GpsSecret,
        IptcSecret,
        XmpSecret,
        CommentSecret,
        FileNameSecret,
    ];

    private static byte[] CreatePrivateMetadataJpeg()
    {
        using VipsImage image = VipsImage.Black(48, 32, bands: 3)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        using VipsImage privateImage = image.Mutate(mutable =>
        {
            mutable.Set(
                GValue.GStrType,
                "exif-ifd0-ImageDescription",
                $"{ExifSecret} ({ExifSecret}, ASCII, 24 components, 24 bytes)");
            mutable.Set(
                GValue.GStrType,
                "exif-ifd3-GPSLatitude",
                $"1/1 2/1 3/1 ({GpsSecret}, ASCII, 24 components, 24 bytes)");
            mutable.Set(GValue.GStrType, "comment", CommentSecret);
            mutable.Set(GValue.GStrType, "exif-ifd0-DocumentName", FileNameSecret);
            mutable.Set(
                GValue.BlobType,
                "iptc-data",
                Encoding.UTF8.GetBytes(IptcSecret));
            mutable.Set(
                GValue.BlobType,
                "xmp-data",
                Encoding.UTF8.GetBytes(
                    $"<?xpacket begin=\"\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                    $"<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                    $"<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                    $"<dc:description>{XmpSecret}</dc:description></rdf:Description>" +
                    "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"));
        });
        byte[] jpeg = privateImage.JpegsaveBuffer(
            q: 90,
            keep: Enums.ForeignKeep.All,
            subsampleMode: Enums.ForeignSubsample.Off);
        jpeg = InjectJpegSegment(
            jpeg,
            marker: 0xFE,
            Encoding.UTF8.GetBytes(CommentSecret));
        jpeg = InjectJpegSegment(
            jpeg,
            marker: 0xE1,
            Encoding.UTF8.GetBytes(
                "http://ns.adobe.com/xap/1.0/\0" +
                $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><private>{XmpSecret}</private>" +
                $"<exif>{ExifSecret}</exif><gps>{GpsSecret}</gps>" +
                $"<iptc>{IptcSecret}</iptc><comment>{CommentSecret}</comment>" +
                $"<filename>{FileNameSecret}</filename></x:xmpmeta>"));
        return InjectJpegSegment(
            jpeg,
            marker: 0xED,
            Encoding.UTF8.GetBytes($"Photoshop 3.0\08BIM\u0004\u0004\0\0\0\0{IptcSecret}"));
    }

    private static void AssertPrivateFixtureContainsIndependentEvidence(byte[] input)
    {
        using VipsImage decoded = VipsImage.NewFromBuffer(input);
        string[] fields = decoded.GetFields() ?? [];
        Assert.Contains(fields, field =>
            field.Contains("exif", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fields, field =>
            field.Contains("gps", StringComparison.OrdinalIgnoreCase));
        foreach (string secret in PrivateSecrets)
        {
            Assert.True(
                input.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0,
                $"Fixture did not contain {secret}.");
        }
    }

    private static byte[] InjectJpegSegment(
        byte[] jpeg,
        byte marker,
        byte[] payload)
    {
        Assert.True(jpeg.Length >= 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8);
        int segmentLength = checked(payload.Length + 2);
        Assert.InRange(segmentLength, 2, ushort.MaxValue);
        byte[] result = new byte[checked(jpeg.Length + payload.Length + 4)];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(
            result.AsSpan(4, 2),
            checked((ushort)segmentLength));
        payload.CopyTo(result, 6);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(payload.Length + 6));
        return result;
    }

    private static byte[] CreatePngWithInvalidChunkLength()
    {
        byte[] bytes = new byte[33];
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), uint.MaxValue);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        return bytes;
    }

    private static CanonicalTransformRecipe Recipe(ImageFormat output) =>
        new(
            schemaVersion: 1,
            width: 24,
            height: 24,
            ImageResizeMode.Fit,
            ImageAnchor.Center,
            allowUpscale: false,
            output,
            quality: 82,
            ImageMetadataPolicy.StripSensitive);

    private static ImageDecodeLimits Limits(
        long maxEncodedBytes = 5 * 1024 * 1024,
        int maxWidth = 4096,
        int maxHeight = 4096,
        long maxAggregatePixels = 16_777_216,
        int maxFrames = 1,
        long maxEstimatedDecodedBytes = 128 * 1024 * 1024) =>
        new(
            maxEncodedBytes,
            maxWidth,
            maxHeight,
            maxAggregatePixels,
            maxFrames,
            maxEstimatedDecodedBytes,
            TimeSpan.FromSeconds(10));

    private sealed class ChunkedUnknownLengthSource(
        byte[] bytes,
        int maximumChunkBytes) : IReplayableImageSource
    {
        public long? Length => null;

        public bool OpensSeekableStreams => false;

        public long BytesRead { get; private set; }

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(
                new ChunkedReadStream(bytes, maximumChunkBytes, count =>
                    BytesRead = checked(BytesRead + count)));
        }
    }

    private sealed class ChunkedReadStream(
        byte[] bytes,
        int maximumChunkBytes,
        Action<int> recordRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(
                Math.Min(buffer.Length, maximumChunkBytes),
                bytes.Length - _position);
            if (count <= 0)
            {
                return 0;
            }

            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            recordRead(count);
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            BytesWritten = checked(BytesWritten + count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten = checked(BytesWritten + buffer.Length);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BytesWritten = checked(BytesWritten + buffer.Length);
            return ValueTask.CompletedTask;
        }
    }
}
