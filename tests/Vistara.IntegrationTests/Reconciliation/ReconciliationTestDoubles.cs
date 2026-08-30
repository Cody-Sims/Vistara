using System.Runtime.CompilerServices;
using Vistara.Application.Common.Storage;

namespace Vistara.IntegrationTests.Reconciliation;

internal sealed class ReconciliationBlobStore : IBlobStore
{
    private readonly Dictionary<string, DateTimeOffset> _objects =
        new(StringComparer.Ordinal);

    public string Name => "reconciliation-test";

    public BlobStoreCapabilities Capabilities { get; } = new()
    {
        SupportsConditionalRead = true,
        SupportsConditionalCopy = true,
        SupportsConditionalCreate = true,
        SupportsConditionalDelete = true,
        SupportsServerSideCopy = true,
        ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
    };

    internal IReadOnlyCollection<string> Keys => _objects.Keys;

    internal void Add(string objectKey, DateTimeOffset lastModifiedUtc) =>
        _objects[objectKey] = lastModifiedUtc;

    public ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        return ValueTask.FromResult(
            _objects.TryGetValue(key.Value, out DateTimeOffset modified)
                ? Head(key.Value, modified)
                : null);
    }

    public async IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach ((string key, DateTimeOffset modified) in _objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Prefix is not null &&
                !key.StartsWith(options.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return Head(key, modified);
        }

        await Task.CompletedTask;
    }

    public ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        if (!_objects.Remove(key.Value))
        {
            return ValueTask.FromResult(new BlobDeleteResult(false, null));
        }

        return ValueTask.FromResult(
            new BlobDeleteResult(
                true,
                new BlobIdentity(key, Version(key.Value))));
    }

    public ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key,
        BlobReadOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<BlobWriteResult> PutAsync(
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<MultipartCompletion> CompleteMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask AbortMultipartAsync(
        MultipartSession session,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
        BlobKey key,
        ReadGrantOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    private static BlobVersion Version(string objectKey) =>
        new($"v-{objectKey.GetHashCode(StringComparison.Ordinal)}");

    private static BlobHead Head(string objectKey, DateTimeOffset modified) =>
        new(
            new BlobIdentity(new BlobKey(objectKey), Version(objectKey)),
            new BlobProperties(
                contentLength: 1,
                new BlobMediaType("application/octet-stream"),
                modified,
                Version(objectKey),
                new BlobEntityTag("\"etag\""),
                [],
                BlobMetadata.Empty));
}
