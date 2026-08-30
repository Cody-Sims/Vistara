using Azure.Core;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.Azure;

public interface IAzureBlobClientFactory
{
    IAzureBlobClient CreateWithTokenCredential(
        Uri serviceUri,
        string accountName,
        string containerName,
        TokenCredential credential,
        bool emulatorMode);

    IAzureBlobClient CreateWithConnectionString(
        string connectionString,
        Uri serviceUri,
        string accountName,
        string containerName,
        bool emulatorMode);
}

public interface IAzureBlobClient
{
    ValueTask<AzureBlobObject?> HeadAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken);

    ValueTask<AzureBlobDownload> DownloadAsync(
        string key,
        AzureBlobRange? range,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken);

    ValueTask StageBlockAsync(
        string key,
        string blockId,
        Stream content,
        byte[] contentMd5,
        CancellationToken cancellationToken);

    ValueTask<AzureBlobBlockList> GetBlockListAsync(
        string key,
        CancellationToken cancellationToken);

    ValueTask<AzureBlobObject> CommitBlockListAsync(
        string key,
        IReadOnlyList<string> blockIds,
        AzureBlobCommitOptions options,
        CancellationToken cancellationToken);

    ValueTask<AzureBlobCopyState> StartCopyAsync(
        string sourceKey,
        string destinationKey,
        AzureBlobCopyOptions options,
        CancellationToken cancellationToken);

    ValueTask<AzureBlobCopyState> GetCopyStateAsync(
        string destinationKey,
        CancellationToken cancellationToken);

    ValueTask<AzureBlobDeleteResult> DeleteAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AzureBlobObject> ListAsync(
        string? prefix,
        bool includeVersions,
        CancellationToken cancellationToken);

    ValueTask<Uri> CreateSasUriAsync(
        AzureBlobSasRequest request,
        CancellationToken cancellationToken);

    Uri GetBlobUri(string key);
}

public abstract class AzureBlobClientBase : IAzureBlobClient
{
    public virtual ValueTask<AzureBlobObject?> HeadAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask<AzureBlobDownload> DownloadAsync(
        string key,
        AzureBlobRange? range,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask StageBlockAsync(
        string key,
        string blockId,
        Stream content,
        byte[] contentMd5,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask<AzureBlobBlockList> GetBlockListAsync(
        string key,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask<AzureBlobObject> CommitBlockListAsync(
        string key,
        IReadOnlyList<string> blockIds,
        AzureBlobCommitOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask<AzureBlobCopyState> StartCopyAsync(
        string sourceKey,
        string destinationKey,
        AzureBlobCopyOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask<AzureBlobCopyState> GetCopyStateAsync(
        string destinationKey,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask<AzureBlobDeleteResult> DeleteAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual IAsyncEnumerable<AzureBlobObject> ListAsync(
        string? prefix,
        bool includeVersions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual ValueTask<Uri> CreateSasUriAsync(
        AzureBlobSasRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Uri GetBlobUri(string key) =>
        throw new NotSupportedException();
}

public sealed record AzureBlobConditions(
    string? IfMatch = null,
    bool RequireMissing = false);

public sealed record AzureBlobRange(long Offset, long Length);

public sealed record AzureBlobBlock(string Name, long SizeBytes);

public sealed record AzureBlobBlockList(
    IReadOnlyList<AzureBlobBlock> Committed,
    IReadOnlyList<AzureBlobBlock> Uncommitted);

public sealed record AzureBlobObject(
    string Key,
    long ContentLength,
    string ContentType,
    DateTimeOffset LastModifiedUtc,
    string Version,
    string EntityTag,
    byte[]? ContentMd5,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record AzureBlobDownload(
    Stream Content,
    AzureBlobObject Blob,
    AzureBlobRange? Range,
    long TotalLength);

public sealed record AzureBlobCommitOptions(
    string ContentType,
    byte[] ContentMd5,
    IReadOnlyDictionary<string, string> Metadata,
    AzureBlobConditions Conditions);

public sealed record AzureBlobCopyOptions(
    AzureBlobConditions SourceConditions,
    AzureBlobConditions DestinationConditions,
    IReadOnlyDictionary<string, string>? ReplacementMetadata);

public enum AzureBlobCopyStatus
{
    Pending,
    Success,
    Failed,
    Aborted,
}

public sealed record AzureBlobCopyState(
    AzureBlobCopyStatus Status,
    AzureBlobObject? Blob,
    string? FailureDescription = null);

public sealed record AzureBlobDeleteResult(
    bool Deleted,
    AzureBlobObject? DeletedBlob);

public enum AzureBlobSasAccess
{
    Read,
    Create,
    Write,
    WriteBlock,
}

public sealed record AzureBlobSasRequest(
    string Key,
    AzureBlobSasAccess Access,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool HttpsOnly,
    string? ContentDisposition = null,
    string? BlockId = null);

public enum AzureBlobClientErrorCode
{
    NotFound,
    PreconditionFailed,
    InvalidRange,
    InvalidRequest,
    IntegrityMismatch,
    OutcomeUnknown,
}

public sealed class AzureBlobClientException : Exception
{
    public AzureBlobClientException(
        AzureBlobClientErrorCode code,
        string message)
        : base(message)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Code = code;
    }

    public AzureBlobClientErrorCode Code { get; }
}
