using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.Health;
using Vistara.Observability.Health;
using Xunit;

namespace Vistara.IntegrationTests.Health;

public sealed class ApiHealthWiringTests
{
    [Fact]
    public async Task Liveness_answers_before_any_dependency_middleware_runs()
    {
        await using WebApplication app = BuildApp(
            probes: [Throwing(HealthDependency.Database)]);

        (int status, string body) = await SendAsync(app, "/health/live");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"name\":\"process\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"database\"", body, StringComparison.Ordinal);
        Assert.False(DependencyMiddlewareRan);
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_when_a_dependency_is_degraded()
    {
        await using WebApplication app = BuildApp(
        [
            Healthy(HealthDependency.Database),
            Healthy(HealthDependency.Schema),
            Throwing(HealthDependency.Storage),
            Healthy(HealthDependency.Queue),
        ]);

        (int status, string body) = await SendAsync(app, "/health/ready");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Contains(
            "\"name\":\"storage\",\"status\":\"unhealthy\"",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_responses_never_disclose_configuration_or_topology()
    {
        await using WebApplication app = BuildApp(
            [Unhealthy(HealthDependency.Database, "Host=db;Password=secret")]);

        (_, string body) = await SendAsync(app, "/health/ready");

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            HealthReasonCodes.DependencyUnavailable,
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_health_is_uncomposed_safe_and_reports_missing_dependencies()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        await using WebApplication app = builder.Build();
        app.UseVistaraApiHealth();

        (int liveStatus, string liveBody) = await SendAsync(app, "/health/live");
        (int startupStatus, string startupBody) =
            await SendAsync(app, "/health/startup");

        Assert.Equal(StatusCodes.Status200OK, liveStatus);
        Assert.Contains("\"name\":\"process\"", liveBody, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, startupStatus);
        Assert.Contains(
            HealthReasonCodes.DependencyMissing,
            startupBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_wiring_maps_dependency_routes_without_duplicating_liveness()
    {
        await using WebApplication app = BuildApp([]);

        string[] routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToArray();

        Assert.Single(routes, route => route == "/health/ready");
        Assert.Single(routes, route => route == "/health/startup");
        Assert.DoesNotContain("/health/live", routes, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Health_evaluation_honours_request_cancellation()
    {
        await using WebApplication app = BuildApp(
            [Healthy(HealthDependency.Database)]);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SendAsync(app, "/health/ready", aborted.Token));
    }

    [Fact]
    public void Api_runtime_composition_registers_health_probes_and_telemetry()
    {
        ServiceCollection services = [];
        services.AddVistaraApiRuntime(
            new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<TracerProvider>());
        using IServiceScope scope = provider.CreateScope();
        Assert.NotEmpty(
            scope.ServiceProvider.GetServices<IHealthDependencyProbe>());
    }

    private static bool DependencyMiddlewareRan { get; set; }

    private static WebApplication BuildApp(IHealthDependencyProbe[] probes)
    {
        DependencyMiddlewareRan = false;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        foreach (IHealthDependencyProbe probe in probes)
        {
            builder.Services.AddSingleton(probe);
        }

        builder.Services.AddScoped<SafeHealthEvaluator>();
        builder.Services.AddScoped<ApiHealthService>();
        WebApplication app = builder.Build();
        app.UseVistaraApiHealth();
        app.Use(async (context, next) =>
        {
            DependencyMiddlewareRan = true;
            await next(context);
        });
        return app;
    }

    private static async Task<(int StatusCode, string Body)> SendAsync(
        WebApplication app,
        string path,
        CancellationToken cancellationToken = default)
    {
        RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await pipeline(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return (context.Response.StatusCode, body);
    }

    private static StubProbe Healthy(HealthDependency dependency) =>
        new StubProbe(dependency, () => HealthProbeResult.Healthy());

    private static StubProbe Unhealthy(
        HealthDependency dependency,
        string reasonCode) =>
        new StubProbe(dependency, () => HealthProbeResult.Unhealthy(reasonCode));

    private static StubProbe Throwing(HealthDependency dependency) =>
        new StubProbe(
            dependency,
            () => throw new InvalidOperationException(
                "Host=db;Password=secret"));

    private sealed class StubProbe(
        HealthDependency dependency,
        Func<HealthProbeResult> result) : IHealthDependencyProbe
    {
        public HealthDependency Dependency { get; } = dependency;

        public ValueTask<HealthProbeResult> CheckAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result());
        }
    }
}
