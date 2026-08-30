using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vistara.Observability.Telemetry;

public enum TelemetryOperationKind
{
    Api,
    Storage,
    Database,
    Jobs,
    Imaging,
    Reconciliation,
    Authorization,
    Worker,
}

public enum TelemetryOutcome
{
    Success,
    Failure,
    Rejected,
    Cancelled,
}

public enum TelemetryCheckpointKind
{
    DerivativeOwnershipAcquired,
    DerivativeSourceVerified,
    DerivativeOutputTransformed,
    DerivativeOutputStaged,
    DerivativeDestinationPublished,
    DerivativeDestinationVisible,
    DerivativeReadyCommitted,
    DerivativeStagingDeleted,
    DerivativeCleanupCommitted,
    ReconciliationCandidateRevalidated,
    ReconciliationMultipartInspected,
    ReconciliationMultipartAborted,
    ReconciliationObjectInspected,
    ReconciliationQuarantined,
    ReconciliationSessionTransitioned,
    ReconciliationStagingDeleted,
    ReconciliationCursorSaved,
}

public static class TelemetryTagNames
{
    public const string Area = "vistara.area";
    public const string Operation = "vistara.operation";
    public const string Outcome = "vistara.outcome";
    public const string ReasonCode = "vistara.reason_code";
    public const string Checkpoint = "vistara.checkpoint";
}

public static class TelemetryReasonCodes
{
    public const string None = "none";
    public const string UnexpectedFailure = "unexpected_failure";

    private static readonly HashSet<string> Allowed =
    [
        None,
        UnexpectedFailure,
        "authentication_failed",
        "cancelled",
        "dead_lettered",
        "dependency_missing",
        "dependency_timeout",
        "dependency_unavailable",
        "configuration_invalid",
        "migration_required",
        "schema_incompatible",
        "storage_unavailable",
        "queue_unavailable",
        "imaging_unavailable",
        "handler_failure",
        "invalid_request",
        "lease_lost",
        "policy_denied",
        "rejected",
        "storage_failure",
        "database_failure",
        "imaging_failure",
        "reconciliation_failure",
    ];

    public static string Normalize(string? reasonCode) =>
        reasonCode is not null && Allowed.Contains(reasonCode)
            ? reasonCode
            : UnexpectedFailure;
}

public static class VistaraTelemetry
{
    public const string SourceName = "Vistara";
    public const string MeterName = "Vistara";

    internal static readonly ActivitySource Activities = new(SourceName);
    private static readonly Meter Metrics = new(MeterName);
    internal static readonly Counter<long> Operations =
        Metrics.CreateCounter<long>("vistara.operations");
    internal static readonly Histogram<double> Duration =
        Metrics.CreateHistogram<double>(
            "vistara.operation.duration",
            unit: "ms");
    internal static readonly Counter<long> Checkpoints =
        Metrics.CreateCounter<long>("vistara.checkpoints");

    public static TelemetryOperation Start(TelemetryOperationKind operation) =>
        new(operation);

    public static TelemetryLogStateCollection CreateLogState(
        TelemetryOperationKind operation,
        TelemetryOutcome outcome,
        string? reasonCode = null) =>
        TelemetryDimensions.CreateLogState(operation, outcome, reasonCode);

    public static void RecordCheckpoint(TelemetryCheckpointKind checkpoint)
    {
        (string area, string name) =
            TelemetryDimensions.CheckpointNames(checkpoint);
        Checkpoints.Add(
            1,
            new TagList
            {
                { TelemetryTagNames.Area, area },
                { TelemetryTagNames.Checkpoint, name },
            });
    }
}

public sealed class TelemetryOperation : IDisposable
{
    private readonly TelemetryOperationKind _operation;
    private readonly Activity? _activity;
    private readonly long _startedAt;
    private TelemetryOutcome _outcome = TelemetryOutcome.Success;
    private string _reasonCode = TelemetryReasonCodes.None;
    private bool _disposed;

    internal TelemetryOperation(TelemetryOperationKind operation)
    {
        Validate(operation);
        _operation = operation;
        _startedAt = Stopwatch.GetTimestamp();
        (string area, string name) = TelemetryDimensions.Names(operation);
        _activity = VistaraTelemetry.Activities.StartActivity(
            name,
            ActivityKind.Internal);
        _activity?.SetTag(TelemetryTagNames.Area, area);
        _activity?.SetTag(TelemetryTagNames.Operation, name);
    }

    public void Fail(string reasonCode)
    {
        _outcome = TelemetryOutcome.Failure;
        _reasonCode = TelemetryReasonCodes.Normalize(reasonCode);
    }

    public void Reject(string reasonCode)
    {
        _outcome = TelemetryOutcome.Rejected;
        _reasonCode = TelemetryReasonCodes.Normalize(reasonCode);
    }

    public void Cancel()
    {
        _outcome = TelemetryOutcome.Cancelled;
        _reasonCode = "cancelled";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TagList tags = TelemetryDimensions.CreateTags(
            _operation,
            _outcome,
            _reasonCode);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            _activity?.SetTag(tag.Key, tag.Value);
        }

