using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Observability.Health;
using Vistara.Observability.Telemetry;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Worker.Composition.Media;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Health;

public static class WorkerHealthServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraWorkerHealth(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(Probe<WorkerConfigurationHealthProbe>());
        services.TryAddEnumerable(Probe<WorkerMigrationHealthProbe>());
        services.TryAddEnumerable(Probe<WorkerImagingHealthProbe>());
        services.TryAddEnumerable(Probe<WorkerDatabaseHealthProbe>());
        services.TryAddEnumerable(Probe<WorkerSchemaHealthProbe>());
        services.TryAddEnumerable(Probe<WorkerStorageHealthProbe>());
        services.TryAddEnumerable(Probe<WorkerQueueHealthProbe>());
        services.TryAddSingleton(new HealthEvaluationOptions());
        services.TryAddSingleton<HealthReportCache>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped(static provider => new SafeHealthEvaluator(
            provider.GetServices<IHealthDependencyProbe>(),
            provider.GetRequiredService<HealthEvaluationOptions>(),
            provider.GetRequiredService<HealthReportCache>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<WorkerHealthService>();
        bool hasCustomJobObserver = services.Any(descriptor =>
            descriptor.ServiceType == typeof(IJobRuntimeObserver) &&
            descriptor.ImplementationType != typeof(NullJobRuntimeObserver) &&
            descriptor.ImplementationInstance is not NullJobRuntimeObserver);
        if (!hasCustomJobObserver)
        {
            services.RemoveAll<IJobRuntimeObserver>();
            services.AddSingleton<
                IJobRuntimeObserver,
                OpenTelemetryJobRuntimeObserver>();
        }

        services.TryAddSingleton<
            IDerivativeCheckpointObserver,
            OpenTelemetryDerivativeCheckpointObserver>();
        services.TryAddSingleton<
            IUploadReconciliationObserver,
            OpenTelemetryUploadReconciliationObserver>();
        services.TryAddSingleton<
            IUploadReconciliationCheckpointObserver,
            OpenTelemetryUploadReconciliationCheckpointObserver>();
        return services;
    }

    private static ServiceDescriptor Probe<TProbe>()
        where TProbe : class, IHealthDependencyProbe =>
        ServiceDescriptor.Scoped<IHealthDependencyProbe, TProbe>();
}

public sealed class WorkerHealthService(SafeHealthEvaluator evaluator)
{
    private readonly SafeHealthEvaluator _evaluator =
        evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public ValueTask<HealthReport> CheckAsync(
        HealthEndpointKind endpoint,
        CancellationToken cancellationToken) =>
        _evaluator.EvaluateAsync(endpoint, cancellationToken);
}

public sealed class OpenTelemetryJobRuntimeObserver(
    ILogger<OpenTelemetryJobRuntimeObserver> logger) :
    IJobRuntimeObserver,
    IDisposable
{
    private static readonly Action<ILogger, string, string, Exception?> LogEvent =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "JobRuntime"),
            "Job runtime event {EventName} completed with reason {ReasonCode}.");

    private readonly ConcurrentDictionary<JobId, TelemetryOperation> _operations =
        new();
    private readonly ILogger _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public void Claimed(int count)
    {
        if (count > 0)
        {
            Record("claimed", TelemetryOutcome.Success, null);
        }
    }

    public void Started(JobId jobId, JobType jobType)
    {
        _ = jobType;
        TelemetryOperation operation =
            VistaraTelemetry.Start(TelemetryOperationKind.Jobs);
        if (!_operations.TryAdd(jobId, operation))
        {
            operation.Dispose();
        }
    }

    public void Heartbeat(JobId jobId)
    {
        _ = jobId;
        Record("heartbeat", TelemetryOutcome.Success, null);
    }

    public void Completed(JobId jobId)
    {
        if (_operations.TryRemove(jobId, out TelemetryOperation? operation))
        {
            operation.Dispose();
        }

        Log("completed", TelemetryOutcome.Success, null);
    }

    public void Failed(JobId jobId, string failureCode, bool deadLettered)
    {
        string reasonCode = deadLettered
            ? "dead_lettered"
            : TelemetryReasonCodes.Normalize(failureCode);
        if (_operations.TryRemove(jobId, out TelemetryOperation? operation))
        {
            operation.Fail(reasonCode);
            operation.Dispose();
        }
        else
        {
            Record("failed", TelemetryOutcome.Failure, reasonCode);
            return;
        }

        Log("failed", TelemetryOutcome.Failure, reasonCode);
    }

    public void LeaseLost(JobId jobId, string errorCode)
    {
        _ = errorCode;
        if (_operations.TryRemove(jobId, out TelemetryOperation? operation))
        {
            operation.Fail("lease_lost");
            operation.Dispose();
        }
        else
        {
            Record("lease_lost", TelemetryOutcome.Failure, "lease_lost");
            return;
        }

        Log("lease_lost", TelemetryOutcome.Failure, "lease_lost");
    }

    public void Dispose()
    {
        foreach (KeyValuePair<JobId, TelemetryOperation> pair in _operations)
        {
            if (_operations.TryRemove(pair.Key, out TelemetryOperation? operation))
            {
                operation.Cancel();
                operation.Dispose();
            }
        }
    }

    private void Record(
        string eventName,
        TelemetryOutcome outcome,
        string? reasonCode)
    {
        using TelemetryOperation operation =
            VistaraTelemetry.Start(TelemetryOperationKind.Worker);
        if (outcome == TelemetryOutcome.Failure)
        {
            operation.Fail(reasonCode ?? TelemetryReasonCodes.UnexpectedFailure);
        }

        Log(eventName, outcome, reasonCode);
    }

    private void Log(
        string eventName,
        TelemetryOutcome outcome,
        string? reasonCode)
    {
        TelemetryLogStateCollection state = VistaraTelemetry.CreateLogState(
            TelemetryOperationKind.Jobs,
            outcome,
            reasonCode);
        using IDisposable? scope = _logger.BeginScope(state);
        LogEvent(
            _logger,
            eventName,
            state[TelemetryTagNames.ReasonCode]?.ToString() ??
                TelemetryReasonCodes.UnexpectedFailure,
            null);
    }
}

