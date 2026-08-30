using Vistara.Application.Common;

namespace Vistara.Application.Gallery.Queries;

public enum AssetQueryResultStatus
{
    Success,
    InvalidQuery,
    InvalidCursor,
    CursorMismatch,
    NotFound,
    VersionConflict,
    ValidationFailed,
    Unavailable,
}

public sealed record AssetQueryPageResult(
    AssetQueryResultStatus Status,
    AssetQueryPage? Page)
{
    public static AssetQueryPageResult Success(AssetQueryPage page) =>
        new(AssetQueryResultStatus.Success, page);

    public static AssetQueryPageResult Failure(AssetQueryResultStatus status) =>
        new(status, null);
}

public sealed record AssetDetailResult(
    AssetQueryResultStatus Status,
    AssetDetail? Detail)
{
    public static AssetDetailResult Success(AssetDetail detail) =>
        new(AssetQueryResultStatus.Success, detail);

    public static AssetDetailResult Failure(AssetQueryResultStatus status) =>
        new(status, null);
}

public sealed record AssetMetadataResult(
    AssetQueryResultStatus Status,
    AssetMetadata? Metadata)
{
    public static AssetMetadataResult Success(AssetMetadata metadata) =>
        new(AssetQueryResultStatus.Success, metadata);

    public static AssetMetadataResult Failure(AssetQueryResultStatus status) =>
        new(status, null);
}

public sealed record AssetFacetResult(
    AssetQueryResultStatus Status,
    IReadOnlyList<AssetFacetGroup>? Groups)
{
    public static AssetFacetResult Success(IReadOnlyList<AssetFacetGroup> groups) =>
        new(AssetQueryResultStatus.Success, groups);

    public static AssetFacetResult Failure(AssetQueryResultStatus status) =>
        new(status, null);
}

public sealed record AssetUpdateResult(
    AssetQueryResultStatus Status,
    AssetDetail? Detail,
    bool Replayed)
{
    public static AssetUpdateResult Success(
        AssetDetail detail,
        bool replayed = false) =>
        new(AssetQueryResultStatus.Success, detail, replayed);

    public static AssetUpdateResult Failure(AssetQueryResultStatus status) =>
        new(status, null, false);
}

public interface IAssetQueryService
{
    ValueTask<AssetQueryPageResult> ListAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        string? cursor,
        CancellationToken cancellationToken);

    ValueTask<AssetQueryPageResult> TimelineAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        string groupBy,
        string? cursor,
        CancellationToken cancellationToken);

    ValueTask<AssetFacetResult> GetFacetsAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        CancellationToken cancellationToken);

    ValueTask<AssetDetailResult> GetAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken);

    ValueTask<AssetMetadataResult> GetMetadataAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken);

    ValueTask<AssetUpdateResult> UpdateAsync(
        AssetQueryScope scope,
        Guid assetId,
        long expectedVersion,
        string idempotencyKey,
        AssetMetadataPatch patch,
        CancellationToken cancellationToken);
}

