using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Vistara.Api.Composition.Gallery;
using Vistara.Api.Composition.Platform;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Azure;
using Vistara.Persistence.Events;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Media;
using Vistara.Persistence.Outbox;
using Vistara.Persistence.Sharing;
using Vistara.Worker.Composition.Platform;
using Xunit;

namespace Vistara.IntegrationTests.Persistence.Azure;

/// <summary>
/// Every PostgreSQL call site must open connections through the one Entra-backed
/// data source; a missed call site would silently fall back to password
/// authentication that the hosted deployment does not have.
/// </summary>
public sealed class NpgsqlEntraContextResolutionTests
{
    [Fact]
    public void Every_api_context_uses_the_one_entra_data_source()
    {
        using ServiceProvider services = ApiServices(entraTokensEnabled: true);
        using IServiceScope scope = services.CreateScope();
        DbDataSource expected = Assert.IsAssignableFrom<DbDataSource>(
            services.GetRequiredService<VistaraNpgsqlDataSourceProvider>()
                .GetDataSource(AzureEntraTestSupport.AzureConnectionString));

        Assert.Same(expected, DataSourceOf<VistaraDbContext>(scope));
        Assert.Same(expected, DataSourceOf<AuthenticationCatalogDbContext>(scope));
        Assert.Same(expected, DataSourceOf<IdentityCatalogDbContext>(scope));
        Assert.Same(expected, DataSourceOf<JwtRevocationCatalogDbContext>(scope));
        Assert.Same(expected, DataSourceOf<MediaCatalogDbContext>(scope));
        Assert.Same(expected, DataSourceOf<RateLimitCatalogDbContext>(scope));
        Assert.Same(expected, DataSourceOf<SharingDbContext>(scope));
    }

    [Fact]
    public void The_tenant_context_factory_uses_the_one_entra_data_source()
    {
        using ServiceProvider services = ApiServices(entraTokensEnabled: true);
        DbDataSource expected = Assert.IsAssignableFrom<DbDataSource>(
            services.GetRequiredService<VistaraNpgsqlDataSourceProvider>()
                .GetDataSource(AzureEntraTestSupport.AzureConnectionString));

        using VistaraDbContext context = services
            .GetRequiredService<TenantDbContextFactory>()
            .Create(Guid.CreateVersion7());

        Assert.Same(expected, DataSourceOf(context));
    }

    [Fact]
    public void Every_worker_context_uses_the_one_entra_data_source()
    {
        using ServiceProvider services = WorkerServices(entraTokensEnabled: true);
        using IServiceScope scope = services.CreateScope();
        DbDataSource expected = Assert.IsAssignableFrom<DbDataSource>(
            services.GetRequiredService<VistaraNpgsqlDataSourceProvider>()
                .GetDataSource(AzureEntraTestSupport.AzureConnectionString));

        Assert.Same(expected, DataSourceOf<VistaraDbContext>(scope));
        Assert.Same(expected, DataSourceOf<JobDbContext>(scope));
        Assert.Same(expected, DataSourceOf<WorkerTenantCatalogDbContext>(scope));
        Assert.Same(expected, DataSourceOf<OutboxDbContext>(scope));
    }

    [Fact]
    public void Api_contexts_keep_password_connection_strings_when_entra_is_off()
    {
        using ServiceProvider services = ApiServices(entraTokensEnabled: false);
        using IServiceScope scope = services.CreateScope();

        Assert.Null(DataSourceOf<VistaraDbContext>(scope));
        Assert.Null(DataSourceOf<SharingDbContext>(scope));
        Assert.Equal(
            AzureEntraTestSupport.PasswordConnectionString,
            ConnectionStringOf<VistaraDbContext>(scope));
        Assert.Equal(
            AzureEntraTestSupport.PasswordConnectionString,
            ConnectionStringOf<SharingDbContext>(scope));
    }

    [Fact]
    public void Worker_contexts_keep_password_connection_strings_when_entra_is_off()
    {
        using ServiceProvider services = WorkerServices(entraTokensEnabled: false);
        using IServiceScope scope = services.CreateScope();

        Assert.Null(DataSourceOf<JobDbContext>(scope));
        Assert.Null(DataSourceOf<OutboxDbContext>(scope));
        Assert.Equal(
            AzureEntraTestSupport.PasswordConnectionString,
            ConnectionStringOf<OutboxDbContext>(scope));
    }