public sealed class OpenTelemetryDerivativeCheckpointObserver
    : IDerivativeCheckpointObserver
{
    public ValueTask ReachedAsync(
        DerivativeCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TelemetryCheckpointKind telemetryCheckpoint = checkpoint switch
        {
            DerivativeCheckpoint.OwnershipAcquired =>
                TelemetryCheckpointKind.DerivativeOwnershipAcquired,
            DerivativeCheckpoint.SourceVerified =>
                TelemetryCheckpointKind.DerivativeSourceVerified,
            DerivativeCheckpoint.OutputTransformed =>
                TelemetryCheckpointKind.DerivativeOutputTransformed,
            DerivativeCheckpoint.OutputStaged =>
                TelemetryCheckpointKind.DerivativeOutputStaged,
            DerivativeCheckpoint.DestinationPublished =>
                TelemetryCheckpointKind.DerivativeDestinationPublished,
            DerivativeCheckpoint.DestinationVisible =>
                TelemetryCheckpointKind.DerivativeDestinationVisible,
            DerivativeCheckpoint.ReadyCommitted =>
                TelemetryCheckpointKind.DerivativeReadyCommitted,
            DerivativeCheckpoint.StagingDeleted =>
                TelemetryCheckpointKind.DerivativeStagingDeleted,
            DerivativeCheckpoint.CleanupCommitted =>
                TelemetryCheckpointKind.DerivativeCleanupCommitted,
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint)),
        };
        VistaraTelemetry.RecordCheckpoint(telemetryCheckpoint);
        return ValueTask.CompletedTask;
    }
}

public sealed class OpenTelemetryUploadReconciliationObserver
    : IUploadReconciliationObserver
{
    public void Record(
        ReconciliationActionKind action,
        ReconciliationActionOutcome outcome)
    {
        _ = action;
        using TelemetryOperation operation =
            VistaraTelemetry.Start(TelemetryOperationKind.Reconciliation);
        switch (outcome)
        {
            case ReconciliationActionOutcome.Refused:
                operation.Reject("rejected");
                break;
            case ReconciliationActionOutcome.Deferred:
                operation.Fail("dependency_unavailable");
                break;
            case ReconciliationActionOutcome.Stale:
                operation.Fail("reconciliation_failure");
                break;
        }
    }
}

public sealed class OpenTelemetryUploadReconciliationCheckpointObserver
    : IUploadReconciliationCheckpointObserver
{
    public ValueTask ReachedAsync(
        ReconciliationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TelemetryCheckpointKind telemetryCheckpoint = checkpoint switch
        {
            ReconciliationCheckpoint.CandidateRevalidated =>
                TelemetryCheckpointKind.ReconciliationCandidateRevalidated,
            ReconciliationCheckpoint.MultipartInspected =>
                TelemetryCheckpointKind.ReconciliationMultipartInspected,
            ReconciliationCheckpoint.MultipartAborted =>
                TelemetryCheckpointKind.ReconciliationMultipartAborted,
            ReconciliationCheckpoint.ObjectInspected =>
                TelemetryCheckpointKind.ReconciliationObjectInspected,
            ReconciliationCheckpoint.Quarantined =>
                TelemetryCheckpointKind.ReconciliationQuarantined,
            ReconciliationCheckpoint.SessionTransitioned =>
                TelemetryCheckpointKind.ReconciliationSessionTransitioned,
            ReconciliationCheckpoint.StagingDeleted =>
                TelemetryCheckpointKind.ReconciliationStagingDeleted,
            ReconciliationCheckpoint.CursorSaved =>
                TelemetryCheckpointKind.ReconciliationCursorSaved,
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint)),
        };
        VistaraTelemetry.RecordCheckpoint(telemetryCheckpoint);
        return ValueTask.CompletedTask;
    }
}

