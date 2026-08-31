using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Vistara.Api.Composition.Media;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.Health;
using Vistara.Observability.Health;
using Vistara.Persistence;
using Xunit;

namespace Vistara.IntegrationTests.Health;

public sealed class ApiHealthWiringTests
{
    [Fact]
    public async Task Liveness_answers_no_content_before_any_dependency_middleware()
    {
        await using WebApplication app = BuildApp(
            probes: [Throwing(HealthDependency.Database)]);

        (int status, string body, string? cacheControl) =
            await SendAsync(app, "/health/live");

        Assert.Equal(StatusCodes.Status204NoContent, status);
        Assert.Empty(body);
        Assert.Equal("no-store", cacheControl);
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

        (int status, string body, _) = await SendAsync(app, "/health/ready");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Contains(
            "\"name\":\"storage\",\"status\":\"unhealthy\"",
            body,
            StringComparison.Ordinal);
        Assert.True(DependencyMiddlewareRan);
    }

    [Fact]
    public async Task Health_responses_never_disclose_configuration_or_topology()
    {
        await using WebApplication app = BuildApp(
            [Unhealthy(HealthDependency.Database, "Host=db;Password=secret")]);

        (_, string body, _) = await SendAsync(app, "/health/ready");

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            HealthReasonCodes.DependencyUnavailable,
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_wiring_maps_dependency_routes_without_duplicating_liveness()
    {
        await using WebApplication app = BuildApp([]);

        string[] routes = Routes(app);

        Assert.Single(routes, route => route == "/health/ready");
        Assert.Single(routes, route => route == "/health/startup");
        Assert.DoesNotContain("/health/live", routes, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Repeated_runtime_wiring_maps_the_health_routes_once()
    {
        await using WebApplication app = BuildApp([], wireTwice: true);

        string[] routes = Routes(app);

        Assert.Single(routes, route => route == "/health/ready");
        Assert.Single(routes, route => route == "/health/startup");
        (int status, _, _) = await SendAsync(app, "/health/ready");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
    }

    [Fact]
    public async Task Unregistered_runtime_services_fail_the_pipeline_at_startup()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        await using WebApplication app = builder.Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => app.UseVistaraApiRuntime());

        Assert.Contains(
            "AddVistaraApiRuntime",
            error.Message,
            StringComparison.Ordinal);
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
    public async Task A_hung_probe_is_bounded_and_reported_as_a_timeout()
    {
        var hung = new StubProbe(
            HealthDependency.Database,
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return HealthProbeResult.Healthy();
            });
        var evaluator = new SafeHealthEvaluator(
            [hung],
            new HealthEvaluationOptions
            {
                ProbeTimeout = TimeSpan.FromMilliseconds(50),
                CacheDuration = TimeSpan.Zero,
            },
            cache: null,
            TimeProvider.System);

        HealthReport report = await evaluator.EvaluateAsync(
            HealthEndpointKind.Readiness,
            CancellationToken.None);

        HealthCheckResult database = Assert.Single(
            report.Checks,
            check => check.Dependency == HealthDependency.Database);
        Assert.Equal(HealthState.Unhealthy, database.State);
        Assert.Equal(HealthReasonCodes.DependencyTimeout, database.ReasonCode);
    }

    [Fact]
    public async Task Flooded_readiness_requests_do_not_flood_the_backend()
    {
        var counting = new StubProbe(
            HealthDependency.Database,
            _ => ValueTask.FromResult(HealthProbeResult.Healthy()));
        var clock = new SteppedTimeProvider(
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        var cache = new HealthReportCache();
        var options = new HealthEvaluationOptions
        {
            ProbeTimeout = TimeSpan.FromSeconds(1),
            CacheDuration = TimeSpan.FromSeconds(5),
        };

        for (int request = 0; request < 50; request++)
        {
            _ = await new SafeHealthEvaluator([counting], options, cache, clock)
                .EvaluateAsync(
                    HealthEndpointKind.Readiness,
                    CancellationToken.None);
        }

        Assert.Equal(1, counting.InvocationCount);

        clock.Advance(TimeSpan.FromSeconds(6));
        _ = await new SafeHealthEvaluator([counting], options, cache, clock)
            .EvaluateAsync(HealthEndpointKind.Readiness, CancellationToken.None);

        Assert.Equal(2, counting.InvocationCount);
    }

    [Fact]
    public async Task Composed_api_readiness_reports_every_registered_probe()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "eng",
            "tests",
            "api-health",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string connectionString =
            $"Data Source=ApiHealth-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(CancellationToken.None);
        try
        {
            DbContextOptions<VistaraDbContext> contextOptions =
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(connectionString)
                    .Options;
            await using (var context = new VistaraDbContext(
                             contextOptions,
                             new FixedTenantScope(Guid.CreateVersion7())))
            {
                await context.Database.EnsureCreatedAsync(CancellationToken.None);
            }

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Media:Storage:Provider"] = "Local",
                    ["Media:Storage:Local:RootPath"] = root,
                    ["Media:Imaging:Provider"] = "NetVips",
                })
                .Build();
            ServiceCollection services = [];
            services.AddSingleton<ITenantScope>(
                new FixedTenantScope(Guid.CreateVersion7()));
            services.AddVistaraPersistence(options =>
            {
                options.Provider = VistaraDatabaseProvider.Sqlite;
                options.ConnectionString = connectionString;
            });
            services.AddVistaraMedia(configuration);
            services.AddVistaraApiHealth();
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();

            HealthReport report = await scope.ServiceProvider
                .GetRequiredService<SafeHealthEvaluator>()
                .EvaluateAsync(
                    HealthEndpointKind.Readiness,
                    CancellationToken.None);

            Assert.True(
                report.State == HealthState.Healthy,
                HealthReportJson.Serialize(report));
            Assert.Equal(
                [
                    HealthDependency.Database,
                    HealthDependency.Schema,
                    HealthDependency.Storage,
                    HealthDependency.Queue,
                ],
                report.Checks.Select(check => check.Dependency));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Api_runtime_composition_is_idempotent_and_registers_every_probe()
    {
        ServiceCollection services = [];
        services.AddVistaraApiRuntime(new ConfigurationBuilder().Build());
        services.AddVistaraApiRuntime(new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(ApiRuntimeMarker));
        using IServiceScope scope = provider.CreateScope();
        Assert.Equal(
            7,
            scope.ServiceProvider.GetServices<IHealthDependencyProbe>().Count());
    }

    private static bool DependencyMiddlewareRan { get; set; }

    private static string[] Routes(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)];

