using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Worker.Features.Reconciliation.Storage;
using Xunit;

namespace Vistara.IntegrationTests.Reconciliation;

public sealed class BlobIntegrityServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dry_run_reports_missing_blobs_without_changing_state()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId,
        [
            Record(Original(tenantId, "one"), Now.AddDays(-1)),
        ]);
        var service = new BlobIntegrityService(
            state,
            new ReconciliationBlobStore(),
            new FixedClock(Now),
            new BlobIntegrityOptions());

        BlobIntegrityReport report = await service.RunAsync(
            new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: true),
            CancellationToken.None);

        Assert.Equal(1, report.MissingDetected);
        Assert.Equal(0, report.MissingRecorded);
        Assert.Empty(state.Missing);
    }

    [Fact]
    public async Task Repair_marks_missing_blobs_once_and_stays_idempotent()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId,
        [
            Record(Original(tenantId, "one"), Now.AddDays(-1)),
        ]);
        var service = new BlobIntegrityService(
            state,
            new ReconciliationBlobStore(),
            new FixedClock(Now),
            new BlobIntegrityOptions());
        var request = new BlobIntegrityRequest(
            tenantId,
            Cursor: null,
            DryRun: false);

        BlobIntegrityReport first =
            await service.RunAsync(request, CancellationToken.None);
        BlobIntegrityReport second =
            await service.RunAsync(request, CancellationToken.None);

        Assert.Equal(1, first.MissingRecorded);
        Assert.Equal(0, second.MissingRecorded);
        Assert.Equal([Original(tenantId, "one")], state.Missing);
    }

    [Fact]
    public async Task Blobs_younger_than_the_threshold_are_never_reported_missing()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId,
        [
            Record(Original(tenantId, "fresh"), Now.AddMinutes(-1)),
        ]);
        var service = new BlobIntegrityService(
            state,
            new ReconciliationBlobStore(),
            new FixedClock(Now),
            new BlobIntegrityOptions());

        BlobIntegrityReport report = await service.RunAsync(
            new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: false),
            CancellationToken.None);

        Assert.Equal(0, report.MissingDetected);
        Assert.Empty(state.Missing);
    }

    [Fact]
    public async Task Present_blobs_are_left_alone()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId,
        [
            Record(Original(tenantId, "present"), Now.AddDays(-1)),
        ]);
        var store = new ReconciliationBlobStore();
        store.Add(Original(tenantId, "present"), Now.AddDays(-1));
        var service = new BlobIntegrityService(
            state,
            store,
            new FixedClock(Now),
            new BlobIntegrityOptions());

        BlobIntegrityReport report = await service.RunAsync(
            new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: false),
            CancellationToken.None);

        Assert.Equal(0, report.MissingDetected);
        Assert.Equal(0, report.OrphansDetected);
    }

    [Fact]
    public async Task Orphans_are_reported_before_deletion_is_enabled()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId, []);
        var store = new ReconciliationBlobStore();
        store.Add(Original(tenantId, "orphan"), Now.AddDays(-3));
        var service = new BlobIntegrityService(
            state,
            store,
            new FixedClock(Now),
            new BlobIntegrityOptions());

        BlobIntegrityReport report = await service.RunAsync(
            new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: false),
            CancellationToken.None);

        Assert.Equal(1, report.OrphansDetected);
        Assert.Equal(0, report.OrphansDeleted);
        Assert.Contains(Original(tenantId, "orphan"), store.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Enabled_orphan_cleanup_only_deletes_aged_unreferenced_objects()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId,
        [
            Record(Original(tenantId, "known"), Now.AddDays(-3)),
        ]);
        var store = new ReconciliationBlobStore();
        store.Add(Original(tenantId, "known"), Now.AddDays(-3));
        store.Add(Original(tenantId, "orphan"), Now.AddDays(-3));
        store.Add(Original(tenantId, "recent-orphan"), Now.AddHours(-1));
        var service = new BlobIntegrityService(
            state,
            store,
            new FixedClock(Now),
            new BlobIntegrityOptions { DeleteOrphans = true });

        BlobIntegrityReport report = await service.RunAsync(
            new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: false),
            CancellationToken.None);

        Assert.Equal(1, report.OrphansDetected);
        Assert.Equal(1, report.OrphansDeleted);
        Assert.DoesNotContain(Original(tenantId, "orphan"), store.Keys, StringComparer.Ordinal);
        Assert.Contains(Original(tenantId, "known"), store.Keys, StringComparer.Ordinal);
        Assert.Contains(
            Original(tenantId, "recent-orphan"),
            store.Keys,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Dry_run_never_deletes_even_when_cleanup_is_enabled()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId, []);
        var store = new ReconciliationBlobStore();
        store.Add(Original(tenantId, "orphan"), Now.AddDays(-3));
        var service = new BlobIntegrityService(
            state,
            store,
            new FixedClock(Now),
            new BlobIntegrityOptions { DeleteOrphans = true });

        BlobIntegrityReport report = await service.RunAsync(
            new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: true),
            CancellationToken.None);

        Assert.Equal(1, report.OrphansDetected);
        Assert.Equal(0, report.OrphansDeleted);
        Assert.Contains(Original(tenantId, "orphan"), store.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Scanning_only_observes_the_requested_tenant()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId,
        [
            Record(Original(tenantId, "one"), Now.AddDays(-1)),
        ]);
        var service = new BlobIntegrityService(
            state,
            new ReconciliationBlobStore(),
            new FixedClock(Now),
            new BlobIntegrityOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.RunAsync(
                new BlobIntegrityRequest(
                    Guid.CreateVersion7(),
                    Cursor: null,
                    DryRun: true),
                CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_stops_the_sweep()
    {
        Guid tenantId = Guid.CreateVersion7();
        var state = new FakeState(tenantId,
        [
            Record(Original(tenantId, "one"), Now.AddDays(-1)),
        ]);
        var service = new BlobIntegrityService(
            state,
            new ReconciliationBlobStore(),
            new FixedClock(Now),
            new BlobIntegrityOptions());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.RunAsync(
                new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: false),
                cancelled.Token));
    }

    [Fact]
    public async Task Full_pages_return_a_continuation_cursor()
    {
        Guid tenantId = Guid.CreateVersion7();
        BlobIntegrityRecord[] records =
        [
            Record(Original(tenantId, "one"), Now.AddDays(-1)),
            Record(Original(tenantId, "two"), Now.AddDays(-1)),
        ];
        var state = new FakeState(tenantId, records);
        var service = new BlobIntegrityService(
            state,
            new ReconciliationBlobStore(),
            new FixedClock(Now),
            new BlobIntegrityOptions { BatchSize = 2 });

        BlobIntegrityReport report = await service.RunAsync(
            new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: true),
            CancellationToken.None);

        Assert.Equal(2, report.Scanned);
        Assert.Equal(
            records[^1].BlobId.ToString(),
            report.ContinuationCursor);
    }

    private static string Original(Guid tenantId, string leaf) =>
        $"originals/{tenantId.ToString("N")[..2]}/{tenantId:D}/{leaf}/1/{leaf}.jpg";

    private static BlobIntegrityRecord Record(
        string objectKey,
        DateTimeOffset createdAtUtc) =>
        new(Guid.CreateVersion7(), objectKey, createdAtUtc);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeState(
        Guid tenantId,
        BlobIntegrityRecord[] records) : IBlobIntegrityStatePort
    {
        private readonly List<BlobIntegrityRecord> _records = [.. records];

        internal List<string> Missing { get; } = [];

        public ValueTask<BlobIntegrityPage> ScanActiveAsync(
            Guid requestedTenantId,
            Guid? cursor,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureTenant(requestedTenantId);
            BlobIntegrityRecord[] page =
            [
                .. _records
                    .Where(record => cursor is null || record.BlobId > cursor)
                    .Take(batchSize),
            ];
            return ValueTask.FromResult(
                new BlobIntegrityPage(
                    page,
                    page.Length == batchSize && page.Length > 0
                        ? page[^1].BlobId
                        : null));
        }

        public ValueTask<bool> MarkMissingAsync(
            Guid requestedTenantId,
            Guid blobId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureTenant(requestedTenantId);
            BlobIntegrityRecord? record =
                _records.SingleOrDefault(candidate => candidate.BlobId == blobId);
            if (record is null)
            {
                return ValueTask.FromResult(false);
            }

            _records.Remove(record);
            Missing.Add(record.ObjectKey);
            return ValueTask.FromResult(true);
        }

        public ValueTask<IReadOnlyCollection<string>>
            FilterUnknownObjectKeysAsync(
                Guid requestedTenantId,
                IReadOnlyCollection<string> objectKeys,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureTenant(requestedTenantId);
            HashSet<string> known = new(
                _records.Select(record => record.ObjectKey),
                StringComparer.Ordinal);
            IReadOnlyCollection<string> unknown =
            [
                .. objectKeys.Where(key => !known.Contains(key)),
            ];
            return ValueTask.FromResult(unknown);
        }

        private void EnsureTenant(Guid requestedTenantId)
        {
            if (requestedTenantId != tenantId)
            {
                throw new InvalidOperationException(
                    "Reconciliation crossed a tenant boundary.");
            }
        }
    }
}
