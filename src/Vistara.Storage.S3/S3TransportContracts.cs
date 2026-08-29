using Vistara.Application.Common.Storage;

namespace Vistara.Storage.S3;

internal interface IS3Transport : IAsyncDisposable
{
    ValueTask<S3ObjectDescriptor?> HeadAsync(
        string key,
        CancellationToken cancellationToken);

    ValueTask<S3ReadResult> GetAsync(
        S3GetCommand command,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectDescriptor> PutAsync(
        S3PutCommand command,
        CancellationToken cancellationToken);

    ValueTask<S3CopyResult> CopyAsync(
        S3CopyCommand command,
        CancellationToken cancellationToken);

    ValueTask<S3DeleteResult> DeleteAsync(
        S3DeleteCommand command,
        CancellationToken cancellationToken);

    IAsyncEnumerable<S3ObjectDescriptor> ListAsync(
        string? prefix,
        bool includeVersions,
        CancellationToken cancellationToken);

    ValueTask<string> BeginMultipartAsync(
        S3BeginMultipartCommand command,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectDescriptor> CompleteMultipartAsync(
        S3CompleteMultipartCommand command,
        CancellationToken cancellationToken);

    ValueTask AbortMultipartAsync(
        string key,
        string uploadId,
        CancellationToken cancellationToken);

    ValueTask<Uri> PresignAsync(
        S3PresignCommand command,
        CancellationToken cancellationToken);

    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed record S3Conditions(string? IfMatch, bool RequireMissing)
{
    public static S3Conditions None { get; } = new(null, false);
}

internal sealed record S3ChecksumValue(
    BlobChecksumAlgorithm Algorithm,
    string Value);

internal sealed record S3WireChecksum(
    BlobChecksumAlgorithm Algorithm,
    string WireValue);

internal sealed record S3ObjectDescriptor(
    string Key,
    long ContentLength,
    string ContentType,
    DateTimeOffset LastModifiedUtc,
    string EntityTag,
    IReadOnlyList<S3ChecksumValue> Checksums,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed record S3GetCommand(
    string Key,
    string? Range,
    S3Conditions Conditions);

internal sealed class S3ReadResult
{
    private readonly Action? _dispose;
    private int _disposed;

    public S3ReadResult(
        Stream content,
        S3ObjectDescriptor descriptor,
        BlobContentRange? contentRange,
        Action? dispose = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Descriptor = descriptor;
        ContentRange = contentRange;
        _dispose = dispose;
        Content = new OwnedStream(this, content);
    }

    public Stream Content { get; }

    public S3ObjectDescriptor Descriptor { get; }

    public BlobContentRange? ContentRange { get; }

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    private void MarkDisposed()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose?.Invoke();
        }
    }

    private sealed class OwnedStream(S3ReadResult owner, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.MarkDisposed();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            owner.MarkDisposed();
            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed record S3PutCommand(
    string Key,
    Stream Content,
    long ContentLength,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<S3WireChecksum> Checksums,
    S3Conditions Conditions);

internal sealed record S3CopyCommand(
    string SourceKey,
    string DestinationKey,
    string? SourceIfMatch,
    IReadOnlyDictionary<string, string>? ReplacementMetadata);

internal sealed record S3CopyResult(
    S3ObjectDescriptor Destination,
    S3ObjectDescriptor Source);

internal sealed record S3DeleteCommand(string Key, string? IfMatch);

internal sealed record S3DeleteResult(
    bool Deleted,
    S3ObjectDescriptor? DeletedObject);

internal sealed record S3BeginMultipartCommand(
    string Key,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    BlobChecksumAlgorithm? ChecksumAlgorithm);

internal sealed record S3CompletedPart(
    int PartNumber,
    string EntityTag,
    S3WireChecksum? Checksum,
    long SizeBytes);

internal sealed record S3CompleteMultipartCommand(
    string Key,
    string UploadId,
    IReadOnlyList<S3CompletedPart> Parts,
    S3Conditions Conditions,
    S3WireChecksum? Checksum);

internal sealed record S3PresignCommand(
    HttpMethodKind Method,
    string Key,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Parameters,
    string? UploadId = null,
    int? PartNumber = null);

internal enum S3TransportError
{
    Unsupported,
    NotFound,
    PreconditionFailed,
    InvalidRange,
    IntegrityMismatch,
    InvalidRequest,
    OutcomeUnknown,
}

internal sealed class S3TransportException(
    S3TransportError error,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public S3TransportError Error { get; } = error;
}
