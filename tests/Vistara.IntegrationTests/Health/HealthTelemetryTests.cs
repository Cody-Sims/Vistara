using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Observability.Telemetry;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Health;
using Xunit;

namespace Vistara.IntegrationTests.Health;

public sealed class HealthTelemetryTests
{
    [Fact]
    public void Operations_emit_low_cardinality_trace_metric_and_log_dimensions()
    {
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VistaraTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity,
        };
        ActivitySource.AddActivityListener(listener);

        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == VistaraTelemetry.MeterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (_, _, tags, _) => measurements.Add(ToDictionary(tags)));
        meterListener.SetMeasurementEventCallback<double>(
            (_, _, tags, _) => measurements.Add(ToDictionary(tags)));
        meterListener.Start();

        using (TelemetryOperation operation =
               VistaraTelemetry.Start(TelemetryOperationKind.Storage))
        {
            operation.Fail("dependency_timeout");
        }

        Assert.NotNull(stopped);
        IReadOnlyDictionary<string, object?> traceTags =
            stopped.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value);
        AssertSafeDimensions(traceTags);
        Assert.NotEmpty(measurements);
        Assert.All(measurements, AssertSafeDimensions);

        TelemetryLogStateCollection log = VistaraTelemetry.CreateLogState(
            TelemetryOperationKind.Authorization,
            TelemetryOutcome.Rejected,
            "policy_denied");
        AssertSafeDimensions(log);
    }

    [Fact]
    public void Unstable_reason_codes_are_replaced_instead_of_becoming_dimensions()
    {
        string unstableDimension = $"tenant-{Guid.CreateVersion7():N}-asset-hash";

        TelemetryLogStateCollection log = VistaraTelemetry.CreateLogState(
            TelemetryOperationKind.Database,
            TelemetryOutcome.Failure,
            unstableDimension);

        Assert.Equal(
            TelemetryReasonCodes.UnexpectedFailure,
            log[TelemetryTagNames.ReasonCode]);
        Assert.DoesNotContain(unstableDimension, log.Values);
    }

    [Fact]
    public void Worker_job_observer_does_not_tag_job_identity_or_type()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VistaraTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var observer = new OpenTelemetryJobRuntimeObserver(
            NullLogger<OpenTelemetryJobRuntimeObserver>.Instance);
        var jobId = new JobId(Guid.CreateVersion7());
        const string jobType = "tenant-specific-secret-job";

        observer.Started(jobId, new JobType(jobType));
        observer.Failed(jobId, "database_failure", deadLettered: false);

        Activity activity = Assert.Single(stopped);
        string tags = string.Join(
            '|',
            activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(jobId.Value.ToString(), tags, StringComparison.Ordinal);
        Assert.DoesNotContain(jobType, tags, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checkpoints_do_not_emit_generic_operation_measurements()
    {
        var measurements = new List<(
            string Instrument,
            IReadOnlyDictionary<string, object?> Tags)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == VistaraTelemetry.MeterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
                measurements.Add((instrument.Name, ToDictionary(tags))));
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) =>
                measurements.Add((instrument.Name, ToDictionary(tags))));
        meterListener.Start();

        await new OpenTelemetryDerivativeCheckpointObserver().ReachedAsync(
            DerivativeCheckpoint.OutputTransformed,
            CancellationToken.None);
        await new OpenTelemetryUploadReconciliationCheckpointObserver()
            .ReachedAsync(
                ReconciliationCheckpoint.CursorSaved,
                CancellationToken.None);

        Assert.DoesNotContain(
            measurements,
            measurement =>
                measurement.Instrument == "vistara.operations" ||
                measurement.Instrument == "vistara.operation.duration");
        var checkpoints = measurements
            .Where(measurement =>
                measurement.Instrument == "vistara.checkpoints")
            .ToArray();
        Assert.Equal(2, checkpoints.Length);
        Assert.Equal(
            [
                "derivatives/output_transformed",
                "reconciliation/cursor_saved",
            ],
            checkpoints
                .Select(measurement =>
                    $"{measurement.Tags[TelemetryTagNames.Area]}/" +
                    $"{measurement.Tags["vistara.checkpoint"]}")
                .Order(StringComparer.Ordinal));
        Assert.All(
            checkpoints,
            measurement =>
            {
                Assert.Subset(
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        TelemetryTagNames.Area,
                        "vistara.checkpoint",
                    },
                    measurement.Tags.Keys.ToHashSet(StringComparer.Ordinal));
                Assert.DoesNotContain(
                    measurement.Tags.Values,
                    value => value?.ToString()?.Contains(
                        "tenant",
                        StringComparison.OrdinalIgnoreCase) == true);
            });
    }

    private static Dictionary<string, object?> ToDictionary(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            result.Add(tag.Key, tag.Value);
        }

        return result;
    }

    private static void AssertSafeDimensions(
        IReadOnlyDictionary<string, object?> dimensions)
    {
        Assert.All(
            dimensions.Keys,
            key =>
            {
                Assert.DoesNotContain("tenant", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("asset", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("hash", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("exception", key, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Subset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                TelemetryTagNames.Area,
                TelemetryTagNames.Operation,
                TelemetryTagNames.Outcome,
                TelemetryTagNames.ReasonCode,
            },
            dimensions.Keys.ToHashSet(StringComparer.Ordinal));
    }
}
