using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Assets;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Application.Uploads;
using Vistara.Persistence.Repositories;

namespace Vistara.Persistence;

public enum VistaraDatabaseProvider
{
    Sqlite,
    PostgreSql,
}

public sealed class VistaraPersistenceOptions
{
    public VistaraDatabaseProvider Provider { get; set; }

    public string ConnectionString { get; set; } = string.Empty;
}

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraPersistence(
        this IServiceCollection services,
        Action<VistaraPersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var persistenceOptions = new VistaraPersistenceOptions();
        configure(persistenceOptions);
        if (string.IsNullOrWhiteSpace(persistenceOptions.ConnectionString))
        {
            throw new ArgumentException(
                "A persistence connection string is required.",
                nameof(configure));
        }

        services.AddSingleton(persistenceOptions);
        services.AddSingleton<TenantDbContextFactory>();
        services.AddDbContext<VistaraDbContext>(options =>
        {
            switch (persistenceOptions.Provider)
            {
                case VistaraDatabaseProvider.Sqlite:
                    options.UseSqlite(persistenceOptions.ConnectionString);
                    break;
                case VistaraDatabaseProvider.PostgreSql:
                    options.UseNpgsql(persistenceOptions.ConnectionString);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(configure),
                        persistenceOptions.Provider,
                        "The database provider is not supported.");
            }
        });

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<IAuthSessionRepository, AuthSessionRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IBlobMetadataRepository, BlobMetadataRepository>();
        services.AddScoped<IUploadSessionRepository, UploadSessionRepository>();
        return services;
    }
}
