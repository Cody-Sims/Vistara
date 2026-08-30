using System.Security.Cryptography;
using Vistara.Application.Common.Imaging;
using VipsImage = global::NetVips.Image;

namespace Vistara.Imaging.NetVips;

internal sealed class LoadedImage : IDisposable
{
    private readonly ProbeReplayStream _stream;
    private readonly long? _encodedLength;

    public LoadedImage(
        VipsImage image,
        ImageFormat format,
        ProbeReplayStream stream,
        long? encodedLength)
    {
        Image = image;
        Format = format;
        _stream = stream;
        _encodedLength = encodedLength;
    }

    public VipsImage Image { get; }

    public ImageFormat Format { get; }

    public long EncodedBytes => _encodedLength ?? _stream.BytesRead;

    public void CompleteRead()
    {
        if (!_encodedLength.HasValue)
        {
            _stream.Drain();
        }
    }

    public void Dispose()
    {
        Image.Dispose();
        _stream.Dispose();
    }
}

internal sealed class ProbeReplayStream(Stream inner) : Stream
{
    private readonly MemoryStream _probeBytes = new();
    private readonly long _initialPosition = inner.CanSeek ? inner.Position : 0;
    private bool _probing = true;
    private long _replayPosition;

    public long BytesRead => inner is LimitedReadStream limited
        ? limited.BytesRead
        : 0;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public void RewindAfterProbe()
    {
        _probing = false;
        if (inner.CanSeek)
        {
            inner.Position = _initialPosition;
        }
        else
        {
            _replayPosition = 0;
        }
    }

    public ReadOnlyMemory<byte> GetProbePrefix(int maximumBytes)
    {
        int length = (int)Math.Min(maximumBytes, _probeBytes.Length);
        return _probeBytes.GetBuffer().AsMemory(0, length);
    }

    public void Drain()
    {
        Span<byte> buffer = stackalloc byte[8192];
        while (Read(buffer) != 0)
        {
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int replayed = Replay(buffer);
        if (replayed == buffer.Length)
        {
            return replayed;
        }

        int read = inner.Read(buffer[replayed..]);
        if (_probing && read > 0)
        {
            _probeBytes.Write(buffer.Slice(replayed, read));
        }

        return replayed + read;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _probeBytes.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Replay(Span<byte> buffer)
    {
        if (_probing || inner.CanSeek || _replayPosition >= _probeBytes.Length)
        {
            return 0;
        }

        int count = (int)Math.Min(buffer.Length, _probeBytes.Length - _replayPosition);
        _probeBytes.GetBuffer().AsSpan((int)_replayPosition, count).CopyTo(buffer);
        _replayPosition += count;
        return count;
    }
}

internal sealed class LimitedReadStream(
    Stream inner,
    long maxBytesRead,
    CancellationToken cancellationToken) : Stream
{
    private long _bytesRead;

    public long BytesRead => _bytesRead;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public void Drain()
    {
        Span<byte> buffer = stackalloc byte[8192];
        while (Read(buffer) != 0)
        {
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int read = inner.Read(buffer, offset, count);
        RecordRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int read = inner.Read(buffer);
        RecordRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            token);
        int read = await inner.ReadAsync(buffer, linked.Token);
        RecordRead(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.Seek(offset, origin);
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RecordRead(int count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bytesRead = checked(_bytesRead + count);
        if (_bytesRead > maxBytesRead)
        {
            throw new ImageProcessorException(
                ImageProcessorErrorCode.DecodeLimitExceeded,
                "The encoded image exceeds the configured byte limit.");
        }
    }
}

internal sealed class HashingWriteStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesWritten;

    public long BytesWritten => _bytesWritten;

    public string GetSha256() =>
        Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        _hash.AppendData(buffer, offset, count);
        _bytesWritten = checked(_bytesWritten + count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        _hash.AppendData(buffer);
        _bytesWritten = checked(_bytesWritten + buffer.Length);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        return WriteAndHashAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private async ValueTask WriteAndHashAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        _hash.AppendData(buffer.Span);
        _bytesWritten = checked(_bytesWritten + buffer.Length);
    }
}
