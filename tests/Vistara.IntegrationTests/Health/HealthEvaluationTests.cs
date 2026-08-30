using Vistara.Observability.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Health;
using Vistara.Application.Derivatives;
using Vistara.Worker.Health;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Health;

public sealed class HealthEvaluationTests
{
    [Fact]
    public async Task Liveness_route_is_anonymous_and_returns_process_health()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraApiHealth();
        await using WebApplication app = builder.Build();
        app.MapVistaraApiHealthEndpoints();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == "/health/live" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains("GET", StringComparer.Ordinal));
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = "GET";
        context.Request.Path = "/health/live";
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("\"name\":\"process\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"database\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_requires_database_schema_storage_and_queue()
    {
        IHealthDependencyProbe[] probes =
        [
            Healthy(HealthDependency.Database),
            Healthy(HealthDependency.Schema),
            Healthy(HealthDependency.Storage),
        ];
        var evaluator = new SafeHealthEvaluator(probes);

        HealthReport report = await evaluator.EvaluateAsync(
            HealthEndpointKind.Readiness,
            CancellationToken.None);

        Assert.Equal(HealthState.Unhealthy, report.State);
        HealthCheckResult queue = Assert.Single(
            report.Checks,
            check => check.Dependency == HealthDependency.Queue);
        Assert.Equal(HealthReasonCodes.DependencyMissing, queue.ReasonCode);
    }

    [Fact]
    public async Task Liveness_is_process_only_and_does_not_invoke_dependencies()
    {
        var probe = new DelegateHealthProbe(
            HealthDependency.Database,
            _ => throw new InvalidOperationException("should not run"));
        var evaluator = new SafeHealthEvaluator([probe]);

        HealthReport report = await evaluator.EvaluateAsync(
            HealthEndpointKind.Liveness,
            CancellationToken.None);

        HealthCheckResult result = Assert.Single(report.Checks);
        Assert.Equal(HealthDependency.Process, result.Dependency);
        Assert.Equal(HealthState.Healthy, result.State);
        Assert.Equal(0, probe.InvocationCount);
    }

    [Fact]
    public async Task Failure_responses_redact_exception_details_and_topology()
    {
        const string sensitive =
            "password-field server=db.internal tenant=0198 asset=secret hash=abc";
        var evaluator = new SafeHealthEvaluator(
        [
            Healthy(HealthDependency.Database),
            new DelegateHealthProbe(
                HealthDependency.Schema,
                _ => throw new InvalidOperationException(sensitive)),
            Healthy(HealthDependency.Storage),
            Healthy(HealthDependency.Queue),
        ]);

        HealthReport report = await evaluator.EvaluateAsync(
            HealthEndpointKind.Readiness,
            CancellationToken.None);
        string json = HealthReportJson.Serialize(report);

        Assert.Equal(HealthState.Unhealthy, report.State);
        Assert.DoesNotContain(sensitive, json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asset", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            HealthReasonCodes.DependencyUnavailable,
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Public_health_models_normalize_untrusted_reason_text()
    {
        const string sensitive = "server=db.internal password-field tenant=secret";
        var report = new HealthReport(
            HealthEndpointKind.Readiness,
            HealthState.Unhealthy,
            [
                new HealthCheckResult(
                    HealthDependency.Database,
                    HealthState.Unhealthy,
                    sensitive),
            ]);

        string json = HealthReportJson.Serialize(report);

        Assert.DoesNotContain(sensitive, json, StringComparison.Ordinal);
        Assert.Contains(
            HealthReasonCodes.DependencyUnavailable,
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_requires_configuration_migrations_and_imaging()
    {
        var evaluator = new SafeHealthEvaluator(
        [
            Healthy(HealthDependency.Configuration),
            Healthy(HealthDependency.Migrations),
            Healthy(HealthDependency.Imaging),
        ]);

        HealthReport report = await evaluator.EvaluateAsync(
            HealthEndpointKind.Startup,
            CancellationToken.None);

        Assert.Equal(HealthState.Healthy, report.State);
        Assert.Equal(
            [
                HealthDependency.Configuration,
                HealthDependency.Migrations,
                HealthDependency.Imaging,
            ],
            report.Checks.Select(check => check.Dependency));
    }

    [Fact]
    public async Task Multiple_dependency_probes_all_run_and_use_worst_state()
    {
        DelegateHealthProbe healthyStorage = Healthy(HealthDependency.Storage);
        var unhealthyStorage = new DelegateHealthProbe(
            HealthDependency.Storage,
            _ => ValueTask.FromResult(
                HealthProbeResult.Unhealthy(
                    HealthReasonCodes.StorageUnavailable)));
        var evaluator = new SafeHealthEvaluator(
        [
            Healthy(HealthDependency.Database),
            Healthy(HealthDependency.Schema),
            healthyStorage,
            unhealthyStorage,
            Healthy(HealthDependency.Queue),
        ]);

        HealthReport report = await evaluator.EvaluateAsync(
            HealthEndpointKind.Readiness,
            CancellationToken.None);

        HealthCheckResult storage = Assert.Single(
            report.Checks,
            check => check.Dependency == HealthDependency.Storage);
        Assert.Equal(HealthState.Unhealthy, report.State);
        Assert.Equal(HealthState.Unhealthy, storage.State);
        Assert.Equal(
            HealthReasonCodes.StorageUnavailable,
            storage.ReasonCode);
        Assert.Equal(1, healthyStorage.InvocationCount);
        Assert.Equal(1, unhealthyStorage.InvocationCount);
    }

    [Fact]
    public async Task Multiple_probe_failures_choose_a_deterministic_specific_reason()
    {
        const string sensitive = "server=private password=secret";
        var unavailable = new DelegateHealthProbe(
            HealthDependency.Storage,
            _ => throw new InvalidOperationException(sensitive));
        var storageFailure = new DelegateHealthProbe(
            HealthDependency.Storage,
            _ => ValueTask.FromResult(
                HealthProbeResult.Unhealthy(
                    HealthReasonCodes.StorageUnavailable)));
        IHealthDependencyProbe[] otherProbes =
        [
            Healthy(HealthDependency.Database),
            Healthy(HealthDependency.Schema),
            Healthy(HealthDependency.Queue),
        ];

        HealthReport first = await new SafeHealthEvaluator(
            [.. otherProbes, unavailable, storageFailure])
            .EvaluateAsync(HealthEndpointKind.Readiness, CancellationToken.None);
        HealthReport reversed = await new SafeHealthEvaluator(
            [.. otherProbes, storageFailure, unavailable])
            .EvaluateAsync(HealthEndpointKind.Readiness, CancellationToken.None);

        HealthCheckResult firstStorage = Assert.Single(
            first.Checks,
            check => check.Dependency == HealthDependency.Storage);
        HealthCheckResult reversedStorage = Assert.Single(
            reversed.Checks,
            check => check.Dependency == HealthDependency.Storage);
        Assert.Equal(
            HealthReasonCodes.StorageUnavailable,
            firstStorage.ReasonCode);
        Assert.Equal(firstStorage, reversedStorage);
        Assert.Equal(2, unavailable.InvocationCount);
        Assert.Equal(2, storageFailure.InvocationCount);
        Assert.DoesNotContain(
            sensitive,
            HealthReportJson.Serialize(first),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Role_composition_registers_required_probes_and_worker_hooks()
    {
        ServiceCollection api = [];
        api.AddVistaraApiHealth();

        Assert.Equal(
            7,
            api.Count(descriptor =>
                descriptor.ServiceType == typeof(IHealthDependencyProbe)));
        Assert.Contains(
            api,
            descriptor =>
                descriptor.ServiceType == typeof(ApiHealthService));
        using (ServiceProvider apiProvider = api.BuildServiceProvider())
        {
            Assert.NotNull(
                apiProvider.GetRequiredService<SafeHealthEvaluator>());
        }

        ServiceCollection worker = [];
        worker.AddSingleton<IJobRuntimeObserver>(
            NullJobRuntimeObserver.Instance);
        worker.AddVistaraWorkerHealth();

        Assert.Equal(
            7,
            worker.Count(descriptor =>
                descriptor.ServiceType == typeof(IHealthDependencyProbe)));
        Assert.Contains(
            worker,
            descriptor =>
                descriptor.ServiceType == typeof(IJobRuntimeObserver) &&
                descriptor.ImplementationType ==
                    typeof(OpenTelemetryJobRuntimeObserver));
        Assert.Contains(
            worker,
            descriptor =>
                descriptor.ServiceType == typeof(IDerivativeCheckpointObserver) &&
                descriptor.ImplementationType ==
                    typeof(OpenTelemetryDerivativeCheckpointObserver));
        Assert.Contains(
            worker,
            descriptor =>
                descriptor.ServiceType == typeof(IUploadReconciliationObserver) &&
                descriptor.ImplementationType ==
                    typeof(OpenTelemetryUploadReconciliationObserver));
        Assert.Contains(
            worker,
            descriptor =>
                descriptor.ServiceType ==
                    typeof(IUploadReconciliationCheckpointObserver) &&
                descriptor.ImplementationType ==
                    typeof(OpenTelemetryUploadReconciliationCheckpointObserver));
        using ServiceProvider workerProvider = worker.BuildServiceProvider();
        Assert.NotNull(
            workerProvider.GetRequiredService<SafeHealthEvaluator>());
    }

    private static DelegateHealthProbe Healthy(HealthDependency dependency) =>
        new DelegateHealthProbe(
            dependency,
            _ => ValueTask.FromResult(HealthProbeResult.Healthy()));

    private sealed class DelegateHealthProbe(
        HealthDependency dependency,
        Func<CancellationToken, ValueTask<HealthProbeResult>> check)
        : IHealthDependencyProbe
    {
        public HealthDependency Dependency { get; } = dependency;

        public int InvocationCount { get; private set; }

        public ValueTask<HealthProbeResult> CheckAsync(
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return check(cancellationToken);
        }
    }
}
