using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Common;
using Vistara.Observability.Health;
using Vistara.Observability.Telemetry;
using Vistara.Worker.Health;

namespace Vistara.Worker.Composition.Runtime;

public sealed class WorkerRuntimeHealthOptions
{
    public const string SectionName = "Worker:Health";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (Interval <= TimeSpan.Zero || Interval > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                "Worker health monitoring interval must be between zero and one hour.");
        }
    }
}

public sealed record WorkerHealthSnapshot(
    HealthReport Startup,
    HealthReport Readiness,
    DateTimeOffset EvaluatedAtUtc)
{
    public HealthState State =>
        Startup.State == HealthState.Healthy &&
        Readiness.State == HealthState.Healthy
            ? HealthState.Healthy
            : HealthState.Unhealthy;
}

/// <summary>
/// Publishes the most recent worker health evaluation so operators and
/// container probes can observe a role that exposes no HTTP surface.
/// </summary>
public interface IWorkerHealthState
{
    WorkerHealthSnapshot? Current { get; }
}

public static class WorkerRuntimeServiceCollectionExtensions
{
    public const string ServiceName = "vistara-worker";

    /// <summary>
    /// Registers worker health probes, the background health monitor, and the
    /// OpenTelemetry runtime.
    /// </summary>
    public static IServiceCollection AddVistaraWorkerRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new WorkerRuntimeHealthOptions();
        configuration.GetSection(WorkerRuntimeHealthOptions.SectionName)
            .Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.AddVistaraWorkerHealth();
        services.AddVistaraTelemetry(configuration, ServiceName);
        services.TryAddSingleton<WorkerHealthMonitor>();
        services.TryAddSingleton<IWorkerHealthState>(
            static provider => provider.GetRequiredService<WorkerHealthMonitor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, WorkerHealthMonitorHostedService>());
        return services;
    }
}

public sealed class WorkerHealthMonitor : IWorkerHealthState
{
    private static readonly Action<ILogger, string, string, Exception?> LogHealth =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "WorkerHealth"),
            "Worker health evaluated as {State} with reason {ReasonCode}.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly WorkerRuntimeHealthOptions _options;
    private readonly ILogger _logger;
    private WorkerHealthSnapshot? _current;

    public WorkerHealthMonitor(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        WorkerRuntimeHealthOptions options,
        ILogger<WorkerHealthMonitor> logger)
    {
        _scopeFactory = scopeFactory ??
            throw new ArgumentNullException(nameof(scopeFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkerHealthSnapshot? Current => Volatile.Read(ref _current);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await EvaluateAsync(stoppingToken);
                await Task.Delay(_options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async ValueTask<WorkerHealthSnapshot> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        using TelemetryOperation operation =
            VistaraTelemetry.Start(TelemetryOperationKind.Worker);
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        WorkerHealthService health =
            scope.ServiceProvider.GetRequiredService<WorkerHealthService>();
        HealthReport startup = await health.CheckAsync(
            HealthEndpointKind.Startup,
            cancellationToken);
        HealthReport readiness = await health.CheckAsync(
            HealthEndpointKind.Readiness,
            cancellationToken);
        var snapshot = new WorkerHealthSnapshot(
            startup,
            readiness,
            _clock.UtcNow);
        Volatile.Write(ref _current, snapshot);

        string reasonCode = FirstUnhealthyReason(snapshot);
        if (snapshot.State == HealthState.Unhealthy)
        {
            operation.Fail(TelemetryReasonCodes.Normalize(reasonCode));
        }

        TelemetryLogStateCollection state = VistaraTelemetry.CreateLogState(
            TelemetryOperationKind.Worker,
            snapshot.State == HealthState.Healthy
                ? TelemetryOutcome.Success
                : TelemetryOutcome.Failure,
            reasonCode);
        using IDisposable? logScope = _logger.BeginScope(state);
        LogHealth(
            _logger,
            snapshot.State == HealthState.Healthy ? "healthy" : "unhealthy",
            reasonCode,
            null);
        return snapshot;
    }

    private static string FirstUnhealthyReason(WorkerHealthSnapshot snapshot) =>
        snapshot.Startup.Checks
            .Concat(snapshot.Readiness.Checks)
            .Where(check => check.State == HealthState.Unhealthy)
            .Select(check => check.ReasonCode)
            .FirstOrDefault() ?? HealthReasonCodes.Healthy;
}

internal sealed class WorkerHealthMonitorHostedService(
    WorkerHealthMonitor monitor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        monitor.RunAsync(stoppingToken);
}
