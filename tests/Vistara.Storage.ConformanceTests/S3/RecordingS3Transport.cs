using System.Text;
using Vistara.Application.Common.Storage;
using Vistara.Storage.S3;

namespace Vistara.Storage.ConformanceTests.S3;

internal sealed class RecordingS3Transport : IS3Transport
{
    private readonly Dictionary<string, StoredObject> _controlObjects =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, S3BeginMultipartCommand>
        _multipartCommands = new(StringComparer.Ordinal);
    private long _controlVersion;
    public List<string> HeadCommands { get; } = [];
    public List<S3GetCommand> GetCommands { get; } = [];
    public List<S3PutCommand> PutCommands { get; } = [];
    public List<S3CopyCommand> CopyCommands { get; } = [];
    public List<S3DeleteCommand> DeleteCommands { get; } = [];
    public List<S3CompleteMultipartCommand> CompleteMultipartCommands { get; } = [];
    public List<S3BeginMultipartCommand> BeginMultipartCommands { get; } = [];
    public List<(string Key, string UploadId)> AbortMultipartCommands { get; } = [];
    public List<S3PresignCommand> PresignCommands { get; } = [];

    public S3ReadResult? ReadResult { get; init; }
    public Func<S3GetCommand, S3ReadResult>? ReadResultFactory { get; init; }
    public Func<string, S3ObjectDescriptor?>? HeadResultFactory { get; set; }
    public S3TransportException? HeadException { get; init; }
    public S3TransportException? CompleteException { get; init; }
    public IReadOnlyList<S3UploadedPartDescriptor> UploadedParts { get; set; } =
        [];
    public IReadOnlyList<S3MultipartUploadDescriptor> ActiveMultipartUploads
    {
        get;
        set;
    } = [];

    public ValueTask<S3ObjectDescriptor?> HeadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HeadCommands.Add(key);
        if (HeadException is not null)
        {
            throw HeadException;
        }

        if (S3DurableMultipartState.IsControlKey(key))
        {
            return ValueTask.FromResult<S3ObjectDescriptor?>(
                _controlObjects.TryGetValue(key, out StoredObject? stored)
                    ? stored.Descriptor
                    : null);
        }

        if (HeadResultFactory is not null)
        {
            return ValueTask.FromResult(HeadResultFactory(key));
        }

