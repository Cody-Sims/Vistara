using System.Text;
using Vistara.Application.Common.Storage;
using Vistara.Storage.S3;

namespace Vistara.Storage.ConformanceTests.S3;

internal sealed class RecordingS3Transport : IS3Transport
{
    public List<string> HeadCommands { get; } = [];
    public List<S3GetCommand> GetCommands { get; } = [];
    public List<S3PutCommand> PutCommands { get; } = [];
    public List<S3CopyCommand> CopyCommands { get; } = [];
    public List<S3DeleteCommand> DeleteCommands { get; } = [];
    public List<S3CompleteMultipartCommand> CompleteMultipartCommands { get; } = [];
    public List<(string Key, string UploadId)> AbortMultipartCommands { get; } = [];
    public List<S3PresignCommand> PresignCommands { get; } = [];

    public S3ReadResult? ReadResult { get; init; }
    public Func<S3GetCommand, S3ReadResult>? ReadResultFactory { get; init; }
    public S3TransportException? HeadException { get; init; }
    public S3TransportException? CompleteException { get; init; }

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

        return ValueTask.FromResult<S3ObjectDescriptor?>(
            CreateDescriptor(key, 5 * 1024 * 1024));
    }

    public ValueTask<S3ReadResult> GetAsync(
        S3GetCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCommands.Add(command);
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
        return CreateDescriptor(command.Key, copy.Length);
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
        return ValueTask.FromResult("upload-id");
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

        return ValueTask.FromResult(
            CreateDescriptor(command.Key, command.Parts.Sum(part => part.SizeBytes)));
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
        new(
            key,
            length,
            "application/octet-stream",
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            "\"etag\"",
            [new S3ChecksumValue(BlobChecksumAlgorithm.Sha256, new string('a', 64))],
            new Dictionary<string, string>());
}
