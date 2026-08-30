namespace Vistara.Storage.Local;

internal sealed class BoundedReadStream(
    FileStream inner,
    long remaining) : Stream
{
    private long _remaining = remaining;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(inner.SafeFileHandle.IsClosed, this);
        int boundedCount = (int)Math.Min(count, _remaining);
        if (boundedCount == 0)
        {
            return 0;
        }

        int read = inner.Read(buffer, offset, boundedCount);
        _remaining -= read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(inner.SafeFileHandle.IsClosed, this);
        int boundedCount = (int)Math.Min(buffer.Length, _remaining);
        if (boundedCount == 0)
        {
            return 0;
        }

        int read = inner.Read(buffer[..boundedCount]);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(inner.SafeFileHandle.IsClosed, this);
        int boundedCount = (int)Math.Min(buffer.Length, _remaining);
        if (boundedCount == 0)
        {
            return 0;
        }

        int read = await inner.ReadAsync(buffer[..boundedCount], cancellationToken);
        _remaining -= read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

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

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