    [Fact]
    public void Sqlite_deployments_reject_entra_tokens()
    {
        ServiceCollection services = [];
        IConfiguration configuration = Configuration(
            "Sqlite",
            "Data Source=:memory:",
            entraTokensEnabled: true);
        services.AddSingleton(configuration);
        services.AddVistaraPersistence(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = "Data Source=:memory:";
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<DbContextOptions<VistaraDbContext>>());
        Assert.Contains("SQLite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sqlite_deployments_stay_on_sqlite_without_azure_configuration()
    {
        ServiceCollection services = [];
        services.AddSingleton(
            Configuration("Sqlite", "Data Source=:memory:", entraTokensEnabled: false));
        services.AddVistaraPersistence(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = "Data Source=:memory:";
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.Null(DataSourceOf<VistaraDbContext>(scope));
    }

    private static ServiceProvider ApiServices(bool entraTokensEnabled)
    {
        IConfiguration configuration = PostgresConfiguration(entraTokensEnabled);
        ServiceCollection services = [];
        services.AddSingleton(configuration);
        RegisterTestCredential(services, entraTokensEnabled);
        services.AddVistaraApiPersistence(configuration);
        services.AddVistaraGallery(configuration);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider WorkerServices(bool entraTokensEnabled)
    {
        IConfiguration configuration = PostgresConfiguration(entraTokensEnabled);
        ServiceCollection services = [];
        services.AddSingleton(configuration);
        RegisterTestCredential(services, entraTokensEnabled);
        services.AddVistaraWorkerPlatform(configuration);
        return services.BuildServiceProvider();
    }

    private static void RegisterTestCredential(
        IServiceCollection services,
        bool entraTokensEnabled)
    {
        if (!entraTokensEnabled)
        {
            return;
        }

        services.AddSingleton(new VistaraNpgsqlDataSourceProvider(
            AzureEntraTestSupport.EnabledOptions(),
            new RecordingTokenCredential()));
    }

    private static IConfiguration PostgresConfiguration(bool entraTokensEnabled)
    {
        return Configuration(
            "PostgreSql",
            entraTokensEnabled
                ? AzureEntraTestSupport.AzureConnectionString
                : AzureEntraTestSupport.PasswordConnectionString,
            entraTokensEnabled);
    }

    private static IConfiguration Configuration(
        string provider,
        string connectionString,
        bool entraTokensEnabled)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = provider,
                ["Persistence:ConnectionString"] = connectionString,
                ["Persistence:Azure:EntraTokenEnabled"] =
                    entraTokensEnabled ? "true" : null,
                ["Persistence:Azure:ManagedIdentityClientId"] =
                    entraTokensEnabled ? AzureEntraTestSupport.ClientId : null,
                ["Worker:InstanceId"] = "npgsql-entra-resolution",
                ["Worker:Jobs:MaximumConcurrency"] = "1",
            })
            .Build();
    }

    // EF1001: NpgsqlOptionsExtension is the only place the configured data
    // source is observable, and reading it is what proves a call site was not
    // left behind on a password connection string.
#pragma warning disable EF1001
    private static DbDataSource? DataSourceOf<TContext>(IServiceScope scope)
        where TContext : DbContext
    {
        return scope.ServiceProvider
            .GetRequiredService<DbContextOptions<TContext>>()
            .FindExtension<NpgsqlOptionsExtension>()
            ?.DataSource;
    }

    private static DbDataSource? DataSourceOf(DbContext context)
    {
        return context.GetService<IDbContextOptions>()
            .FindExtension<NpgsqlOptionsExtension>()
            ?.DataSource;
    }

    private static string? ConnectionStringOf<TContext>(IServiceScope scope)
        where TContext : DbContext
    {
        return scope.ServiceProvider
            .GetRequiredService<DbContextOptions<TContext>>()
            .FindExtension<NpgsqlOptionsExtension>()
            ?.ConnectionString;
    }
#pragma warning restore EF1001
}
