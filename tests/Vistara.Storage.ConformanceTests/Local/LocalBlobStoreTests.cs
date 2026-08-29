using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Common.Storage;
using Vistara.Storage.ConformanceTests.Fixtures;
using Vistara.Storage.Local;

namespace Vistara.Storage.ConformanceTests.Local;

public sealed class LocalBlobStoreTests
{
    private static readonly string[] PossibleConcurrentValues = ["first", "second"];

    [Fact]
    public void Local_rejects_blank_root_configuration()
    {
        Assert.Throws<ArgumentException>(
            () => new LocalBlobStoreOptions(" "));
    }

    [Fact]
    public void Local_requires_an_absolute_dedicated_root()
    {
        Assert.Throws<ArgumentException>(
            () => new LocalBlobStoreOptions("relative/store"));
        Assert.Throws<ArgumentException>(
            () => new LocalBlobStoreOptions(Path.GetPathRoot(Environment.CurrentDirectory)!));
    }

    [Fact]
    public void Local_reports_only_implemented_capabilities()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            LocalBlobStore store = CreateStore(scratch);

            Assert.False(store.Capabilities.SupportsDirectUpload);
            Assert.False(store.Capabilities.SupportsMultipartUpload);
            Assert.True(store.Capabilities.SupportsRangeReads);
            Assert.True(store.Capabilities.SupportsConditionalRead);
            Assert.True(store.Capabilities.SupportsConditionalCreate);
            Assert.True(store.Capabilities.SupportsConditionalReplace);
            Assert.True(store.Capabilities.SupportsConditionalCopy);
            Assert.True(store.Capabilities.SupportsConditionalDelete);
            Assert.False(store.Capabilities.SupportsConditionalMultipartCompletion);
            Assert.True(store.Capabilities.SupportsServerSideCopy);
            Assert.False(store.Capabilities.SupportsObjectVersioning);
            Assert.False(store.Capabilities.SupportsSignedRead);
            Assert.Equal(BlobConsistencyModel.Strong, store.Capabilities.ReadAfterWriteConsistency);
            Assert.Equal(BlobConsistencyModel.Strong, store.Capabilities.ListAfterWriteConsistency);
            Assert.Equal(
                [BlobChecksumAlgorithm.Sha256],
                store.Capabilities.NativeChecksumAlgorithms);
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_persists_content_metadata_identity_and_checksum_across_restart()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            string root = Path.Combine(scratch, "store");
            BlobKey key = new("originals/aa/asset/revision/upload.jpg");
            BlobMetadata metadata = new(
                [new KeyValuePair<string, string>("tenant", "tenant-1")]);
            BlobChecksum checksum = Sha256("persistent");
            LocalBlobStore first = new(new LocalBlobStoreOptions(root));

            BlobWriteResult written = await first.PutAsync(
                key,
                new TrackingReplayableContent("persistent"),
                new BlobWriteOptions(
                    new BlobMediaType("image/jpeg"),
                    metadata,
                    [checksum],
                    BlobRequestConditions.CreateOnly),
                CancellationToken.None);

            LocalBlobStore restarted = new(new LocalBlobStoreOptions(root));
            BlobHead? observed = await restarted.HeadAsync(key, CancellationToken.None);
            Assert.NotNull(observed);
            Assert.Equal(written.Head.Identity, observed.Identity);
            Assert.Equal(written.Head.Properties.EntityTag, observed.Properties.EntityTag);
            Assert.Equal("image/jpeg", observed.Properties.ContentType.Value);
            Assert.Equal("tenant-1", observed.Properties.Metadata["tenant"]);
            Assert.Contains(checksum, observed.Properties.Checksums);
            Assert.Equal("persistent", await ReadTextAsync(restarted, key));
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_create_only_is_atomic_under_concurrency()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            LocalBlobStore store = CreateStore(scratch);
            BlobKey key = new("derivatives/v1/source/recipe.webp");

            Task<WriteAttempt> first = CaptureWriteAsync(store, key, "first");
            Task<WriteAttempt> second = CaptureWriteAsync(store, key, "second");
            WriteAttempt[] attempts = await Task.WhenAll(first, second);