    private static WebApplication BuildApp(
        StubProbe[] probes,
        bool wireTwice = false)
    {
        DependencyMiddlewareRan = false;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddVistaraApiRuntime(builder.Configuration);
        builder.Services.AddSingleton(
            new HealthEvaluationOptions { CacheDuration = TimeSpan.Zero });
        foreach (StubProbe probe in probes)
        {
            builder.Services.AddSingleton<IHealthDependencyProbe>(probe);
        }

        WebApplication app = builder.Build();
        app.UseVistaraApiRuntime();
        if (wireTwice)
        {
            app.UseVistaraApiRuntime();
        }

        app.Use(async (context, next) =>
        {
            DependencyMiddlewareRan = true;
            await next(context);
        });
        app.UseRouting();
#pragma warning disable ASP0014
        app.UseEndpoints(static _ => { });
#pragma warning restore ASP0014
        return app;
    }

    private static async Task<(int StatusCode, string Body, string? CacheControl)>
        SendAsync(
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
        return (
            context.Response.StatusCode,
            body,
            context.Response.Headers.CacheControl);
    }

    private static StubProbe Healthy(HealthDependency dependency) =>
        new(dependency, _ => ValueTask.FromResult(HealthProbeResult.Healthy()));

    private static StubProbe Unhealthy(
        HealthDependency dependency,
        string reasonCode) =>
        new(
            dependency,
            _ => ValueTask.FromResult(HealthProbeResult.Unhealthy(reasonCode)));

    private static StubProbe Throwing(HealthDependency dependency) =>
        new(
            dependency,
            _ => throw new InvalidOperationException("Host=db;Password=secret"));

    private sealed class StubProbe(
        HealthDependency dependency,
        Func<CancellationToken, ValueTask<HealthProbeResult>> result)
        : IHealthDependencyProbe
    {
        private int _invocationCount;

        public HealthDependency Dependency { get; } = dependency;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<HealthProbeResult> CheckAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return result(cancellationToken);
        }
    }

    private sealed class SteppedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
