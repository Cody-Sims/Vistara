using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Jobs;
using Vistara.Persistence.Azure;

namespace Vistara.Persistence.Jobs;

public sealed class JobPersistenceOptions
{
    public VistaraDatabaseProvider Provider { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public int ConfiguredWorkerCount { get; set; } = 1;
}

public static class JobPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraJobQueue(
        this IServiceCollection services,
        Action<JobPersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new JobPersistenceOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                "A job persistence connection string is required.",
                nameof(configure));
        }

        services.AddVistaraNpgsqlDataSources();
        services.AddDbContext<JobDbContext>((provider, builder) =>
            builder.UseVistaraDatabase(
                provider,
                options.Provider,
                options.ConnectionString));
        services.AddDbContext<WorkerTenantCatalogDbContext>((provider, builder) =>
            builder.UseVistaraDatabase(
                provider,
                options.Provider,
                options.ConnectionString));
        services.AddSingleton(new JobQueueOptions
        {
            ConfiguredWorkerCount = options.ConfiguredWorkerCount,
        });
        services.AddScoped<
            IWorkerTenantCatalog,
            RelationalWorkerTenantCatalog>();
        services.AddScoped<IJobQueue, RelationalJobQueue>();
        return services;
    }
}
