using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Common.Storage;
using Vistara.Storage.Azure;
using Vistara.Storage.ConformanceTests.Fixtures;

namespace Vistara.Storage.ConformanceTests.Azure;

internal sealed class AzureBlobStoreFixture : IBlobStoreFixture
{
    private readonly InMemoryAzureBlobClient _client = new();

    private AzureBlobStoreFixture()
    {
        AzureBlobStoreOptions options =
            new(
                "account123",
                "media",
                new Uri("https://account123.blob.core.windows.net"))
            {
                TokenCredential = new TestTokenCredential(),
                TimeProvider = new FixedTimeProvider(
                    InMemoryBlobStoreFixture.ContractTimestamp),
                CopyPollInterval = TimeSpan.Zero,
                MaximumCopyPollAttempts = 3,
                TransferBlockBytes = 4,
            };
        Store = new AzureBlobStore(options, new FixedFactory(_client));
    }

    public IBlobStore Store { get; }

    public static AzureBlobStoreFixture Create() => new();

    public BlobKey Key(string suffix) => new($"contract/{suffix}");

    public IReplayableBlobContent Content(string value) =>
        new TrackingReplayableContent(value);

    public TrackingReplayableContent TrackingContent(string value) => new(value);

    public async ValueTask SeedAsync(BlobKey key, string value)
    {
        await Store.PutAsync(
            key,
            Content(value),
            new BlobWriteOptions(contentType: new BlobMediaType("text/plain")),
            CancellationToken.None);
    }

    public async ValueTask<string> ReadTextAsync(BlobKey key)
    {
        await using BlobReadHandle handle = await Store.OpenReadAsync(
            key,
            BlobReadOptions.Full,
            CancellationToken.None);
        using StreamReader reader = new(handle.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    public DirectUploadRequest DirectRequest(BlobKey key) =>
        new(
            key,
            8,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(10),
            BlobMetadata.Empty);

    public MultipartRequest MultipartRequest(BlobKey key) =>
        new(
            key,
            8,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(10),
            BlobMetadata.Empty);

    public MultipartSession Session(BlobKey key) =>
        new(
            "azure-session",
            key,
            InMemoryBlobStoreFixture.ContractTimestamp.AddMinutes(10),
            8,
            BlobRequestConditions.CreateOnly,
            50_000,
            1,
            4_000_000_000);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FixedFactory(IAzureBlobClient client) : IAzureBlobClientFactory
    {
        public IAzureBlobClient CreateWithTokenCredential(
            Uri serviceUri,
            string accountName,
            string containerName,
            global::Azure.Core.TokenCredential credential,
            bool emulatorMode) =>
            client;

        public IAzureBlobClient CreateWithConnectionString(
            string connectionString,
            Uri serviceUri,
            string accountName,
            string containerName,
            bool emulatorMode) =>
            client;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TestTokenCredential : global::Azure.Core.TokenCredential
    {
        public override global::Azure.Core.AccessToken GetToken(
            global::Azure.Core.TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.MaxValue);

        public override ValueTask<global::Azure.Core.AccessToken> GetTokenAsync(
            global::Azure.Core.TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new global::Azure.Core.AccessToken("test", DateTimeOffset.MaxValue));
    }
}

internal sealed class InMemoryAzureBlobClient : AzureBlobClientBase
{
    private readonly Dictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string BlockId), byte[]> _blocks = [];
    private long _version;

    public override ValueTask<AzureBlobObject?> HeadAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blobs.TryGetValue(key, out StoredBlob? blob);
        CheckConditions(blob, conditions);
        return ValueTask.FromResult(blob is null ? null : ToObject(key, blob));
    }

    public override ValueTask<AzureBlobDownload> DownloadAsync(
        string key,
        AzureBlobRange? range,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredBlob blob = Get(key);
        CheckConditions(blob, conditions);
        byte[] bytes = blob.Content;
        if (range is not null &&
            (range.Offset >= bytes.LongLength ||
             checked(range.Offset + range.Length) > bytes.LongLength))
        {
            throw Error(AzureBlobClientErrorCode.InvalidRange);
        }

        byte[] content = range is null
            ? bytes.ToArray()
            : bytes.AsSpan(checked((int)range.Offset), checked((int)range.Length)).ToArray();
        return ValueTask.FromResult(new AzureBlobDownload(
            new MemoryStream(content, writable: false),
            ToObject(key, blob),
            range,
            bytes.LongLength));
    }

    public override async ValueTask StageBlockAsync(
        string key,
        string blockId,
        Stream content,
        byte[] contentMd5,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();
#pragma warning disable CA5351 // The fake verifies Azure's MD5 transport checksum.
        if (!MD5.HashData(bytes).AsSpan().SequenceEqual(contentMd5))
#pragma warning restore CA5351
        {
            throw Error(AzureBlobClientErrorCode.IntegrityMismatch);
        }

        _blocks[(key, blockId)] = bytes;
    }

    public override ValueTask<AzureBlobBlockList> GetBlockListAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blobs.TryGetValue(key, out StoredBlob? blob);
        AzureBlobBlock[] uncommitted = _blocks
            .Where(pair => pair.Key.Key == key)
            .Select(pair => new AzureBlobBlock(
                pair.Key.BlockId,
                pair.Value.LongLength))
            .OrderBy(block => block.Name, StringComparer.Ordinal)
            .ToArray();
        if (blob is null && uncommitted.Length == 0)
        {
            throw Error(AzureBlobClientErrorCode.NotFound);
        }

        return ValueTask.FromResult(new AzureBlobBlockList(
            blob?.CommittedBlocks ?? [],
            uncommitted));
    }

