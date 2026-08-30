namespace Vistara.Application.Gallery.Albums;

public sealed record AlbumUpdate(
    OptionalField<string> Name,
    OptionalField<string> Description,
    OptionalField<Guid?> CoverAssetId);

public interface IAlbumApplication
{
    ValueTask<CurationResult<IReadOnlyList<AlbumSnapshot>>> ListAsync(
        CurationActor actor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<AlbumSnapshot>> GetAsync(
        CurationActor actor,
        Guid albumId,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<AlbumSnapshot>> CreateAsync(
        CurationActor actor,
        Guid albumId,
        string name,
        string? description,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<AlbumSnapshot>> UpdateAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        AlbumUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<bool>> DeleteAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<AlbumSnapshot>> AddItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<AlbumSnapshot>> RemoveItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<AlbumSnapshot>> ReorderItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<AlbumItemPosition> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IAlbumCurationStore : IAlbumApplication;

public sealed class AlbumApplication(IAlbumCurationStore store) : IAlbumApplication
{
    private readonly IAlbumCurationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<CurationResult<IReadOnlyList<AlbumSnapshot>>> ListAsync(
        CurationActor actor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > CurationValidation.MaximumBatchSize)
        {
            return ValueTask.FromResult(
                CurationResult.Failure<IReadOnlyList<AlbumSnapshot>>(
                    CurationFailure.Invalid("album_limit_invalid")));
        }

        return _store.ListAsync(actor, limit, cancellationToken);
    }

    public ValueTask<CurationResult<AlbumSnapshot>> GetAsync(
        CurationActor actor,
        Guid albumId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return CurationValidation.IsUuid7(albumId)
            ? _store.GetAsync(actor, albumId, cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                CurationFailure.NotFound("album_not_found")));
    }

    public ValueTask<CurationResult<AlbumSnapshot>> CreateAsync(
        CurationActor actor,
        Guid albumId,
        string name,
        string? description,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string normalizedName = CurationValidation.CollapseWhitespace(name);
        if (!ValidMutation(albumId, idempotencyKey, now) ||
            normalizedName.Length is 0 or > 500)
        {
            return ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                CurationFailure.Invalid("album_request_invalid")));
        }

        string? normalizedDescription = description is null
            ? null
            : CurationValidation.CollapseWhitespace(description);
        if (normalizedDescription?.Length > 4000)
        {
            return ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                CurationFailure.Invalid("album_description_too_long")));
        }

        return _store.CreateAsync(
            actor,
            albumId,
            normalizedName,
            normalizedDescription,
            idempotencyKey,
            now,
            cancellationToken);
    }

    public ValueTask<CurationResult<AlbumSnapshot>> UpdateAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        AlbumUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(update);
        if (!ValidMutation(albumId, idempotencyKey, now) || expectedVersion <= 0)
        {
            return ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                CurationFailure.Invalid("album_request_invalid")));
        }

        OptionalField<string> name = update.Name;
        if (name.IsSpecified)
        {
            string normalized = CurationValidation.CollapseWhitespace(name.Value);
            if (normalized.Length is 0 or > 500)
            {
                return ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                    CurationFailure.Invalid("album_name_required")));
            }

            name = OptionalField.Specified(normalized);
        }

        OptionalField<string> description = update.Description;
        if (description.IsSpecified && description.Value is not null)
        {
            string normalizedDescription =
                CurationValidation.CollapseWhitespace(description.Value);
            if (normalizedDescription.Length > 4000)
            {
                return ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                    CurationFailure.Invalid("album_description_too_long")));
            }

            description = OptionalField.Specified(normalizedDescription);
        }

        return _store.UpdateAsync(
            actor,
            albumId,
            expectedVersion,
            update with { Name = name, Description = description },
            idempotencyKey,
            now,
            cancellationToken);
    }

    public ValueTask<CurationResult<bool>> DeleteAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return ValidMutation(albumId, idempotencyKey, now) && expectedVersion > 0
            ? _store.DeleteAsync(
                actor,
                albumId,
                expectedVersion,
                idempotencyKey,
                now,
                cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<bool>(
                CurationFailure.Invalid("album_request_invalid")));
    }

    public ValueTask<CurationResult<AlbumSnapshot>> AddItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateItemsAsync(
            actor,
            albumId,
            expectedVersion,
            items,
            idempotencyKey,
            now,
            _store.AddItemsAsync,
            cancellationToken);

    public ValueTask<CurationResult<AlbumSnapshot>> RemoveItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateItemsAsync(
            actor,
            albumId,
            expectedVersion,
            items,
            idempotencyKey,
            now,
            _store.RemoveItemsAsync,
            cancellationToken);

    public ValueTask<CurationResult<AlbumSnapshot>> ReorderItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<AlbumItemPosition> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(items);
        bool valid = ValidMutation(albumId, idempotencyKey, now) &&
            expectedVersion > 0 &&
            items.Count is >= 1 and <= CurationValidation.MaximumBatchSize &&
            items.All(item => CurationValidation.IsUuid7(item.AssetId) && item.Position >= 0) &&
            items.Select(item => item.AssetId).Distinct().Count() == items.Count &&
            items.Select(item => item.Position).Distinct().Count() == items.Count;
        return valid
            ? _store.ReorderItemsAsync(
                actor,
                albumId,
                expectedVersion,
                items,
                idempotencyKey,
                now,
                cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                CurationFailure.Invalid("album_order_invalid")));
    }

    private static ValueTask<CurationResult<AlbumSnapshot>> MutateItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        Func<
            CurationActor,
            Guid,
            long,
            IReadOnlyList<VersionedAssetTarget>,
            string,
            DateTimeOffset,
            CancellationToken,
            ValueTask<CurationResult<AlbumSnapshot>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(items);
        bool valid = ValidMutation(albumId, idempotencyKey, now) &&
            expectedVersion > 0 &&
            items.Count is >= 1 and <= CurationValidation.MaximumBatchSize &&
            items.All(item => CurationValidation.IsUuid7(item.AssetId) && item.Version > 0) &&
            items.Select(item => item.AssetId).Distinct().Count() == items.Count;
        return valid
            ? operation(
                actor,
                albumId,
                expectedVersion,
                items,
                idempotencyKey,
                now,
                cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<AlbumSnapshot>(
                CurationFailure.Invalid("album_items_invalid")));
    }

    private static bool ValidMutation(
        Guid id,
        string idempotencyKey,
        DateTimeOffset now) =>
        CurationValidation.IsUuid7(id) &&
        CurationValidation.IsValidIdempotencyKey(idempotencyKey) &&
        CurationValidation.IsUtc(now);
}
