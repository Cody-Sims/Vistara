using System.Text.Json;

namespace Vistara.Observability.Health;

public enum HealthEndpointKind
{
    Liveness,
    Readiness,
    Startup,
}

public enum HealthDependency
{
    Process,
    Configuration,
    Migrations,
    Imaging,
    Database,
    Schema,
    Storage,
    Queue,
}

public enum HealthState
{
    Healthy,
    Unhealthy,
}

public static class HealthReasonCodes
{
    public const string Healthy = "healthy";
    public const string DependencyMissing = "dependency_missing";
    public const string DependencyUnavailable = "dependency_unavailable";
    public const string DependencyTimeout = "dependency_timeout";
    public const string ConfigurationInvalid = "configuration_invalid";
    public const string MigrationRequired = "migration_required";
    public const string SchemaIncompatible = "schema_incompatible";
    public const string StorageUnavailable = "storage_unavailable";
    public const string QueueUnavailable = "queue_unavailable";
    public const string ImagingUnavailable = "imaging_unavailable";

    private static readonly HashSet<string> Allowed =
    [
        Healthy,
        DependencyMissing,
        DependencyUnavailable,
        DependencyTimeout,
        ConfigurationInvalid,
        MigrationRequired,
        SchemaIncompatible,
        StorageUnavailable,
        QueueUnavailable,
        ImagingUnavailable,
    ];

    internal static string Normalize(string? reasonCode) =>
        reasonCode is not null && Allowed.Contains(reasonCode)
            ? reasonCode
            : DependencyUnavailable;
}

public readonly record struct HealthProbeResult
{
    private HealthProbeResult(HealthState state, string reasonCode)
    {
        State = state;
        ReasonCode = reasonCode;
    }

    public HealthState State { get; }

    public string ReasonCode { get; }

    public static HealthProbeResult Healthy() =>
        new(HealthState.Healthy, HealthReasonCodes.Healthy);

    public static HealthProbeResult Unhealthy(string reasonCode) =>
        new(
            HealthState.Unhealthy,
            HealthReasonCodes.Normalize(reasonCode));
}

public interface IHealthDependencyProbe
{
    HealthDependency Dependency { get; }

    ValueTask<HealthProbeResult> CheckAsync(
        CancellationToken cancellationToken);
}

public sealed record HealthCheckResult
{
    public HealthCheckResult(
        HealthDependency dependency,
        HealthState state,
        string reasonCode)
    {
        if (!Enum.IsDefined(dependency))
        {
            throw new ArgumentOutOfRangeException(nameof(dependency));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        Dependency = dependency;
        State = state;
        ReasonCode = state == HealthState.Healthy
            ? HealthReasonCodes.Healthy
            : HealthReasonCodes.Normalize(reasonCode);
    }

    public HealthDependency Dependency { get; }

    public HealthState State { get; }

    public string ReasonCode { get; }
}

public sealed record HealthReport(
    HealthEndpointKind Endpoint,
    HealthState State,
    IReadOnlyList<HealthCheckResult> Checks);

public static class HealthReportJson
{
    public static string Serialize(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(new
        {
            status = StateName(report.State),
            checks = report.Checks.Select(check => new
            {
                name = DependencyName(check.Dependency),
                status = StateName(check.State),
                reasonCode = check.ReasonCode,
            }),
        });
    }

    private static string StateName(HealthState state) =>
        state switch
        {
            HealthState.Healthy => "healthy",
            HealthState.Unhealthy => "unhealthy",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static string DependencyName(HealthDependency dependency) =>
        dependency switch
        {
            HealthDependency.Process => "process",
            HealthDependency.Configuration => "configuration",
            HealthDependency.Migrations => "migrations",
            HealthDependency.Imaging => "imaging",
            HealthDependency.Database => "database",
            HealthDependency.Schema => "schema",
            HealthDependency.Storage => "storage",
            HealthDependency.Queue => "queue",
            _ => throw new ArgumentOutOfRangeException(nameof(dependency)),
        };
}