            Assert.Single(attempts, attempt => attempt.Result is not null);
            BlobStoreException conflict = Assert.Single(
                attempts,
                attempt => attempt.Error is not null).Error!;
            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, conflict.Code);
            Assert.Contains(
                await ReadTextAsync(store, key),
                PossibleConcurrentValues);
            Assert.Empty(FindStagingFiles(scratch));
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_failed_checksum_removes_staging_and_destination()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            LocalBlobStore store = CreateStore(scratch);
            BlobKey key = new("staging/aa/failed");
            BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.PutAsync(
                    key,
                    new TrackingReplayableContent("actual"),
                    new BlobWriteOptions(checksums:
                        [new BlobChecksum(BlobChecksumAlgorithm.Sha256, new string('0', 64))]),
                    CancellationToken.None));

            Assert.Equal(BlobStoreErrorCode.IntegrityMismatch, error.Code);
            Assert.Null(await store.HeadAsync(key, CancellationToken.None));
            Assert.Empty(FindStagingFiles(scratch));
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_cancelled_write_disposes_source_and_removes_staging()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            LocalBlobStore store = CreateStore(scratch);
            BlobKey key = new("staging/aa/cancelled");
            using CancellationTokenSource cancellation = new();
            using CancelAfterFirstReadContent content = new(cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await store.PutAsync(
                    key,
                    content,
                    BlobWriteOptions.None,
                    cancellation.Token));

            Assert.True(content.Disposed);
            Assert.Null(await store.HeadAsync(key, CancellationToken.None));
            Assert.Empty(FindStagingFiles(scratch));
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_rejects_out_of_bounds_ranges()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            LocalBlobStore store = CreateStore(scratch);
            BlobKey key = new("contract/range-invalid");
            await PutAsync(store, key, "1234");

            BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.OpenReadAsync(
                    key,
                    new BlobReadOptions(new BlobRange(3, 2)),
                    CancellationToken.None));

            Assert.Equal(BlobStoreErrorCode.InvalidRange, error.Code);
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_enforces_version_and_etag_replace_and_delete_conditions()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            LocalBlobStore store = CreateStore(scratch);
            BlobKey key = new("contract/conditions");
            BlobWriteResult initial = await PutAsync(store, key, "v1");

            BlobStoreException replaceConflict = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.PutAsync(
                    key,
                    new TrackingReplayableContent("bad"),
                    new BlobWriteOptions(
                        conditions: new BlobRequestConditions(
                            new BlobVersion("wrong"))),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, replaceConflict.Code);

            BlobWriteResult replaced = await store.PutAsync(
                key,
                new TrackingReplayableContent("v2"),
                new BlobWriteOptions(
                    conditions: new BlobRequestConditions(
                        ifEntityTagMatch: initial.Head.Properties.EntityTag)),
                CancellationToken.None);
            Assert.False(replaced.Created);

            BlobStoreException deleteConflict = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.DeleteAsync(
                    key,
                    new BlobDeleteOptions(
                        new BlobRequestConditions(initial.Head.Identity.Version)),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, deleteConflict.Code);

            BlobDeleteResult deleted = await store.DeleteAsync(
                key,
                new BlobDeleteOptions(
                    new BlobRequestConditions(replaced.Head.Identity.Version)),
                CancellationToken.None);
            Assert.True(deleted.Deleted);
            Assert.False((await store.DeleteAsync(
                key,
                BlobDeleteOptions.None,
                CancellationToken.None)).Deleted);
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_listing_is_ordinal_prefix_bounded_and_restart_stable()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            string root = Path.Combine(scratch, "store");
            LocalBlobStore first = new(new LocalBlobStoreOptions(root));
            await PutAsync(first, new BlobKey("contract/list/b"), "b");
            await PutAsync(first, new BlobKey("contract/list/a"), "a");
            await PutAsync(first, new BlobKey("contract/listing/not-in-prefix"), "x");
            await PutAsync(first, new BlobKey("other/list/a"), "x");

            LocalBlobStore restarted = new(new LocalBlobStoreOptions(root));
            List<string> keys = [];
            await foreach (BlobHead head in restarted.ListAsync(
                               new BlobListOptions("contract/list/"),
                               CancellationToken.None))
            {
                keys.Add(head.Identity.Key.Value);
            }

            Assert.Equal(["contract/list/a", "contract/list/b"], keys);
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_explicitly_rejects_direct_signed_and_multipart_operations()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            await using LocalBlobStoreFixture fixture =
                LocalBlobStoreFixture.CreateAt(scratch);
            BlobKey key = fixture.Key("unsupported");

            await AssertUnsupportedAsync(
                () => fixture.Store.CreateDirectUploadAsync(
                    fixture.DirectRequest(key),
                    CancellationToken.None));
            await AssertUnsupportedAsync(
                () => fixture.Store.BeginMultipartAsync(
                    fixture.MultipartRequest(key),
                    CancellationToken.None));
            await AssertUnsupportedAsync(
                () => fixture.Store.CreatePartPlanAsync(
                    fixture.Session(key),
                    1,
                    CancellationToken.None));
            await AssertUnsupportedAsync(
                () => fixture.Store.CompleteMultipartAsync(
                    fixture.Session(key),
                    [new UploadedPart(1, new BlobEntityTag("etag"), null, 8)],
                    CancellationToken.None));
            await AssertUnsupportedAsync(
                () => fixture.Store.AbortMultipartAsync(
                    fixture.Session(key),
                    CancellationToken.None));
            await AssertUnsupportedAsync(
                () => fixture.Store.CreateReadGrantAsync(
                    key,
                    new ReadGrantOptions(TimeSpan.FromMinutes(1)),
                    CancellationToken.None));
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("../traversal")]
    [InlineData("safe/../escape")]
    [InlineData("safe\\windows-separator")]
    [InlineData("safe//normalized")]
    [InlineData("unicodé")]
    public void Local_keys_reject_absolute_traversal_separator_and_unicode_forms(
        string value)
    {
        Assert.Throws<ArgumentException>(() => new BlobKey(value));
    }

    [Fact]
    public void Local_rejects_a_symlinked_root()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            string realRoot = Path.Combine(scratch, "real");
            string linkedRoot = Path.Combine(scratch, "linked");
            Directory.CreateDirectory(realRoot);
            if (!TryCreateDirectoryLink(linkedRoot, realRoot))
            {
                return;
            }

            BlobStoreException error = Assert.Throws<BlobStoreException>(
                () => new LocalBlobStore(new LocalBlobStoreOptions(linkedRoot)));
            Assert.Equal(BlobStoreErrorCode.InvalidRequest, error.Code);
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task Local_never_follows_a_symlinked_object_outside_the_root()
    {
        string scratch = LocalTestDirectory.Create();
        try
        {
            LocalBlobStore store = CreateStore(scratch);
            BlobKey key = new("contract/symlink");
            await PutAsync(store, key, "inside");
            string objectPath = Assert.Single(
                Directory.EnumerateFiles(scratch, "*.blob", SearchOption.AllDirectories));
            string outsidePath = Path.Combine(scratch, "outside-secret");
            File.Move(objectPath, outsidePath);
            if (!TryCreateFileLink(objectPath, outsidePath))
            {
                File.Move(outsidePath, objectPath);
                return;
            }

            BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.HeadAsync(key, CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.InvalidRequest, error.Code);
        }
        finally
        {
            LocalTestDirectory.Delete(scratch);
        }
    }

    private static LocalBlobStore CreateStore(string scratch) =>
        new(new LocalBlobStoreOptions(Path.Combine(scratch, "store")));

    private static ValueTask<BlobWriteResult> PutAsync(
        LocalBlobStore store,
        BlobKey key,
        string value) =>
        store.PutAsync(
            key,
            new TrackingReplayableContent(value),
            new BlobWriteOptions(contentType: new BlobMediaType("text/plain")),
            CancellationToken.None);

    private static async Task<string> ReadTextAsync(LocalBlobStore store, BlobKey key)
    {
        await using BlobReadHandle handle = await store.OpenReadAsync(
            key,
            BlobReadOptions.Full,
            CancellationToken.None);
        using StreamReader reader = new(handle.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    private static BlobChecksum Sha256(string value) =>
        new(
            BlobChecksumAlgorithm.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))));

    private static async Task<WriteAttempt> CaptureWriteAsync(
        LocalBlobStore store,
        BlobKey key,
        string value)
    {
        try
        {
            BlobWriteResult result = await store.PutAsync(
                key,
                new TrackingReplayableContent(value),
                new BlobWriteOptions(conditions: BlobRequestConditions.CreateOnly),
                CancellationToken.None);
            return new WriteAttempt(result, null);
        }
        catch (BlobStoreException error)
        {
            return new WriteAttempt(null, error);
        }
    }

    private static IEnumerable<string> FindStagingFiles(string path) =>
        Directory.EnumerateFiles(path, "*.staging", SearchOption.AllDirectories);

    private static async Task AssertUnsupportedAsync(Func<ValueTask> operation)
    {
        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await operation());
        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
    }

    private static async Task AssertUnsupportedAsync<T>(Func<ValueTask<T>> operation)
    {
        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await operation());
        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception error) when (
            error is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception error) when (
            error is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private sealed record WriteAttempt(
        BlobWriteResult? Result,
        BlobStoreException? Error);

    private sealed class CancelAfterFirstReadContent(
        CancellationTokenSource cancellation) : IReplayableBlobContent, IDisposable
    {
        private readonly byte[] _bytes = new byte[256 * 1024];
        private TrackingStream? _stream;

        public long Length => _bytes.LongLength;

        public bool Disposed => _stream?.Disposed ?? false;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream = new TrackingStream(_bytes, cancellation);
            return ValueTask.FromResult<Stream>(_stream);
        }

        public void Dispose() => _stream?.Dispose();

        private sealed class TrackingStream(
            byte[] bytes,
            CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
        {
            private bool _cancelled;

            public bool Disposed { get; private set; }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                ValueTask<int> read = base.ReadAsync(buffer, cancellationToken);
                if (!_cancelled)
                {
                    _cancelled = true;
                    cancellation.Cancel();
                }

                return read;
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }

}
