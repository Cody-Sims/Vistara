using System.Data;
using System.Data.Common;
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
        this IEndpointRouteBuilder endpoints) =>
        endpoints.MapVistaraApiHealthEndpoints(includeLiveness: true);

    /// <summary>
    /// Maps the health routes. Hosts whose route table already owns
    /// <c>/health/live</c> map the dependency routes only; liveness is still
    /// answered by <see cref="ApiHealthApplicationBuilderExtensions.UseVistaraApiHealth"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapVistaraApiHealthEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool includeLiveness)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (includeLiveness)
        {
            endpoints.MapGet(
                    "/health/live",
                    static (HttpContext context) => Write(
                        context,
                        HealthEndpointKind.Liveness))
                .AllowAnonymous();
        }

        endpoints.MapGet(
                "/health/ready",
                static (HttpContext context) => Write(
                    context,
                    HealthEndpointKind.Readiness))
            .AllowAnonymous();
        endpoints.MapGet(
                "/health/startup",
                static (HttpContext context) => Write(
                    context,
                    HealthEndpointKind.Startup))
            .AllowAnonymous();
        return endpoints;
    }

    private static Task Write(
        HttpContext context,
        HealthEndpointKind endpoint) =>
        ApiHealthService
            .Resolve(context.RequestServices)
            .WriteAsync(context, endpoint, context.RequestAborted);
}

public static class ApiHealthApplicationBuilderExtensions
{
    /// <summary>
    /// Answers the health paths ahead of rate limiting, authentication, and
    /// tenant resolution so probes keep working while dependencies are
    /// degraded, and advertises the dependency routes on the route table.
    /// </summary>
    public static IApplicationBuilder UseVistaraApiHealth(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application is IEndpointRouteBuilder endpoints)
        {
            endpoints.MapVistaraApiHealthEndpoints(includeLiveness: false);
        }

        return application.UseMiddleware<ApiHealthMiddleware>();
    }
}

internal sealed class ApiHealthMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next =
        next ?? throw new ArgumentNullException(nameof(next));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !TryResolve(context.Request.Path, out HealthEndpointKind endpoint))
        {
            return _next(context);
        }

        return ApiHealthService
            .Resolve(context.RequestServices)
            .WriteAsync(context, endpoint, context.RequestAborted);
    }

    private static bool TryResolve(PathString path, out HealthEndpointKind endpoint)
    {
        if (path.Equals("/health/live", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = HealthEndpointKind.Liveness;
            return true;
        }

        if (path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = HealthEndpointKind.Readiness;
            return true;
        }

        if (path.Equals("/health/startup", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = HealthEndpointKind.Startup;
            return true;
        }

        endpoint = default;
        return false;
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

    /// <summary>
    /// Resolves the composed health service, or an evaluator over whatever
    /// probes are registered, so health never fails with a server error when
    /// runtime composition is incomplete.
    /// </summary>
    internal static ApiHealthService Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<ApiHealthService>() ??
            new ApiHealthService(
                new SafeHealthEvaluator(
                    services.GetServices<IHealthDependencyProbe>()));
    }

    public async Task WriteAsync(
        HttpContext context,
        HealthEndpointKind endpoint,
        CancellationToken cancellationToken)
    {
        HealthReport report = await _evaluator.EvaluateAsync(
            endpoint,
            cancellationToken);
        context.Response.StatusCode = StatusFor(report);
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(
            HealthReportJson.Serialize(report),
            cancellationToken);
    }

    private static int StatusFor(HealthReport report) =>
        report.State == HealthState.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
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
            """SELECT "routed_tenant_id" FROM "worker_tenant_catalog" WHERE 1 = 0""",
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
