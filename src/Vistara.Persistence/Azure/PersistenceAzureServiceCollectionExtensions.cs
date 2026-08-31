using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Vistara.Persistence.Azure;

/// <summary>
/// Registers the shared Entra data source and routes every relational call site
/// through it, so enabling <c>Persistence:Azure</c> cannot leave one context
/// behind on password authentication.
/// </summary>
public static class PersistenceAzureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provider without a configuration instance. Options are
    /// resolved once, from an explicitly registered
    /// <see cref="PersistenceAzureOptions"/> when present and otherwise from the
    /// host <see cref="IConfiguration"/>.
    /// </summary>
    public static IServiceCollection AddVistaraNpgsqlDataSources(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(static provider =>
            new VistaraNpgsqlDataSourceProvider(
                provider.GetService<PersistenceAzureOptions>()
                ?? PersistenceAzureOptions.FromConfiguration(
                    provider.GetService<IConfiguration>())));
        return services;
    }

    /// <summary>
    /// Registers the provider and binds <c>Persistence:Azure</c> eagerly so a
    /// misconfigured deployment fails during composition rather than on the first
    /// database call.
    /// </summary>
    public static IServiceCollection AddVistaraNpgsqlDataSources(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(PersistenceAzureOptions.FromConfiguration(configuration));
        return services.AddVistaraNpgsqlDataSources();
    }

    public static DbContextOptionsBuilder UseVistaraDatabase(
        this DbContextOptionsBuilder builder,
        IServiceProvider services,
        VistaraDatabaseProvider databaseProvider,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        return builder.UseVistaraDatabase(
            services.GetService<VistaraNpgsqlDataSourceProvider>(),
            databaseProvider,
            connectionString);
    }

    public static DbContextOptionsBuilder UseVistaraDatabase(
        this DbContextOptionsBuilder builder,
        VistaraNpgsqlDataSourceProvider? dataSources,
        VistaraDatabaseProvider databaseProvider,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        dataSources?.EnsureSupports(databaseProvider);
        switch (databaseProvider)
        {
            case VistaraDatabaseProvider.Sqlite:
                return builder.UseSqlite(connectionString);
            case VistaraDatabaseProvider.PostgreSql:
                NpgsqlDataSource? dataSource = dataSources?.GetDataSource(connectionString);
                return dataSource is null
                    ? builder.UseNpgsql(connectionString)
                    : builder.UseNpgsql(dataSource);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(databaseProvider),
                    databaseProvider,
                    "The database provider is not supported.");
        }
    }
}
