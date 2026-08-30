namespace Vistara.Observability.Health;

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

    public SafeHealthEvaluator(IEnumerable<IHealthDependencyProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes
            .GroupBy(probe => probe.Dependency)
            .ToDictionary(group => group.Key, group => group.ToArray());
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

        var checks = new List<HealthCheckResult>(dependencies.Length);
        foreach (HealthDependency dependency in dependencies)
        {
            checks.Add(await CheckAsync(dependency, cancellationToken));
        }

        HealthState state = checks.All(check =>
            check.State == HealthState.Healthy)
            ? HealthState.Healthy
            : HealthState.Unhealthy;
        return new HealthReport(endpoint, state, checks);
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
            try
            {
                HealthProbeResult result =
                    await probe.CheckAsync(cancellationToken);
                if (result.State == HealthState.Unhealthy)
                {
                    failures.Add(result);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failures.Add(
                    HealthProbeResult.Unhealthy(
                        HealthReasonCodes.DependencyUnavailable));
            }
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
