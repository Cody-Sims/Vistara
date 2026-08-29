using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Jobs;

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

        services.AddDbContext<JobDbContext>(builder =>
        {
            if (options.Provider == VistaraDatabaseProvider.Sqlite)
            {
                builder.UseSqlite(options.ConnectionString);
            }
            else if (options.Provider == VistaraDatabaseProvider.PostgreSql)
            {
                builder.UseNpgsql(options.ConnectionString);
            }
            else
            {
                throw new ArgumentOutOfRangeException(
                    nameof(configure),
                    options.Provider,
                    "The job database provider is not supported.");
            }
        });
        services.AddSingleton(new JobQueueOptions
        {
            ConfiguredWorkerCount = options.ConfiguredWorkerCount,
        });
        services.AddScoped<IJobQueue, RelationalJobQueue>();
        return services;
    }
}
