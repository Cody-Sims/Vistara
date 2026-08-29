using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;

namespace Vistara.Worker.Features.Derivatives;

public interface IDerivativeOutputScratch : IReplayableBlobContent, IAsyncDisposable
{
    Stream Destination { get; }

    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

public interface IDerivativeOutputScratchFactory
{
    ValueTask<IDerivativeOutputScratch> CreateAsync(
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class FileDerivativeOutputScratchFactory
    : IDerivativeOutputScratchFactory
{
    private readonly string _directory;

    public FileDerivativeOutputScratchFactory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public ValueTask<IDerivativeOutputScratch> CreateAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(
            _directory,
            $"{Guid.CreateVersion7():N}.scratch");
        var file = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult<IDerivativeOutputScratch>(
            new FileDerivativeOutputScratch(path, file, maximumBytes));
    }
}

public sealed class DerivativeTransformGate : IDisposable
{
    private readonly SemaphoreSlim _gate;

    public DerivativeTransformGate(int maximumConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        _gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    public async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed class FileDerivativeOutputScratch : IDerivativeOutputScratch
{
    private readonly string _path;
    private FileStream? _file;
    private BoundedWriteStream? _destination;
    private long? _length;

    internal FileDerivativeOutputScratch(
        string path,
        FileStream file,
        long maximumBytes)
    {
        _path = path;
        _file = file;
        _destination = new BoundedWriteStream(file, maximumBytes);
    }

    public Stream Destination =>
        _destination ??
        throw new InvalidOperationException("The derivative scratch output is complete.");

    public long Length =>
        _length ??
        throw new InvalidOperationException("The derivative scratch output is incomplete.");

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_length.HasValue)
        {
            return;
        }

        BoundedWriteStream destination = _destination ??
            throw new ObjectDisposedException(nameof(FileDerivativeOutputScratch));
        await destination.FlushAsync(cancellationToken);
        _length = destination.BytesWritten;
        await destination.DisposeAsync();
        _destination = null;
        _file = null;
    }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        _ = Length;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(
            new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public async ValueTask DisposeAsync()
    {
        if (_destination is not null)
        {
            await _destination.DisposeAsync();
            _destination = null;
            _file = null;
        }
        else if (_file is not null)
        {
            await _file.DisposeAsync();
            _file = null;
        }

        try
        {
            File.Delete(_path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

internal sealed class BoundedWriteStream(Stream inner, long maximumBytes) : Stream
{
    private long _bytesWritten;

    internal long BytesWritten => _bytesWritten;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => _bytesWritten;

    public override long Position
    {
        get => _bytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        inner.Write(buffer);
        _bytesWritten += buffer.Length;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        WriteBoundedAsync(buffer, cancellationToken);

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

    private async ValueTask WriteBoundedAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        EnsureCapacity(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken);
        _bytesWritten += buffer.Length;
    }

    private void EnsureCapacity(int count)
    {
        if (count < 0 || _bytesWritten > maximumBytes - count)
        {
            throw new ImageProcessorException(
                ImageProcessorErrorCode.DecodeLimitExceeded,
                "The encoded derivative exceeds the configured byte limit.");
        }
    }
}