    public override ValueTask<AzureBlobObject> CommitBlockListAsync(
        string key,
        IReadOnlyList<string> blockIds,
        AzureBlobCommitOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blobs.TryGetValue(key, out StoredBlob? existing);
        CheckConditions(existing, options.Conditions);
        using MemoryStream content = new();
        List<AzureBlobBlock> committedBlocks = [];
        foreach (string blockId in blockIds)
        {
            if (!_blocks.TryGetValue((key, blockId), out byte[]? block))
            {
                block = new byte[4];
            }

            content.Write(block);
            committedBlocks.Add(
                new AzureBlobBlock(blockId, block.LongLength));
        }

        byte[] bytes = content.ToArray();
        if (options.ContentMd5.Length > 0)
        {
#pragma warning disable CA5351 // The fake verifies Azure's MD5 transport checksum.
            if (!MD5.HashData(bytes).AsSpan().SequenceEqual(options.ContentMd5))
#pragma warning restore CA5351
            {
                throw Error(AzureBlobClientErrorCode.IntegrityMismatch);
            }
        }

        StoredBlob stored = new(
            bytes,
            options.ContentType,
            options.ContentMd5.ToArray(),
            new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal),
            NextVersion(),
            committedBlocks);
        _blobs[key] = stored;
        foreach ((string Key, string BlockId) staged in _blocks.Keys
                     .Where(candidate => candidate.Key == key)
                     .ToArray())
        {
            _blocks.Remove(staged);
        }

        return ValueTask.FromResult(ToObject(key, stored));
    }

    public override ValueTask<AzureBlobCopyState> StartCopyAsync(
        string sourceKey,
        string destinationKey,
        AzureBlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredBlob source = Get(sourceKey);
        CheckConditions(source, options.SourceConditions);
        _blobs.TryGetValue(destinationKey, out StoredBlob? destination);
        CheckConditions(destination, options.DestinationConditions);
        StoredBlob copied = source with
        {
            Metadata = options.ReplacementMetadata is null
                ? new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>(
                    options.ReplacementMetadata,
                    StringComparer.Ordinal),
            Version = NextVersion(),
        };
        _blobs[destinationKey] = copied;
        return ValueTask.FromResult(new AzureBlobCopyState(
            AzureBlobCopyStatus.Success,
            ToObject(destinationKey, copied)));
    }

    public override ValueTask<AzureBlobCopyState> GetCopyStateAsync(
        string destinationKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredBlob blob = Get(destinationKey);
        return ValueTask.FromResult(new AzureBlobCopyState(
            AzureBlobCopyStatus.Success,
            ToObject(destinationKey, blob)));
    }

    public override ValueTask<AzureBlobDeleteResult> DeleteAsync(
        string key,
        AzureBlobConditions conditions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blobs.TryGetValue(key, out StoredBlob? blob);
        CheckConditions(blob, conditions);
        if (blob is null)
        {
            return ValueTask.FromResult(new AzureBlobDeleteResult(false, null));
        }

        _blobs.Remove(key);
        return ValueTask.FromResult(new AzureBlobDeleteResult(
            true,
            ToObject(key, blob)));
    }

    public override async IAsyncEnumerable<AzureBlobObject> ListAsync(
        string? prefix,
        bool includeVersions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach ((string key, StoredBlob blob) in _blobs
                     .Where(pair =>
                         prefix is null ||
                         pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ToObject(key, blob);
        }
    }

    public override ValueTask<Uri> CreateSasUriAsync(
        AzureBlobSasRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string suffix = request.BlockId is null
            ? string.Empty
            : $"&comp=block&blockid={Uri.EscapeDataString(request.BlockId)}";
        return ValueTask.FromResult(new Uri(
            $"https://account123.blob.core.windows.net/media/{request.Key}?sig=fake{suffix}"));
    }

    public override Uri GetBlobUri(string key) =>
        new($"https://account123.blob.core.windows.net/media/{key}");

    private StoredBlob Get(string key) =>
        _blobs.TryGetValue(key, out StoredBlob? blob)
            ? blob
            : throw Error(AzureBlobClientErrorCode.NotFound);

    private static void CheckConditions(
        StoredBlob? blob,
        AzureBlobConditions conditions)
    {
        if ((conditions.RequireMissing && blob is not null) ||
            (conditions.IfMatch is not null &&
             !string.Equals(
                 blob?.Version,
                 conditions.IfMatch,
                 StringComparison.Ordinal)))
        {
            throw Error(AzureBlobClientErrorCode.PreconditionFailed);
        }
    }

    private static AzureBlobObject ToObject(string key, StoredBlob blob) =>
        new(
            key,
            blob.Content.LongLength,
            blob.ContentType,
            InMemoryBlobStoreFixture.ContractTimestamp,
            blob.Version,
            blob.Version,
            blob.ContentMd5,
            blob.Metadata);

    private string NextVersion() =>
        string.Concat("\"azure-", Interlocked.Increment(ref _version), "\"");

    private static AzureBlobClientException Error(AzureBlobClientErrorCode code) =>
        new(code, "Fake Azure Blob request failed.");

    private sealed record StoredBlob(
        byte[] Content,
        string ContentType,
        byte[] ContentMd5,
        IReadOnlyDictionary<string, string> Metadata,
        string Version,
        IReadOnlyList<AzureBlobBlock> CommittedBlocks);
}