internal abstract class WorkerHealthProbe(
    IServiceProvider services,
    HealthDependency dependency,
    string failureReason) : IHealthDependencyProbe
{
    private readonly IServiceProvider _services =
        services ?? throw new ArgumentNullException(nameof(services));
    private readonly string _failureReason = failureReason;

    public HealthDependency Dependency { get; } = dependency;

    public async ValueTask<HealthProbeResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        using TelemetryOperation operation =
            VistaraTelemetry.Start(OperationFor(Dependency));
        try
        {
            await CheckCoreAsync(_services, cancellationToken);
            return HealthProbeResult.Healthy();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception)
        {
            operation.Fail(TelemetryReasonCodes.Normalize(_failureReason));
            return HealthProbeResult.Unhealthy(_failureReason);
        }
    }

    protected abstract ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken);

    private static TelemetryOperationKind OperationFor(
        HealthDependency dependency) =>
        dependency switch
        {
            HealthDependency.Storage => TelemetryOperationKind.Storage,
            HealthDependency.Imaging => TelemetryOperationKind.Imaging,
            HealthDependency.Configuration => TelemetryOperationKind.Worker,
            HealthDependency.Queue => TelemetryOperationKind.Jobs,
            _ => TelemetryOperationKind.Database,
        };

    protected static async ValueTask ExecuteSchemaQueryAsync(
        DbContext context,
        string commandText,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool close = connection.State != ConnectionState.Open;
        if (close)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            _ = await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            if (close)
            {
                await connection.CloseAsync();
            }
        }
    }
}

internal sealed class WorkerConfigurationHealthProbe(IServiceProvider services)
    : WorkerHealthProbe(
        services,
        HealthDependency.Configuration,
        HealthReasonCodes.ConfigurationInvalid)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = services.GetRequiredService<IOptions<WorkerPlatformOptions>>().Value;
        _ = services.GetRequiredService<IOptions<MediaOptions>>().Value;
        _ = services.GetRequiredService<VistaraPersistenceOptions>();
        return ValueTask.CompletedTask;
    }
}

internal sealed class WorkerMigrationHealthProbe(IServiceProvider services)
    : WorkerHealthProbe(
        services,
        HealthDependency.Migrations,
        HealthReasonCodes.MigrationRequired)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteSchemaQueryAsync(
            services.GetRequiredService<VistaraDbContext>(),
            """SELECT "routed_tenant_id" FROM "worker_tenant_catalog" WHERE 1 = 0""",
            cancellationToken);
}

internal sealed class WorkerImagingHealthProbe(IServiceProvider services)
    : WorkerHealthProbe(
        services,
        HealthDependency.Imaging,
        HealthReasonCodes.ImagingUnavailable)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IImageProcessor processor =
            services.GetRequiredService<IImageProcessor>();
        _ = processor.Capabilities;
        _ = processor.PipelineFingerprint;
        return ValueTask.CompletedTask;
    }
}

internal sealed class WorkerDatabaseHealthProbe(IServiceProvider services)
    : WorkerHealthProbe(
        services,
        HealthDependency.Database,
        HealthReasonCodes.DependencyUnavailable)
{
    protected override async ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        bool canConnect = await services
            .GetRequiredService<VistaraDbContext>()
            .Database
            .CanConnectAsync(cancellationToken);
        if (!canConnect)
        {
            throw new InvalidOperationException();
        }
    }
}

internal sealed class WorkerSchemaHealthProbe(IServiceProvider services)
    : WorkerHealthProbe(
        services,
        HealthDependency.Schema,
        HealthReasonCodes.SchemaIncompatible)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteSchemaQueryAsync(
            services.GetRequiredService<VistaraDbContext>(),
            """SELECT "id", "tenant_id" FROM "tenants" WHERE 1 = 0""",
            cancellationToken);
}

internal sealed class WorkerStorageHealthProbe(IServiceProvider services)
    : WorkerHealthProbe(
        services,
        HealthDependency.Storage,
        HealthReasonCodes.StorageUnavailable)
{
    private static readonly BlobListOptions Sentinel = new("health/");

    protected override async ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        IBlobStore store = services.GetRequiredService<IBlobStore>();
        _ = store.Capabilities;
        await foreach (BlobHead head in store.ListAsync(
                           Sentinel,
                           cancellationToken))
        {
            _ = head;
            break;
        }
    }
}

internal sealed class WorkerQueueHealthProbe(IServiceProvider services)
    : WorkerHealthProbe(
        services,
        HealthDependency.Queue,
        HealthReasonCodes.QueueUnavailable)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteSchemaQueryAsync(
            services.GetRequiredService<JobDbContext>(),
            """SELECT "id", "state" FROM "jobs" WHERE 1 = 0""",
            cancellationToken);
}