        if (_outcome == TelemetryOutcome.Failure)
        {
            _activity?.SetStatus(ActivityStatusCode.Error);
        }

        _activity?.Stop();
        VistaraTelemetry.Operations.Add(1, tags);
        VistaraTelemetry.Duration.Record(
            Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds,
            tags);
    }

    private static void Validate(TelemetryOperationKind operation)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
}

public sealed class TelemetryLogStateCollection :
    IReadOnlyDictionary<string, object?>
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    internal TelemetryLogStateCollection(Dictionary<string, object?> values)
    {
        _values = values;
    }

    public object? this[string key] => _values[key];

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<object?> Values => _values.Values;

    public int Count => _values.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        _values.GetEnumerator();

    public bool TryGetValue(string key, out object? value) =>
        _values.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class TelemetryDimensions
{
    internal static TagList CreateTags(
        TelemetryOperationKind operation,
        TelemetryOutcome outcome,
        string? reasonCode)
    {
        (string area, string name) = Names(operation);
        return new TagList
        {
            { TelemetryTagNames.Area, area },
            { TelemetryTagNames.Operation, name },
            { TelemetryTagNames.Outcome, OutcomeName(outcome) },
            {
                TelemetryTagNames.ReasonCode,
                outcome == TelemetryOutcome.Success
                    ? TelemetryReasonCodes.None
                    : TelemetryReasonCodes.Normalize(reasonCode)
            },
        };
    }

    internal static TelemetryLogStateCollection CreateLogState(
        TelemetryOperationKind operation,
        TelemetryOutcome outcome,
        string? reasonCode)
    {
        TagList tags = CreateTags(operation, outcome, reasonCode);
        return new TelemetryLogStateCollection(
            tags.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    internal static (string Area, string Operation) Names(
        TelemetryOperationKind operation) =>
        operation switch
        {
            TelemetryOperationKind.Api => ("api", "api.request"),
            TelemetryOperationKind.Storage => ("storage", "storage.operation"),
            TelemetryOperationKind.Database => ("database", "database.operation"),
            TelemetryOperationKind.Jobs => ("jobs", "job.execution"),
            TelemetryOperationKind.Imaging => ("imaging", "image.operation"),
            TelemetryOperationKind.Reconciliation =>
                ("reconciliation", "reconciliation.run"),
            TelemetryOperationKind.Authorization =>
                ("authorization", "authorization.decision"),
            TelemetryOperationKind.Worker => ("worker", "worker.loop"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    internal static (string Area, string Checkpoint) CheckpointNames(
        TelemetryCheckpointKind checkpoint) =>
        checkpoint switch
        {
            TelemetryCheckpointKind.DerivativeOwnershipAcquired =>
                ("derivatives", "ownership_acquired"),
            TelemetryCheckpointKind.DerivativeSourceVerified =>
                ("derivatives", "source_verified"),
            TelemetryCheckpointKind.DerivativeOutputTransformed =>
                ("derivatives", "output_transformed"),
            TelemetryCheckpointKind.DerivativeOutputStaged =>
                ("derivatives", "output_staged"),
            TelemetryCheckpointKind.DerivativeDestinationPublished =>
                ("derivatives", "destination_published"),
            TelemetryCheckpointKind.DerivativeDestinationVisible =>
                ("derivatives", "destination_visible"),
            TelemetryCheckpointKind.DerivativeReadyCommitted =>
                ("derivatives", "ready_committed"),
            TelemetryCheckpointKind.DerivativeStagingDeleted =>
                ("derivatives", "staging_deleted"),
            TelemetryCheckpointKind.DerivativeCleanupCommitted =>
                ("derivatives", "cleanup_committed"),
            TelemetryCheckpointKind.ReconciliationCandidateRevalidated =>
                ("reconciliation", "candidate_revalidated"),
            TelemetryCheckpointKind.ReconciliationMultipartInspected =>
                ("reconciliation", "multipart_inspected"),
            TelemetryCheckpointKind.ReconciliationMultipartAborted =>
                ("reconciliation", "multipart_aborted"),
            TelemetryCheckpointKind.ReconciliationObjectInspected =>
                ("reconciliation", "object_inspected"),
            TelemetryCheckpointKind.ReconciliationQuarantined =>
                ("reconciliation", "quarantined"),
            TelemetryCheckpointKind.ReconciliationSessionTransitioned =>
                ("reconciliation", "session_transitioned"),
            TelemetryCheckpointKind.ReconciliationStagingDeleted =>
                ("reconciliation", "staging_deleted"),
            TelemetryCheckpointKind.ReconciliationCursorSaved =>
                ("reconciliation", "cursor_saved"),
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint)),
        };

    private static string OutcomeName(TelemetryOutcome outcome) =>
        outcome switch
        {
            TelemetryOutcome.Success => "success",
            TelemetryOutcome.Failure => "failure",
            TelemetryOutcome.Rejected => "rejected",
            TelemetryOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
