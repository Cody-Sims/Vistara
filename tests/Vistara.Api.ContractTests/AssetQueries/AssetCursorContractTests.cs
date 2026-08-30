using Vistara.Application.Common;
using Vistara.Application.Gallery.Queries;
using Xunit;

namespace Vistara.Api.ContractTests.AssetQueries;

public sealed class AssetCursorContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");
    private static readonly Guid ActorId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000702");
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000703");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly byte[] CursorKey =
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void Cursor_is_opaque_authenticated_and_round_trips_snapshot_and_keyset()
    {
        var protector = new AssetCursorProtector(CursorKey);
        var state = new AssetCursorState(
            new string('a', 64),
            Now,
            AssetSort.CapturedAt,
            SortDirection.Descending,
            NullRank: 1,
            InstantValue: Now.AddDays(-1),
            TextValue: null,
            NumberValue: null,
            AssetId);

        string cursor = protector.Protect(state);
        AssetCursorReadResult result = protector.Read(cursor);

        Assert.Equal(AssetCursorReadStatus.Valid, result.Status);
        Assert.Equal(state, result.State);
        Assert.DoesNotContain(new string('a', 64), cursor, StringComparison.Ordinal);
        Assert.DoesNotContain(AssetId.ToString("D"), cursor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cursor_tampering_is_rejected_without_disclosing_payload_details()
    {
        var protector = new AssetCursorProtector(CursorKey);
        string cursor = protector.Protect(new AssetCursorState(
            new string('a', 64),
            Now,
            AssetSort.ImportedAt,
            SortDirection.Descending,
            NullRank: 0,
            InstantValue: Now,
            TextValue: null,
            NumberValue: null,
            AssetId));
        char replacement = cursor[^1] == 'A' ? 'B' : 'A';

        AssetCursorReadResult result =
            protector.Read(cursor[..^1] + replacement);

        Assert.Equal(AssetCursorReadStatus.Invalid, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task Cursor_cannot_be_reused_with_different_filters_or_sort()
    {
        var store = new RecordingAssetQueryStore();
        var service = new AssetQueryService(
            store,
            new AssetCursorProtector(CursorKey),
            new FixedClock(Now));
        var scope = new AssetQueryScope(TenantId, ActorId);
        AssetQueryCriteria original = AssetQueryCriteria.Create(
            limit: 1,
            search: "lake",
            statuses: ["Ready"],
            contentTypes: ["image/jpeg"],
            sort: "capturedAt",
            direction: "desc");

        AssetQueryPageResult first =
            await service.ListAsync(scope, original, cursor: null, CancellationToken.None);
        AssetQueryCriteria changed = AssetQueryCriteria.Create(
            limit: 1,
            search: "mountain",
            statuses: ["Ready"],
            contentTypes: ["image/jpeg"],
            sort: "capturedAt",
            direction: "desc");
        AssetQueryPageResult mismatch = await service.ListAsync(
            scope,
            changed,
            first.Page!.NextCursor,
            CancellationToken.None);

        Assert.Equal(AssetQueryResultStatus.CursorMismatch, mismatch.Status);
        Assert.Equal(1, store.QueryCalls);
    }

    [Fact]
    public async Task Cursor_snapshot_is_reused_across_pages()
    {
        var store = new RecordingAssetQueryStore();
        var service = new AssetQueryService(
            store,
            new AssetCursorProtector(CursorKey),
            new FixedClock(Now));
        var scope = new AssetQueryScope(TenantId, ActorId);
        AssetQueryCriteria criteria = AssetQueryCriteria.Create(limit: 1);

        AssetQueryPageResult first =
            await service.ListAsync(scope, criteria, cursor: null, CancellationToken.None);
        _ = await service.ListAsync(
            scope,
            criteria,
            first.Page!.NextCursor,
            CancellationToken.None);

        Assert.Equal([Now, Now], store.Snapshots);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingAssetQueryStore : IAssetQueryStore
    {
        public int QueryCalls { get; private set; }
        public List<DateTimeOffset> Snapshots { get; } = [];

        public ValueTask<AssetQuerySlice> QueryAsync(
            AssetQueryScope scope,
            AssetQueryCriteria criteria,
            AssetQueryWindow window,
            CancellationToken cancellationToken)
        {
            QueryCalls++;
            Snapshots.Add(window.SnapshotAtUtc);
            var item = new AssetQueryItem(
                AssetId,
                "Lake",
                null,
                "Ready",
                "Private",
                1,
                "image/jpeg",
                "jpeg",
                800,
                600,
                10_000,
                Now.AddDays(-1),
                Now.AddDays(-2),
                Now,
                false,
                [],
                [],
                1);
            return ValueTask.FromResult(
                new AssetQuerySlice(
                    [item],
                    new AssetQueryKey(
                        NullRank: 0,
                        InstantValue: item.CapturedAt,
                        TextValue: null,
                        NumberValue: null,
                        item.Id),
                    HasMore: true));
        }

        public ValueTask<AssetDetail?> GetAsync(
            AssetQueryScope scope,
            Guid assetId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AssetDetail?>(null);

        public ValueTask<AssetMetadata?> GetMetadataAsync(
            AssetQueryScope scope,
            Guid assetId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AssetMetadata?>(null);

        public ValueTask<IReadOnlyList<AssetFacetGroup>> GetFacetsAsync(
            AssetQueryScope scope,
            AssetQueryCriteria criteria,
            DateTimeOffset snapshotAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AssetFacetGroup>>([]);

        public ValueTask<AssetUpdateStoreResult> UpdateAsync(
            AssetQueryScope scope,
            Guid assetId,
            long expectedVersion,
            string idempotencyKey,
            AssetMetadataPatch patch,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AssetUpdateStoreResult.NotFound());
    }
}
