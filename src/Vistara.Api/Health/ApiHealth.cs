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
        services.TryAddSingleton(new HealthEvaluationOptions());
        services.TryAddSingleton<HealthReportCache>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped(static provider => new SafeHealthEvaluator(
            provider.GetServices<IHealthDependencyProbe>(),
            provider.GetRequiredService<HealthEvaluationOptions>(),
            provider.GetRequiredService<HealthReportCache>(),
            provider.GetRequiredService<TimeProvider>()));
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
    /// Maps the health routes. Liveness answers <c>204 No Content</c> with no
    /// body because container health checks assert exactly that. Hosts whose
    /// route table already owns <c>/health/live</c> map the dependency routes
    /// only; liveness is still answered ahead of the pipeline by
    /// <see cref="ApiHealthApplicationBuilderExtensions.UseVistaraApiHealth"/>.
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
                    static (HttpContext context) =>
                    {
                        ApiLiveness.Write(context);
                        return Task.CompletedTask;
                    })
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
        context.RequestServices
            .GetRequiredService<ApiHealthService>()
            .WriteAsync(context, endpoint, context.RequestAborted);
}

internal static class ApiLiveness
{
    internal static void Write(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.ContentLength = 0;
        context.Response.Headers.CacheControl = "no-store";
    }

    internal static bool Matches(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        request.Path.Equals("/health/live", StringComparison.OrdinalIgnoreCase);
}

public static class ApiHealthApplicationBuilderExtensions
{
    /// <summary>
    /// Answers liveness ahead of rate limiting, authentication, and tenant
    /// resolution so a saturated or degraded instance is never reported dead,
    /// and maps readiness and startup as ordinary governed endpoints so their
    /// dependency probes stay behind the rate limiter.
    /// </summary>
    public static IApplicationBuilder UseVistaraApiHealth(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application is IEndpointRouteBuilder endpoints)
        {
            endpoints.MapVistaraApiHealthEndpoints(includeLiveness: false);
        }

        return application.UseMiddleware<ApiLivenessMiddleware>();
    }
}

internal sealed class ApiLivenessMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next =
        next ?? throw new ArgumentNullException(nameof(next));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ApiLiveness.Matches(context.Request))
        {
            return _next(context);
        }

        ApiLiveness.Write(context);
        return Task.CompletedTask;
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
    /// <summary>
    /// Runs a real round trip on the context's own connection rather than
    /// <c>CanConnectAsync</c>. Readiness is anonymous, so a query routed
    /// through the EF pipeline is rejected by the tenant interceptor before it
    /// reaches the server, and a connection that opens is not evidence that the
    /// database answers.
    /// </summary>
    protected override ValueTask CheckCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteSchemaQueryAsync(
            services.GetRequiredService<VistaraDbContext>(),
            "SELECT 1",
            cancellationToken);
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