public sealed class AssetQueryService(
    IAssetQueryStore store,
    AssetCursorProtector cursors,
    IClock clock) : IAssetQueryService
{
    private readonly IAssetQueryStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly AssetCursorProtector _cursors =
        cursors ?? throw new ArgumentNullException(nameof(cursors));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    public async ValueTask<AssetQueryPageResult> ListAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);
        (AssetQueryResultStatus status, AssetQueryWindow? window) =
            ReadWindow(criteria, cursor);
        if (window is null)
        {
            return AssetQueryPageResult.Failure(status);
        }

        AssetQuerySlice slice = await _store.QueryAsync(
            scope,
            criteria,
            window,
            cancellationToken);
        string? nextCursor = slice.HasMore && slice.NextKey is not null
            ? _cursors.Protect(new AssetCursorState(
                criteria.FilterHash,
                window.SnapshotAtUtc,
                criteria.Sort,
                criteria.Direction,
                slice.NextKey.NullRank,
                slice.NextKey.InstantValue,
                slice.NextKey.TextValue,
                slice.NextKey.NumberValue,
                slice.NextKey.AssetId))
            : null;
        return AssetQueryPageResult.Success(
            new AssetQueryPage(slice.Items, nextCursor));
    }

    public ValueTask<AssetQueryPageResult> TimelineAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        string groupBy,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (groupBy is not ("day" or "month" or "year"))
        {
            return ValueTask.FromResult(
                AssetQueryPageResult.Failure(AssetQueryResultStatus.InvalidQuery));
        }

        return ListAsync(scope, criteria, cursor, cancellationToken);
    }

    public async ValueTask<AssetFacetResult> GetFacetsAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AssetFacetGroup> groups = await _store.GetFacetsAsync(
            scope,
            criteria,
            _clock.UtcNow,
            cancellationToken);
        return AssetFacetResult.Success(groups);
    }

    public async ValueTask<AssetDetailResult> GetAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        AssetDetail? detail =
            await _store.GetAsync(scope, assetId, cancellationToken);
        return detail is null
            ? AssetDetailResult.Failure(AssetQueryResultStatus.NotFound)
            : AssetDetailResult.Success(detail);
    }

    public async ValueTask<AssetMetadataResult> GetMetadataAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        AssetMetadata? metadata =
            await _store.GetMetadataAsync(scope, assetId, cancellationToken);
        return metadata is null
            ? AssetMetadataResult.Failure(AssetQueryResultStatus.NotFound)
            : AssetMetadataResult.Success(metadata);
    }

    public async ValueTask<AssetUpdateResult> UpdateAsync(
        AssetQueryScope scope,
        Guid assetId,
        long expectedVersion,
        string idempotencyKey,
        AssetMetadataPatch patch,
        CancellationToken cancellationToken)
    {
        if (expectedVersion < 1 ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 128)
        {
            return AssetUpdateResult.Failure(AssetQueryResultStatus.ValidationFailed);
        }

        AssetUpdateStoreResult result = await _store.UpdateAsync(
            scope,
            assetId,
            expectedVersion,
            idempotencyKey,
            patch,
            _clock.UtcNow,
            cancellationToken);
        return result.Status switch
        {
            AssetUpdateStoreStatus.Updated =>
                AssetUpdateResult.Success(result.Detail!),
            AssetUpdateStoreStatus.Replayed =>
                AssetUpdateResult.Success(result.Detail!, replayed: true),
            AssetUpdateStoreStatus.NotFound =>
                AssetUpdateResult.Failure(AssetQueryResultStatus.NotFound),
            AssetUpdateStoreStatus.VersionConflict =>
                AssetUpdateResult.Failure(AssetQueryResultStatus.VersionConflict),
            _ => AssetUpdateResult.Failure(AssetQueryResultStatus.ValidationFailed),
        };
    }

    private (AssetQueryResultStatus Status, AssetQueryWindow? Window) ReadWindow(
        AssetQueryCriteria criteria,
        string? cursor)
    {
        if (cursor is null)
        {
            return (
                AssetQueryResultStatus.Success,
                new AssetQueryWindow(_clock.UtcNow, null));
        }

        AssetCursorReadResult read = _cursors.Read(cursor);
        if (read.Status != AssetCursorReadStatus.Valid || read.State is null)
        {
            return (AssetQueryResultStatus.InvalidCursor, null);
        }

        AssetCursorState state = read.State;
        if (!CryptographicEquals(state.FilterHash, criteria.FilterHash) ||
            state.Sort != criteria.Sort ||
            state.Direction != criteria.Direction)
        {
            return (AssetQueryResultStatus.CursorMismatch, null);
        }

        return (
            AssetQueryResultStatus.Success,
            new AssetQueryWindow(
                state.SnapshotAtUtc,
                new AssetQueryKey(
                    state.NullRank,
                    state.InstantValue,
                    state.TextValue,
                    state.NumberValue,
                    state.AssetId)));
    }

    private static bool CryptographicEquals(string left, string right)
    {
        byte[] leftBytes = Convert.FromHexString(left);
        byte[] rightBytes = Convert.FromHexString(right);
        try
        {
            return System.Security.Cryptography.CryptographicOperations
                .FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(leftBytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
