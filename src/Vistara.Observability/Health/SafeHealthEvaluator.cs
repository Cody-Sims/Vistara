namespace Vistara.Observability.Health;

/// <summary>
/// Bounds dependency health work. Probes run under a timeout so a hung
/// dependency cannot pin a request thread, and successive evaluations reuse a
/// recent report so request volume cannot translate into backend probe volume.
/// </summary>
public sealed record HealthEvaluationOptions
{
    public static HealthEvaluationOptions Unbounded { get; } = new()
    {
        ProbeTimeout = Timeout.InfiniteTimeSpan,
        CacheDuration = TimeSpan.Zero,
    };

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if ((ProbeTimeout <= TimeSpan.Zero &&
                ProbeTimeout != Timeout.InfiniteTimeSpan) ||
            ProbeTimeout > TimeSpan.FromMinutes(1) ||
            CacheDuration < TimeSpan.Zero ||
            CacheDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                "Health evaluation bounds are invalid.");
        }
    }
}

/// <summary>
/// Shares the most recent dependency report across concurrent requests so a
/// flood of probe traffic issues at most one round of backend work per cache
/// window.
/// </summary>
public sealed class HealthReportCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<HealthEndpointKind, Entry> _entries = [];

    public bool TryGet(
        HealthEndpointKind endpoint,
        DateTimeOffset nowUtc,
        TimeSpan cacheDuration,
        out HealthReport report)
    {
        if (cacheDuration <= TimeSpan.Zero)
        {
            report = null!;
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(endpoint, out Entry entry) &&
                nowUtc - entry.EvaluatedAtUtc < cacheDuration)
            {
                report = entry.Report;
                return true;
            }
        }

        report = null!;
        return false;
    }

    public void Set(
        HealthEndpointKind endpoint,
        HealthReport report,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _entries[endpoint] = new Entry(report, nowUtc);
        }
    }

    private readonly record struct Entry(
        HealthReport Report,
        DateTimeOffset EvaluatedAtUtc);
}

public sealed class SafeHealthEvaluator
{
    private static readonly Dictionary<
        HealthEndpointKind,
        HealthDependency[]> Required =
        new Dictionary<HealthEndpointKind, HealthDependency[]>
        {
            [HealthEndpointKind.Liveness] = [HealthDependency.Process],
            [HealthEndpointKind.Readiness] =
            [
                HealthDependency.Database,
                HealthDependency.Schema,
                HealthDependency.Storage,
                HealthDependency.Queue,
            ],
            [HealthEndpointKind.Startup] =
            [
                HealthDependency.Configuration,
                HealthDependency.Migrations,
                HealthDependency.Imaging,
            ],
        };

    private readonly Dictionary<
        HealthDependency,
        IHealthDependencyProbe[]> _probes;
    private readonly HealthEvaluationOptions _options;
    private readonly HealthReportCache? _cache;
    private readonly TimeProvider _timeProvider;

    public SafeHealthEvaluator(IEnumerable<IHealthDependencyProbe> probes)
        : this(probes, HealthEvaluationOptions.Unbounded, null, TimeProvider.System)
    {
    }

    public SafeHealthEvaluator(
        IEnumerable<IHealthDependencyProbe> probes,
        HealthEvaluationOptions options,
        HealthReportCache? cache,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _probes = probes
            .GroupBy(probe => probe.Dependency)
            .ToDictionary(group => group.Key, group => group.ToArray());
        _options = options;
        _cache = cache;
        _timeProvider = timeProvider;
    }

    public async ValueTask<HealthReport> EvaluateAsync(
        HealthEndpointKind endpoint,
        CancellationToken cancellationToken)
    {
        if (!Required.TryGetValue(endpoint, out HealthDependency[]? dependencies))
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint));
        }

        if (endpoint == HealthEndpointKind.Liveness)
        {
            return new HealthReport(
                endpoint,
                HealthState.Healthy,
                [
                    new HealthCheckResult(
                        HealthDependency.Process,
                        HealthState.Healthy,
                        HealthReasonCodes.Healthy),
                ]);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_cache is not null &&
            _cache.TryGet(
                endpoint,
                now,
                _options.CacheDuration,
                out HealthReport cached))
        {
            return cached;
        }

        var checks = new List<HealthCheckResult>(dependencies.Length);
        foreach (HealthDependency dependency in dependencies)
        {
            checks.Add(await CheckAsync(dependency, cancellationToken));
        }

        HealthState state = checks.All(check =>
            check.State == HealthState.Healthy)
            ? HealthState.Healthy
            : HealthState.Unhealthy;
        var report = new HealthReport(endpoint, state, checks);
        _cache?.Set(endpoint, report, now);
        return report;
    }

    private async ValueTask<HealthProbeResult?> RunProbeAsync(
        IHealthDependencyProbe probe,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource bounded = Bounded(cancellationToken);
        try
        {
            return await probe.CheckAsync(bounded.Token);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HealthProbeResult.Unhealthy(
                HealthReasonCodes.DependencyTimeout);
        }
        catch (Exception)
        {
            return HealthProbeResult.Unhealthy(
                HealthReasonCodes.DependencyUnavailable);
        }
    }

    private CancellationTokenSource Bounded(CancellationToken cancellationToken)
    {
        var linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ProbeTimeout != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(_options.ProbeTimeout);
        }

        return linked;
    }

    private async ValueTask<HealthCheckResult> CheckAsync(
        HealthDependency dependency,
        CancellationToken cancellationToken)
    {
        if (!_probes.TryGetValue(
                dependency,
                out IHealthDependencyProbe[]? probes) ||
            probes.Length == 0)
        {
            return new HealthCheckResult(
                dependency,
                HealthState.Unhealthy,
                HealthReasonCodes.DependencyMissing);
        }

        var failures = new List<HealthProbeResult>(probes.Length);
        foreach (IHealthDependencyProbe probe in probes)
        {
            failures.AddRange(
                await RunProbeAsync(probe, cancellationToken) is
                    { State: HealthState.Unhealthy } failure
                    ? [failure]
                    : Array.Empty<HealthProbeResult>());
        }

        if (failures.Count == 0)
        {
            return new HealthCheckResult(
                dependency,
                HealthState.Healthy,
                HealthReasonCodes.Healthy);
        }

        string reasonCode = failures
            .Select(failure => failure.ReasonCode)
            .OrderBy(
                static reason => reason == HealthReasonCodes.DependencyUnavailable
                    ? 1
                    : 0)
            .ThenBy(static reason => reason, StringComparer.Ordinal)
            .First();
        return new HealthCheckResult(
            dependency,
            HealthState.Unhealthy,
            reasonCode);
    }
}
