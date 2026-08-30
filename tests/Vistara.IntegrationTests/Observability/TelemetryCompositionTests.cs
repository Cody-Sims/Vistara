using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vistara.Observability.Telemetry;
using Xunit;

namespace Vistara.IntegrationTests.Observability;

public sealed class TelemetryCompositionTests
{
    private static readonly Action<ILogger, Exception?> LogSweepFinished =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, "ReconciliationSweep"),
            "Reconciliation sweep finished.");

    [Fact]
    public async Task Composed_telemetry_exports_vistara_traces_metrics_and_logs()
    {
        List<Activity> activities = [];
        List<Metric> metrics = [];
        List<LogRecord> logs = [];
        using IHost host = BuildHost(
            new Dictionary<string, string?>
            {
                ["Telemetry:ServiceName"] = "vistara-test",
            },
            activities,
            metrics,
            logs);

        await host.StartAsync(CancellationToken.None);
        using (TelemetryOperation operation =
                   VistaraTelemetry.Start(TelemetryOperationKind.Reconciliation))
        {
            Assert.NotNull(Activity.Current);
            operation.Fail("reconciliation_failure");
        }

        LogSweepFinished(
            host.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Vistara.Test"),
            null);
        host.Services.GetRequiredService<TracerProvider>().ForceFlush();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush();
        await host.StopAsync(CancellationToken.None);

        Activity exported = Assert.Single(
            activities,
            candidate => candidate.OperationName == "reconciliation.run");
        Assert.Equal(ActivityStatusCode.Error, exported.Status);
        Assert.Equal(
            "reconciliation",
            exported.GetTagItem(TelemetryTagNames.Area));
        Assert.Contains(
            metrics,
            metric => metric.Name == "vistara.operations");
        Assert.Contains(
            metrics,
            metric => metric.MeterName == "System.Runtime");
        Assert.NotEmpty(logs);
    }

    [Fact]
    public async Task Composed_telemetry_never_labels_series_with_tenant_or_asset_identity()
    {
        List<Activity> activities = [];
        List<Metric> metrics = [];
        List<LogRecord> logs = [];
        using IHost host = BuildHost(
            new Dictionary<string, string?>(),
            activities,
            metrics,
            logs);

        await host.StartAsync(CancellationToken.None);
        using (VistaraTelemetry.Start(TelemetryOperationKind.Storage))
        {
        }

        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        await host.StopAsync(CancellationToken.None);

        string[] allowed =
        [
            TelemetryTagNames.Area,
            TelemetryTagNames.Operation,
            TelemetryTagNames.Outcome,
            TelemetryTagNames.ReasonCode,
            TelemetryTagNames.Checkpoint,
        ];
        Metric operations = Assert.Single(
            metrics,
            metric => metric.Name == "vistara.operations");
        List<string> tagKeys = [];
        foreach (MetricPoint point in operations.GetMetricPoints())
        {
            foreach (KeyValuePair<string, object?> tag in point.Tags)
            {
                tagKeys.Add(tag.Key);
            }
        }

        Assert.NotEmpty(tagKeys);
        Assert.All(
            tagKeys,
            key => Assert.Contains(key, allowed, StringComparer.Ordinal));
    }

    [Fact]
    public void Disabled_telemetry_composes_without_providers()
    {
        ServiceCollection services = [];
        services.AddVistaraTelemetry(
            Configuration(new Dictionary<string, string?>
            {
                ["Telemetry:Enabled"] = "false",
            }),
            "vistara-test");
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<TracerProvider>());
        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Theory]
    [InlineData("Telemetry:SamplingRatio", "1.5")]
    [InlineData("Telemetry:ServiceName", "  ")]
    [InlineData("Telemetry:OtlpEndpoint", "not-a-uri")]
    public void Invalid_telemetry_configuration_fails_fast(string key, string value)
    {
        ServiceCollection services = [];

        Assert.Throws<InvalidOperationException>(() =>
            services.AddVistaraTelemetry(
                Configuration(new Dictionary<string, string?> { [key] = value }),
                "vistara-test"));
    }

    private static IHost BuildHost(
        Dictionary<string, string?> settings,
        List<Activity> activities,
        List<Metric> metrics,
        List<LogRecord> logs)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddVistaraTelemetry(builder.Configuration, "vistara-test");
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(activities))
            .WithMetrics(meters => meters.AddInMemoryExporter(metrics))
            .WithLogging(logging => logging.AddInMemoryExporter(logs));
        return builder.Build();
    }

    private static IConfiguration Configuration(
        Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
