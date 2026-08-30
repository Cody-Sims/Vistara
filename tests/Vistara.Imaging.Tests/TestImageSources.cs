using Vistara.Application.Common.Imaging;

namespace Vistara.Imaging.Tests;

internal sealed class MemoryImageSource(byte[] bytes) : IReplayableImageSource
{
    public long? Length => bytes.LongLength;

    public bool OpensSeekableStreams => true;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}

internal sealed class DelayedImageSource(byte[] bytes, TimeSpan delay)
    : IReplayableImageSource
{
    public long? Length => bytes.LongLength;

    public bool OpensSeekableStreams => true;

    public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return new MemoryStream(bytes, writable: false);
    }
}

internal sealed class TrackingImageSource(long length) : IReplayableImageSource
{
    public bool WasOpened { get; private set; }

    public long? Length => length;

    public bool OpensSeekableStreams => true;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        WasOpened = true;
        return ValueTask.FromResult<Stream>(new MemoryStream());
    }
}

internal sealed class WriteOnlyNonSeekableStream : Stream
{
    private long _bytesWritten;

    public long BytesWritten => _bytesWritten;

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

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _bytesWritten = checked(_bytesWritten + count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _bytesWritten = checked(_bytesWritten + buffer.Length);
    }
}
