using Vistara.Application.Common.Storage;

namespace Vistara.Storage.ConformanceTests.Fixtures;

public interface IBlobStoreFixture : IAsyncDisposable
{
    IBlobStore Store { get; }

    BlobKey Key(string suffix);

    IReplayableBlobContent Content(string value);

    TrackingReplayableContent TrackingContent(string value);

    ValueTask SeedAsync(BlobKey key, string value);

    ValueTask<string> ReadTextAsync(BlobKey key);

    DirectUploadRequest DirectRequest(BlobKey key);

    MultipartRequest MultipartRequest(BlobKey key);

    MultipartSession Session(BlobKey key);
}
