using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Api.Composition.Platform;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Observability.Health;
using Vistara.Observability.Telemetry;
using Vistara.Persistence;

namespace Vistara.Api.Health;

public static class ApiHealthServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraApiHealth(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(Probe<ApiConfigurationHealthProbe>());
        services.TryAddEnumerable(Probe<ApiMigrationHealthProbe>());
        services.TryAddEnumerable(Probe<ApiImagingHealthProbe>());
        services.TryAddEnumerable(Probe<ApiDatabaseHealthProbe>());
        services.TryAddEnumerable(Probe<ApiSchemaHealthProbe>());
        services.TryAddEnumerable(Probe<ApiStorageHealthProbe>());
        services.TryAddEnumerable(Probe<ApiQueueHealthProbe>());
        services.TryAddScoped<SafeHealthEvaluator>();
        services.TryAddScoped<ApiHealthService>();
        return services;
    }

    private static ServiceDescriptor Probe<TProbe>()
        where TProbe : class, IHealthDependencyProbe =>
        ServiceDescriptor.Scoped<IHealthDependencyProbe, TProbe>();
}

public static class ApiHealthEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapVistaraApiHealthEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(
                "/health/live",
                static (ApiHealthService health, CancellationToken cancellationToken) =>
                    health.CheckAsync(
                        HealthEndpointKind.Liveness,
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapGet(
                "/health/ready",
                static (ApiHealthService health, CancellationToken cancellationToken) =>
                    health.CheckAsync(
                        HealthEndpointKind.Readiness,
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapGet(
                "/health/startup",
                static (ApiHealthService health, CancellationToken cancellationToken) =>
                    health.CheckAsync(
                        HealthEndpointKind.Startup,
                        cancellationToken))
            .AllowAnonymous();
        return endpoints;
    }
}

public static class ApiObservabilityApplicationBuilderExtensions
{
    public static IApplicationBuilder UseVistaraApiObservability(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.UseMiddleware<ApiObservabilityMiddleware>();
    }
}

public sealed class ApiObservabilityMiddleware(
    RequestDelegate next,
    ILogger<ApiObservabilityMiddleware> logger)
{
    private static readonly Action<ILogger, int, string, Exception?> LogRequest =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, "ApiRequest"),
            "API request completed with status class {StatusClass} and reason {ReasonCode}.");

    private readonly RequestDelegate _next =
        next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TelemetryOutcome outcome = TelemetryOutcome.Success;
        string reasonCode = TelemetryReasonCodes.None;
        using TelemetryOperation operation =
            VistaraTelemetry.Start(TelemetryOperationKind.Api);
        try
        {
            await _next(context);
            if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                outcome = TelemetryOutcome.Failure;
                reasonCode = TelemetryReasonCodes.UnexpectedFailure;
                operation.Fail(reasonCode);
            }
            else if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
            {
                outcome = TelemetryOutcome.Rejected;
                reasonCode = context.Response.StatusCode switch
                {
                    StatusCodes.Status401Unauthorized => "authentication_failed",
                    StatusCodes.Status403Forbidden => "policy_denied",
                    _ => "invalid_request",
                };
                operation.Reject(reasonCode);
                if (context.Response.StatusCode is
                    StatusCodes.Status401Unauthorized or
                    StatusCodes.Status403Forbidden)
                {
                    using TelemetryOperation authorization =
                        VistaraTelemetry.Start(
                            TelemetryOperationKind.Authorization);
                    authorization.Reject(reasonCode);
                }
            }
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            outcome = TelemetryOutcome.Cancelled;
            reasonCode = "cancelled";
            operation.Cancel();
            throw;
        }
        catch (Exception)
        {
            outcome = TelemetryOutcome.Failure;
            reasonCode = TelemetryReasonCodes.UnexpectedFailure;
            operation.Fail(reasonCode);
            throw;
        }
        finally
        {
            TelemetryLogStateCollection state = VistaraTelemetry.CreateLogState(
                TelemetryOperationKind.Api,
                outcome,
                reasonCode);
            using IDisposable? scope = _logger.BeginScope(state);
            LogRequest(
                _logger,
                context.Response.StatusCode / 100,
                state[TelemetryTagNames.ReasonCode]?.ToString() ??
                    TelemetryReasonCodes.UnexpectedFailure,
                null);
        }
    }
}

public sealed class ApiHealthService(SafeHealthEvaluator evaluator)
{
    private readonly SafeHealthEvaluator _evaluator =
        evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<ContentHttpResult> CheckAsync(
        HealthEndpointKind endpoint,
        CancellationToken cancellationToken)
    {
        HealthReport report = await _evaluator.EvaluateAsync(
            endpoint,
            cancellationToken);
        return TypedResults.Text(
            HealthReportJson.Serialize(report),
            "application/json",
            statusCode: report.State == HealthState.Healthy
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
    }
}

internal abstract class ApiHealthProbe(
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
            HealthDependency.Configuration => TelemetryOperationKind.Api,
            _ => TelemetryOperationKind.Database,
        };

    protected static async ValueTask ExecuteSchemaQueryAsync(
        VistaraDbContext context,
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

internal sealed class ApiConfigurationHealthProbe(IServiceProvider services)
    : ApiHealthProbe(
        services,
        HealthDependency.Configuration,
        HealthReasonCodes.ConfigurationInvalid)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = services.GetRequiredService<IOptions<PlatformOptions>>().Value;
        _ = services.GetRequiredService<IOptions<MediaOptions>>().Value;
        _ = services.GetRequiredService<VistaraPersistenceOptions>();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ApiMigrationHealthProbe(IServiceProvider services)
    : ApiHealthProbe(
        services,
        HealthDependency.Migrations,
        HealthReasonCodes.MigrationRequired)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteSchemaQueryAsync(
            services.GetRequiredService<VistaraDbContext>(),
            """SELECT "tenant_id" FROM "worker_tenant_catalog" WHERE 1 = 0""",
            cancellationToken);
}

internal sealed class ApiImagingHealthProbe(IServiceProvider services)
    : ApiHealthProbe(
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

internal sealed class ApiDatabaseHealthProbe(IServiceProvider services)
    : ApiHealthProbe(
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

internal sealed class ApiSchemaHealthProbe(IServiceProvider services)
    : ApiHealthProbe(
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

internal sealed class ApiStorageHealthProbe(IServiceProvider services)
    : ApiHealthProbe(
        services,
        HealthDependency.Storage,
        HealthReasonCodes.StorageUnavailable)
{
    private static readonly BlobKey Sentinel = new("health/readiness");

    protected override async ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        _ = await services
            .GetRequiredService<IBlobStore>()
            .HeadAsync(Sentinel, cancellationToken);
    }
}

internal sealed class ApiQueueHealthProbe(IServiceProvider services)
    : ApiHealthProbe(
        services,
        HealthDependency.Queue,
        HealthReasonCodes.QueueUnavailable)
{
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteSchemaQueryAsync(
            services.GetRequiredService<VistaraDbContext>(),
            """SELECT "id", "state" FROM "jobs" WHERE 1 = 0""",
            cancellationToken);
}