        return ValueTask.FromResult<S3ObjectDescriptor?>(
            CreateDescriptor(key, 5 * 1024 * 1024));
    }

    public ValueTask<S3ReadResult> GetAsync(
        S3GetCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCommands.Add(command);
        if (S3DurableMultipartState.IsControlKey(command.Key))
        {
            if (!_controlObjects.TryGetValue(
                    command.Key,
                    out StoredObject? stored))
            {
                throw new S3TransportException(
                    S3TransportError.NotFound,
                    "Control object not found.");
            }

            return ValueTask.FromResult(
                new S3ReadResult(
                    new MemoryStream(stored.Content, writable: false),
                    stored.Descriptor,
                    null));
        }

        return ValueTask.FromResult(
            ReadResultFactory?.Invoke(command) ??
            ReadResult ??
            CreateReadResult("payload", command.Key));
    }

    public async ValueTask<S3ObjectDescriptor> PutAsync(
        S3PutCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PutCommands.Add(command);
        using MemoryStream copy = new();
        await command.Content.CopyToAsync(copy, cancellationToken);
        if (!S3DurableMultipartState.IsControlKey(command.Key))
        {
            return CreateDescriptor(command.Key, copy.Length);
        }

        _controlObjects.TryGetValue(
            command.Key,
            out StoredObject? existing);
        if ((command.Conditions.RequireMissing && existing is not null) ||
            (command.Conditions.IfMatch is not null &&
             !string.Equals(
                 existing?.Descriptor.EntityTag,
                 command.Conditions.IfMatch,
                 StringComparison.Ordinal)))
        {
            throw new S3TransportException(
                S3TransportError.PreconditionFailed,
                "Control object precondition failed.");
        }

        byte[] bytes = copy.ToArray();
        S3ObjectDescriptor descriptor = new(
            command.Key,
            bytes.LongLength,
            command.ContentType,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            $"\"control-{Interlocked.Increment(ref _controlVersion)}\"",
            [],
            command.Metadata);
        _controlObjects[command.Key] = new StoredObject(bytes, descriptor);
        return descriptor;
    }

    public ValueTask<S3CopyResult> CopyAsync(
        S3CopyCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CopyCommands.Add(command);
        return ValueTask.FromResult(
            new S3CopyResult(
                CreateDescriptor(command.DestinationKey, 7),
                CreateDescriptor(command.SourceKey, 7)));
    }

    public ValueTask<S3DeleteResult> DeleteAsync(
        S3DeleteCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCommands.Add(command);
        return ValueTask.FromResult(
            new S3DeleteResult(true, CreateDescriptor(command.Key, 7)));
    }

    public async IAsyncEnumerable<S3ObjectDescriptor> ListAsync(
        string? prefix,
        bool includeVersions,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return CreateDescriptor(prefix is null ? "listed" : $"{prefix}listed", 6);
    }

    public ValueTask<string> BeginMultipartAsync(
        S3BeginMultipartCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginMultipartCommands.Add(command);
        _multipartCommands[command.Key] = command;
        return ValueTask.FromResult("upload-id");
    }

    public ValueTask<IReadOnlyList<S3MultipartUploadDescriptor>>
        ListMultipartUploadsAsync(
            string key,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ActiveMultipartUploads);
    }

    public ValueTask<IReadOnlyList<S3UploadedPartDescriptor>> ListPartsAsync(
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(UploadedParts);
    }

    public ValueTask<S3ObjectDescriptor> CompleteMultipartAsync(
        S3CompleteMultipartCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompleteMultipartCommands.Add(command);
        if (CompleteException is not null)
        {
            throw CompleteException;
        }

        _multipartCommands.TryGetValue(
            command.Key,
            out S3BeginMultipartCommand? begin);
        IReadOnlyList<S3ChecksumValue> checksums = command.Checksum is null
            ? []
            :
            [
                new S3ChecksumValue(
                    command.Checksum.Algorithm,
                    command.Checksum.WireValue),
            ];
        return ValueTask.FromResult(
            CreateDescriptor(
                command.Key,
                command.Parts.Sum(part => part.SizeBytes),
                begin?.ContentType ?? "application/octet-stream",
                begin?.Metadata ?? new Dictionary<string, string>(),
                checksums));
    }

    public ValueTask AbortMultipartAsync(
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AbortMultipartCommands.Add((key, uploadId));
        return ValueTask.CompletedTask;
    }

    public ValueTask<Uri> PresignAsync(
        S3PresignCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PresignCommands.Add(command);
        return ValueTask.FromResult(
            new Uri($"https://signed.invalid/{command.Key}?signature=redacted"));
    }

    public static S3ReadResult CreateReadResult(string content, string key) =>
        new(
            new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
            CreateDescriptor(key, 10),
            new BlobContentRange(2, 4, 10));

    private static S3ObjectDescriptor CreateDescriptor(string key, long length) =>
        CreateDescriptor(
            key,
            length,
            "application/octet-stream",
            new Dictionary<string, string>(),
            [
                new S3ChecksumValue(
                    BlobChecksumAlgorithm.Sha256,
                    new string('a', 64)),
            ]);

    private static S3ObjectDescriptor CreateDescriptor(
        string key,
        long length,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<S3ChecksumValue> checksums) =>
        new(
            key,
            length,
            contentType,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            "\"etag\"",
            checksums,
            metadata);

    private sealed record StoredObject(
        byte[] Content,
        S3ObjectDescriptor Descriptor);
}
