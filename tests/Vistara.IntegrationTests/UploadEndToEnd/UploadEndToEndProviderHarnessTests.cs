using System.Security.Cryptography;
using Amazon.Runtime;
using Vistara.Application.Common.Storage;
using Vistara.Storage.Azure;
using Vistara.Storage.Local;
using Vistara.Storage.S3;
using Xunit;
using Xunit.Abstractions;

namespace Vistara.IntegrationTests.UploadEndToEnd;

public sealed class UploadEndToEndProviderHarnessTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UploadEndToEnd_local_provider_streams_promotes_and_cleans_up()
    {
        await using TestDirectory directory = TestDirectory.Create();
        var store = new LocalBlobStore(new LocalBlobStoreOptions(directory.Path));
        byte[] bytes = PngBytes();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Guid tenantId = Guid.CreateVersion7();
        Guid uploadId = Guid.CreateVersion7();
        BlobKey staging = Key("staging", tenantId, uploadId);
        BlobKey canonical = Key("originals", tenantId, uploadId);
        var metadata = new BlobMetadata(
        [
            KeyValuePair.Create("vistara-tenant-id", tenantId.ToString("D")),
            KeyValuePair.Create("vistara-upload-id", uploadId.ToString("D")),
        ]);

        Assert.False(store.Capabilities.SupportsDirectUpload);
        Assert.False(store.Capabilities.SupportsMultipartUpload);
        BlobWriteResult written = await store.PutAsync(
            staging,
            new BytesContent(bytes),
            new BlobWriteOptions(
                new BlobMediaType("image/png"),
                metadata,
                [new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256)],
                BlobRequestConditions.CreateOnly),
            CancellationToken.None);

        BlobCopyResult promoted = await store.CopyAsync(
            staging,
            canonical,
            new BlobCopyOptions(
                new BlobRequestConditions(ifMatch: written.Head.Identity.Version),
                BlobRequestConditions.CreateOnly,
                metadata),
            CancellationToken.None);
        await using BlobReadHandle read = await store.OpenReadAsync(
            canonical,
            new BlobReadOptions(
                Conditions: new BlobRequestConditions(
                    ifMatch: promoted.Head.Identity.Version)),
            CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer);
        BlobDeleteResult deleted = await store.DeleteAsync(
            staging,
            new BlobDeleteOptions(
                new BlobRequestConditions(ifMatch: written.Head.Identity.Version)),
            CancellationToken.None);

        Assert.Equal(bytes, buffer.ToArray());
        Assert.Equal(sha256, Assert.Single(promoted.Head.Properties.Checksums).Value);
        Assert.True(deleted.Deleted);
        Assert.Null(await store.HeadAsync(staging, CancellationToken.None));
        Assert.NotNull(await store.HeadAsync(canonical, CancellationToken.None));
    }

    [Fact]
    public async Task UploadEndToEnd_minio_offline_harness_proves_direct_contract_and_multipart_limits()
    {
        var options = new S3BlobStoreOptions(
            S3ProviderKind.Minio,
            "vistara-tests",
            "us-east-1")
        {
            ServiceUrl = new Uri("http://127.0.0.1:9000/"),
            ForcePathStyle = true,
            AllowInsecureHttp = true,
            AllowedEndpointHosts = ["127.0.0.1"],
            MaximumPresignLifetime = TimeSpan.FromMinutes(10),
        };
        await using var store = new S3BlobStore(
            options,
            new BasicAWSCredentials("offline-test", "offline-test"),
            new FixedTimeProvider(Now));
        var key = new BlobKey("staging/01/offline-minio/upload");
        string sha256 = new string('a', 64);

        DirectUploadPlan direct = await store.CreateDirectUploadAsync(
            new DirectUploadRequest(
                key,
                128,
                new BlobMediaType("image/png"),
                new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256),
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        BlobStoreException multipartIntegrity = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.BeginMultipartAsync(
                new MultipartRequest(
                    key,
                    5L * 1024 * 1024,
                    new BlobMediaType("image/png"),
                    new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256),
                    BlobRequestConditions.CreateOnly,
                    TimeSpan.FromMinutes(5),
                    BlobMetadata.Empty),
                CancellationToken.None));

        Assert.Equal("minio", store.Name);
        Assert.True(store.Capabilities.SupportsDirectUpload);
        Assert.True(store.Capabilities.SupportsMultipartUpload);
        Assert.False(store.Capabilities.SupportsConditionalMultipartCompletion);
        Assert.Equal(HttpMethodKind.Put, direct.Request.Method);
        Assert.Equal("127.0.0.1", direct.Request.Url.Host);
        Assert.Contains("/vistara-tests/", direct.Request.Url.AbsolutePath);
        Assert.Equal("128", direct.Request.Headers["Content-Length"]);
        Assert.Equal(BlobStoreErrorCode.Unsupported, multipartIntegrity.Code);
        output.WriteLine(
            "MinIO emulator was not contacted; offline signing, capabilities, and " +
            "multipart checksum rejection executed against S3BlobStore.");
    }

    [Fact]
    public async Task UploadEndToEnd_azurite_offline_harness_executes_direct_and_multipart_flows()
    {
        var client = new OfflineAzureBlobClient();
        AzureBlobStore store = CreateAzureStore(client);
        var directKey = new BlobKey("staging/01/offline-azurite/direct");
        var multipartKey = new BlobKey("staging/01/offline-azurite/multipart");
        byte[] bytes = PngBytes();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        DirectUploadPlan direct = await store.CreateDirectUploadAsync(
            Request(directKey, bytes.LongLength, sha256),
            CancellationToken.None);
        client.ExecuteSignedPut(direct, bytes);
        BlobHead directHead = Assert.IsType<BlobHead>(
            await store.HeadAsync(directKey, CancellationToken.None));

        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                multipartKey,
                bytes.LongLength,
                new BlobMediaType("image/png"),
                new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256),
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        MultipartPartPlan part = await store.CreatePartPlanAsync(
            session,
            1,
            CancellationToken.None);
        await client.ExecuteSignedBlockAsync(part, bytes);
        MultipartCompletion completion = await store.CompleteMultipartAsync(
            session,
            [
                new UploadedPart(
                    1,
                    new BlobEntityTag("\"offline-part-1\""),
                    null,
                    bytes.LongLength),
            ],
            CancellationToken.None);

        Assert.Equal("azure", store.Name);
        Assert.True(store.Capabilities.SupportsDirectUpload);
        Assert.True(store.Capabilities.SupportsMultipartUpload);
        Assert.Equal(bytes.LongLength, directHead.Properties.ContentLength);
        Assert.Equal(bytes.LongLength, completion.Head.Properties.ContentLength);
        Assert.Equal("image/png", completion.Head.Properties.ContentType.Value);
        output.WriteLine(
            "Azurite was not contacted; the AzureBlobStore direct SAS and multipart " +
            "flow executed against an in-process Azure client boundary.");
    }

    [Fact]
    public async Task UploadEndToEnd_provider_harnesses_reject_integrity_limits_and_cancellation()
    {
        await using TestDirectory directory = TestDirectory.Create();
        var local = new LocalBlobStore(new LocalBlobStoreOptions(directory.Path));
        byte[] bytes = PngBytes();
        var key = new BlobKey("staging/01/rejections/upload");

        BlobStoreException integrity = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await local.PutAsync(
                key,
                new BytesContent(bytes),
                new BlobWriteOptions(
                    new BlobMediaType("image/png"),
                    checksums:
                    [
                        new BlobChecksum(
                            BlobChecksumAlgorithm.Sha256,
                            new string('0', 64)),
                    ]),
                CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await local.PutAsync(
                key,
                new BytesContent(bytes),
                new BlobWriteOptions(new BlobMediaType("image/png")),
                cancellation.Token));

        Assert.Equal(BlobStoreErrorCode.IntegrityMismatch, integrity.Code);
        Assert.Null(await local.HeadAsync(key, CancellationToken.None));
        BlobStoreException limit = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await new LocalBlobStore(new LocalBlobStoreOptions(directory.Path))
                .PutAsync(
                    key,
                    new DeclaredLengthContent(bytes, long.MaxValue),
                    BlobWriteOptions.None,
                    CancellationToken.None));
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, limit.Code);
    }

    private static AzureBlobStore CreateAzureStore(OfflineAzureBlobClient client)
    {
        var options = new AzureBlobStoreOptions(
            "devstoreaccount1",
            "vistara-tests",
            new Uri("http://127.0.0.1:10000/devstoreaccount1"),
            emulatorMode: true)
        {
            CredentialMode = AzureBlobCredentialMode.ConnectionString,
            ConnectionString =
                "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
                "AccountKey=offline;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;",
            SasMode = AzureBlobSasMode.SharedKey,
            AllowSharedKeySas = true,
            TimeProvider = new FixedTimeProvider(Now),
            CopyPollInterval = TimeSpan.Zero,
        };
        return new AzureBlobStore(options, new OfflineAzureBlobClientFactory(client));
    }

    private static DirectUploadRequest Request(
        BlobKey key,
        long length,
        string sha256) =>
        new(
            key,
            length,
            new BlobMediaType("image/png"),
            new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256),
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(5),
            BlobMetadata.Empty);

    private static BlobKey Key(string prefix, Guid tenantId, Guid uploadId) =>
        new($"{prefix}/{tenantId:N}/{uploadId:N}/image.png");

    private static byte[] PngBytes() =>
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk" +
            "YAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BytesContent(byte[] bytes) : IReplayableBlobContent
    {
        public long Length => bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class DeclaredLengthContent(byte[] bytes, long length)
        : IReplayableBlobContent
    {
        public long Length { get; } = length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class TestDirectory : IAsyncDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TestDirectory Create()
        {
            string repository = FindRepositoryRoot();
            return new TestDirectory(System.IO.Path.Combine(
                repository,
                "eng",
                "tests",
                ".upload-end-to-end",
                Guid.NewGuid().ToString("N")));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
            while (directory is not null &&
                   !File.Exists(System.IO.Path.Combine(directory.FullName, "AGENTS.md")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ??
                throw new InvalidOperationException("Repository root was not found.");
        }
    }

    private sealed class OfflineAzureBlobClientFactory(IAzureBlobClient client)
        : IAzureBlobClientFactory
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

    private sealed class OfflineAzureBlobClient : AzureBlobClientBase
    {
        private readonly Dictionary<string, StoredBlob> _blobs =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(string Key, string BlockId), byte[]> _blocks = [];
        private long _version;

        public void ExecuteSignedPut(DirectUploadPlan plan, byte[] bytes)
        {
            string key = Uri.UnescapeDataString(
                plan.Request.Url.AbsolutePath.Split('/', 4)[^1]);
            string contentType = plan.Request.Headers["Content-Type"];
            var metadata = plan.Request.Headers
                .Where(pair => pair.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    pair => pair.Key["x-ms-meta-".Length..],
                    pair => pair.Value,
                    StringComparer.Ordinal);
            _blobs.Add(key, new StoredBlob(bytes.ToArray(), contentType, metadata, NextVersion()));
        }

        public async ValueTask ExecuteSignedBlockAsync(
            MultipartPartPlan plan,
            byte[] bytes)
        {
            string key = Uri.UnescapeDataString(
                plan.Request.Url.AbsolutePath.Split('/', 4)[^1]);
            string blockId = GetQueryValue(plan.Request.Url, "blockid");
#pragma warning disable CA5351
            await StageBlockAsync(
                key,
                blockId,
                new MemoryStream(bytes, writable: false),
                MD5.HashData(bytes),
                CancellationToken.None);
#pragma warning restore CA5351
        }

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
            if (!_blobs.TryGetValue(key, out StoredBlob? blob))
            {
                throw new AzureBlobClientException(
                    AzureBlobClientErrorCode.NotFound,
                    "The offline blob was not found.");
            }

            CheckConditions(blob, conditions);
            if (range is not null)
            {
                throw new AzureBlobClientException(
                    AzureBlobClientErrorCode.InvalidRange,
                    "The offline harness does not use ranged control reads.");
            }

            return ValueTask.FromResult(new AzureBlobDownload(
                new MemoryStream(blob.Bytes, writable: false),
                ToObject(key, blob),
                null,
                blob.Bytes.LongLength));
        }

        public override async ValueTask StageBlockAsync(
            string key,
            string blockId,
            Stream content,
            byte[] contentMd5,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            byte[] bytes = buffer.ToArray();
#pragma warning disable CA5351
            if (!MD5.HashData(bytes).AsSpan().SequenceEqual(contentMd5))
#pragma warning restore CA5351
            {
                throw new AzureBlobClientException(
                    AzureBlobClientErrorCode.IntegrityMismatch,
                    "The offline block checksum did not match.");
            }

            _blocks[(key, blockId)] = bytes;
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
            using var content = new MemoryStream();
            foreach (string blockId in blockIds)
            {
                content.Write(_blocks[(key, blockId)]);
            }

            var stored = new StoredBlob(
                content.ToArray(),
                options.ContentType,
                new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal),
                NextVersion());
            _blobs[key] = stored;
            return ValueTask.FromResult(ToObject(key, stored));
        }

        public override ValueTask<Uri> CreateSasUriAsync(
            AzureBlobSasRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string block = request.BlockId is null
                ? string.Empty
                : $"&comp=block&blockid={Uri.EscapeDataString(request.BlockId)}";
            return ValueTask.FromResult(new Uri(
                $"http://127.0.0.1:10000/devstoreaccount1/vistara-tests/" +
                $"{request.Key}?sig=offline{block}"));
        }

        public override Uri GetBlobUri(string key) =>
            new($"http://127.0.0.1:10000/devstoreaccount1/vistara-tests/{key}");

        private static void CheckConditions(
            StoredBlob? blob,
            AzureBlobConditions conditions)
        {
            if ((conditions.RequireMissing && blob is not null) ||
                (conditions.IfMatch is not null &&
                 !string.Equals(blob?.Version, conditions.IfMatch, StringComparison.Ordinal)))
            {
                throw new AzureBlobClientException(
                    AzureBlobClientErrorCode.PreconditionFailed,
                    "The offline precondition failed.");
            }
        }

        private static AzureBlobObject ToObject(string key, StoredBlob blob) =>
            new(
                key,
                blob.Bytes.LongLength,
                blob.ContentType,
                Now,
                blob.Version,
                blob.Version,
                null,
                blob.Metadata);

        private string NextVersion() =>
            $"\"offline-{Interlocked.Increment(ref _version)}\"";

        private static string GetQueryValue(Uri uri, string name)
        {
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                string[] parts = pair.Split('=', 2);
                if (parts.Length == 2 &&
                    string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            throw new InvalidOperationException($"Query value '{name}' was missing.");
        }

        private sealed record StoredBlob(
            byte[] Bytes,
            string ContentType,
            IReadOnlyDictionary<string, string> Metadata,
            string Version);
    }
}
