using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vistara.Application.Derivatives;
using Vistara.Persistence.Derivatives.Worker;
using Vistara.Persistence.Jobs;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

public sealed class WorkerRuntimeConfigurationTests
{
    [Fact]
    public void Sqlite_worker_defaults_to_exactly_one_job_processor()
    {
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(Configuration("Sqlite"));

        using ServiceProvider provider = services.BuildServiceProvider();
        WorkerPlatformOptions options = provider
            .GetRequiredService<IOptions<WorkerPlatformOptions>>()
            .Value;
        JobQueueOptions queue = provider.GetRequiredService<JobQueueOptions>();

        Assert.Equal(1, options.Jobs.MaximumConcurrency);
        Assert.Equal(1, queue.ConfiguredWorkerCount);
    }

    [Fact]
    public void Sqlite_worker_rejects_parallel_job_processor_configuration()
    {
        ServiceCollection services = [];
        IConfiguration configuration = Configuration(
            "Sqlite",
            ("Worker:Jobs:MaximumConcurrency", "2"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => services.AddVistaraWorkerPlatform(configuration));

        Assert.Contains("single worker", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgreSql_worker_uses_configured_job_concurrency()
    {
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(Configuration(
            "PostgreSql",
            ("Worker:Jobs:MaximumConcurrency", "6")));

        using ServiceProvider provider = services.BuildServiceProvider();
        WorkerPlatformOptions options = provider
            .GetRequiredService<IOptions<WorkerPlatformOptions>>()
            .Value;
        JobQueueOptions queue = provider.GetRequiredService<JobQueueOptions>();

        Assert.Equal(6, options.Jobs.MaximumConcurrency);
        Assert.Equal(6, queue.ConfiguredWorkerCount);
    }

    [Fact]
    public void Worker_imaging_defaults_match_the_operator_hard_ceiling()
    {
        var limits = new WorkerImagingLimitsOptions();

        Assert.Equal(50L * 1024 * 1024, limits.MaxEncodedBytes);
        Assert.Equal(20_000, limits.MaxWidth);
        Assert.Equal(20_000, limits.MaxHeight);
        Assert.Equal(40_000_000, limits.MaxAggregatePixels);
        Assert.Equal(1, limits.MaxFrames);
        Assert.Equal(512L * 1024 * 1024, limits.MaxEstimatedDecodedBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), limits.ProcessingDeadline);
        Assert.Equal(1, limits.MaximumConcurrentTransforms);
    }

    [Fact]
    public void Worker_imaging_rejects_configuration_above_the_hard_ceiling()
    {
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(Configuration(
            "Sqlite",
            ("Worker:ImagingLimits:MaxAggregatePixels", "40000001")));
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException error = Assert.Throws<OptionsValidationException>(
            () => _ = provider
                .GetRequiredService<IOptions<WorkerPlatformOptions>>()
                .Value);

        Assert.Contains("imaging", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_composition_registers_durable_derivatives_and_upload_reconciliation()
    {
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(Configuration("Sqlite"));

        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IDerivativeStatePort) &&
                descriptor.ImplementationType ==
                    typeof(RelationalDerivativeStatePort));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IJobHandler) &&
                descriptor.ImplementationType == typeof(DerivativeJobHandler));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IJobHandler) &&
                descriptor.ImplementationType ==
                    typeof(UploadReconciliationJobHandler));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType?.Name ==
                    "UploadReconciliationSchedulerHostedService");
    }

    private static IConfiguration Configuration(
        string provider,
        params (string Key, string Value)[] values)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = provider,
            ["Persistence:ConnectionString"] = provider == "Sqlite"
                ? "Data Source=:memory:"
                : "Host=localhost;Database=vistara;Username=vistara;Password=unused",
            ["Worker:InstanceId"] = "configuration-test",
        };
        foreach ((string key, string value) in values)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }
}
