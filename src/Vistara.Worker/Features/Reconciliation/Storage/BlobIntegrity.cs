using System.Text.Json;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Jobs;
using Vistara.Observability.Telemetry;
using Vistara.Worker.Runtime.Jobs;
using Vistara.Worker.Runtime.Reconciliation;

namespace Vistara.Worker.Features.Reconciliation.Storage;

public sealed record BlobIntegrityOptions
{
    public int BatchSize { get; init; } = 200;

    public int MaximumStorageObjects { get; init; } = 1_000;

    /// <summary>
    /// A durable blob is only reported missing once storage has had time to
    /// become consistent after promotion.
    /// </summary>
    public TimeSpan MissingMinimumAge { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Unreferenced storage objects are only deletable once they are older
    /// than an in-flight upload could plausibly be.
    /// </summary>
    public TimeSpan OrphanMinimumAge { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Destructive orphan cleanup stays disabled until an operator has seen a
    /// dry-run report.
    /// </summary>
    public bool DeleteOrphans { get; init; }

    internal void Validate()
    {
        if (BatchSize is < 1 or > 1_000 ||
            MaximumStorageObjects is < 0 or > 100_000 ||
            MissingMinimumAge < TimeSpan.Zero ||
            OrphanMinimumAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Blob integrity reconciliation options are invalid.");
        }
    }
}

public sealed record BlobIntegrityRecord(
    Guid BlobId,
    string ObjectKey,
    DateTimeOffset CreatedAtUtc);

public sealed record BlobIntegrityPage(
    IReadOnlyList<BlobIntegrityRecord> Records,
    Guid? ContinuationCursor);

/// <summary>
/// Tenant-scoped durable state required to reconcile catalogued blobs against
/// object storage.
/// </summary>
public interface IBlobIntegrityStatePort
{
    ValueTask<BlobIntegrityPage> ScanActiveAsync(
        Guid tenantId,
        Guid? cursor,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transitions an active blob to <c>Missing</c>. Returns false when the
    /// row already moved on, keeping the sweep idempotent and fenced.
    /// </summary>
    ValueTask<bool> MarkMissingAsync(
        Guid tenantId,
        Guid blobId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyCollection<string>> FilterUnknownObjectKeysAsync(
        Guid tenantId,
        IReadOnlyCollection<string> objectKeys,
        CancellationToken cancellationToken);
}

public sealed record BlobIntegrityRequest(
    Guid TenantId,
    string? Cursor,
    bool DryRun);

public sealed record BlobIntegrityReport
{
    public int Scanned { get; init; }

    public int MissingDetected { get; init; }

    public int MissingRecorded { get; init; }

    public int OrphansDetected { get; init; }

    public int OrphansDeleted { get; init; }

    public string? ContinuationCursor { get; init; }
}

public sealed class BlobIntegrityService(
    IBlobIntegrityStatePort state,
    IBlobStore store,
    IClock clock,
    BlobIntegrityOptions options)
{
    private readonly IBlobIntegrityStatePort _state =
        state ?? throw new ArgumentNullException(nameof(state));
    private readonly IBlobStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly BlobIntegrityOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<BlobIntegrityReport> RunAsync(
        BlobIntegrityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _options.Validate();
        using TelemetryOperation operation =
            VistaraTelemetry.Start(TelemetryOperationKind.Reconciliation);
        try
        {
            DateTimeOffset now = _clock.UtcNow;
            (int scanned, int missingDetected, int missingRecorded, Guid? cursor) =
                await ReconcileMissingAsync(request, now, cancellationToken);
            (int orphansDetected, int orphansDeleted) =
                await ReconcileOrphansAsync(request, now, cancellationToken);
            return new BlobIntegrityReport
            {
                Scanned = scanned,
                MissingDetected = missingDetected,
                MissingRecorded = missingRecorded,
                OrphansDetected = orphansDetected,
                OrphansDeleted = orphansDeleted,
                ContinuationCursor = cursor?.ToString(),
            };
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception)
        {
            operation.Fail("reconciliation_failure");
            throw;
        }
    }

    private async ValueTask<(int Scanned, int Detected, int Recorded, Guid? Cursor)>
        ReconcileMissingAsync(
            BlobIntegrityRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        Guid? cursor = Guid.TryParse(request.Cursor, out Guid parsed)
            ? parsed
            : null;
        BlobIntegrityPage page = await _state.ScanActiveAsync(
            request.TenantId,
            cursor,
            _options.BatchSize,
            cancellationToken);
        int detected = 0;
        int recorded = 0;
        foreach (BlobIntegrityRecord record in page.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (now - record.CreatedAtUtc < _options.MissingMinimumAge)
            {
                continue;
            }

            BlobHead? head = await _store.HeadAsync(
                new BlobKey(record.ObjectKey),
                cancellationToken);
            if (head is not null)
            {
                continue;
            }

            detected++;
            VistaraTelemetry.RecordCheckpoint(
                TelemetryCheckpointKind.ReconciliationObjectInspected);
            if (request.DryRun)
            {
                continue;
            }

            if (await _state.MarkMissingAsync(
                    request.TenantId,
                    record.BlobId,
                    cancellationToken))
            {
                recorded++;
                VistaraTelemetry.RecordCheckpoint(
                    TelemetryCheckpointKind.ReconciliationQuarantined);
            }
        }

        return (page.Records.Count, detected, recorded, page.ContinuationCursor);
    }

    private async ValueTask<(int Detected, int Deleted)> ReconcileOrphansAsync(
        BlobIntegrityRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_options.MaximumStorageObjects == 0)
        {
            return (0, 0);
        }

        Dictionary<string, BlobHead> candidates = new(StringComparer.Ordinal);
        foreach (string prefix in TenantBlobNamespaces.For(request.TenantId))
        {
            if (candidates.Count >= _options.MaximumStorageObjects)
            {
                break;
            }

            await foreach (BlobHead head in _store.ListAsync(
                               new BlobListOptions(prefix),
                               cancellationToken))
            {
                string objectKey = head.Identity.Key.Value;
                if (!TenantBlobNamespaces.Contains(request.TenantId, objectKey))
                {
                    continue;
                }

                if (now - head.Properties.LastModifiedUtc <
                    _options.OrphanMinimumAge)
                {
                    continue;
                }

                candidates[objectKey] = head;
                if (candidates.Count >= _options.MaximumStorageObjects)
                {
                    break;
                }
            }
        }

        if (candidates.Count == 0)
        {
            return (0, 0);
        }

        IReadOnlyCollection<string> unknown =
            await _state.FilterUnknownObjectKeysAsync(
                request.TenantId,
                candidates.Keys,
                cancellationToken);
        int deleted = 0;
        foreach (string objectKey in unknown)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TenantBlobNamespaces.Contains(request.TenantId, objectKey))
            {
                throw new InvalidOperationException(
                    "Reconciliation produced an object outside the tenant namespace.");
            }

            VistaraTelemetry.RecordCheckpoint(
                TelemetryCheckpointKind.ReconciliationObjectInspected);
            if (request.DryRun || !_options.DeleteOrphans)
            {
                continue;
            }

            var key = new BlobKey(objectKey);
            BlobHead? current = await _store.HeadAsync(key, cancellationToken);
            if (current is null ||
                current.Identity.Version.Value !=
                    candidates[objectKey].Identity.Version.Value ||
                now - current.Properties.LastModifiedUtc <
                    _options.OrphanMinimumAge)
            {
                continue;
            }

            BlobDeleteResult result = await _store.DeleteAsync(
                key,
                BlobDeleteOptions.None,
                cancellationToken);
            if (result.Deleted)
            {
                deleted++;
                VistaraTelemetry.RecordCheckpoint(
                    TelemetryCheckpointKind.ReconciliationStagingDeleted);
            }
        }

        return (unknown.Count, deleted);
    }

}

public sealed class BlobIntegrityJobHandler(BlobIntegrityService service)
    : IJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly BlobIntegrityService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType { get; } = new("storage.reconcile");

    public JobType JobType => SupportedJobType;

    public async ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.PayloadVersion != 1 ||
            job.Type.Value != SupportedJobType.Value ||
            !TryReadPayload(job.Payload, out ReconciliationSchedulePayload? payload))
        {
            return JobHandlerResult.Failed(
                new JobFailure(JobFailureReason.ProcessingFailed));
        }

        _ = await _service.RunAsync(
            new BlobIntegrityRequest(
                job.TenantId.Value,
                payload!.Cursor,
                payload.DryRun),
            cancellationToken);
        return JobHandlerResult.Success();
    }

    private static bool TryReadPayload(
        string json,
        out ReconciliationSchedulePayload? payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<ReconciliationSchedulePayload>(
                json,
                JsonOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            payload = null;
            return false;
        }
    }
}
