namespace Vistara.Application.Gallery.Favorites;

public interface IFavoriteApplication
{
    ValueTask<CurationResult<CuratedAssetSnapshot>> SetAsync(
        CurationActor actor,
        Guid assetId,
        long expectedVersion,
        bool favorite,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<BulkCurationSubmission>> QueueBulkAsync(
        CurationActor actor,
        Guid jobId,
        BulkCurationRequest request,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IFavoriteCurationStore : IFavoriteApplication
{
    ValueTask<IReadOnlyList<BulkCurationItemResult>> ExecuteBulkAsync(
        CurationActor actor,
        BulkCurationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed class FavoriteApplication(IFavoriteCurationStore store) : IFavoriteApplication
{
    private readonly IFavoriteCurationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<CurationResult<CuratedAssetSnapshot>> SetAsync(
        CurationActor actor,
        Guid assetId,
        long expectedVersion,
        bool favorite,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        bool valid = CurationValidation.IsUuid7(assetId) &&
            expectedVersion > 0 &&
            CurationValidation.IsValidIdempotencyKey(idempotencyKey) &&
            CurationValidation.IsUtc(now);
        return valid
            ? _store.SetAsync(
                actor,
                assetId,
                expectedVersion,
                favorite,
                idempotencyKey,
                now,
                cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<CuratedAssetSnapshot>(
                CurationFailure.Invalid("favorite_request_invalid")));
    }

    public ValueTask<CurationResult<BulkCurationSubmission>> QueueBulkAsync(
        CurationActor actor,
        Guid jobId,
        BulkCurationRequest request,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        bool valid = CurationValidation.IsUuid7(jobId) &&
            CurationValidation.IsValidIdempotencyKey(idempotencyKey) &&
            CurationValidation.IsUtc(now) &&
            request.Items.Count is >= 1 and <= CurationValidation.MaximumBatchSize &&
            request.Items.All(item =>
                CurationValidation.IsUuid7(item.AssetId) && item.Version > 0) &&
            request.Items.Select(item => item.AssetId).Distinct().Count() ==
                request.Items.Count &&
            IsCurationAction(request.Action);
        return valid
            ? _store.QueueBulkAsync(
                actor,
                jobId,
                request,
                idempotencyKey,
                now,
                cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<BulkCurationSubmission>(
                CurationFailure.Invalid("bulk_request_invalid")));
    }

    private static bool IsCurationAction(BulkCurationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            "addTag" or "removeTag" =>
                action.TagId is { } tagId &&
                CurationValidation.IsUuid7(tagId) &&
                action.AlbumId is null &&
                action.Favorite is null,
            "addToAlbum" or "removeFromAlbum" =>
                action.AlbumId is { } albumId &&
                CurationValidation.IsUuid7(albumId) &&
                action.TagId is null &&
                action.Favorite is null,
            "setFavorite" =>
                action.Favorite is not null &&
                action.TagId is null &&
                action.AlbumId is null,
            _ => false,
        };
    }
}
