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
            probes.Length != 1)
        {
            return new HealthCheckResult(
                dependency,
                HealthState.Unhealthy,
                HealthReasonCodes.DependencyMissing);
        }

        try
        {
            HealthProbeResult result =
                await probes[0].CheckAsync(cancellationToken);
            return new HealthCheckResult(
                dependency,
                result.State,
                result.ReasonCode);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new HealthCheckResult(
                dependency,
                HealthState.Unhealthy,
                HealthReasonCodes.DependencyUnavailable);
        }
    }
}
