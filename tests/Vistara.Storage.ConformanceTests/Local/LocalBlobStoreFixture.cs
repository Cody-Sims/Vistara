using System.Text;
using Vistara.Application.Common.Storage;
using Vistara.Storage.ConformanceTests.Fixtures;
using Vistara.Storage.Local;

namespace Vistara.Storage.ConformanceTests.Local;

internal sealed class LocalBlobStoreFixture : IBlobStoreFixture
{
    private readonly string _scratchPath;

    private LocalBlobStoreFixture(string scratchPath)
    {
        _scratchPath = scratchPath;
        Store = new LocalBlobStore(new LocalBlobStoreOptions(
            Path.Combine(scratchPath, "store")));
    }

    public IBlobStore Store { get; }

    public static LocalBlobStoreFixture Create() =>
        new(LocalTestDirectory.Create());

    public static LocalBlobStoreFixture CreateAt(string scratchPath) =>
        new(scratchPath);

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
            "local-session",
            key,
            DateTimeOffset.UtcNow.AddMinutes(10),
            8,
            BlobRequestConditions.CreateOnly,
            1,
            1,
            8);

    public ValueTask DisposeAsync()
    {
        LocalTestDirectory.Delete(_scratchPath);
        return ValueTask.CompletedTask;
    }
}
