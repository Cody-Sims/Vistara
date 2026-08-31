using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Assets;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Application.Uploads;
using Vistara.Application.Uploads.Quotas;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Events;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Media;
using Vistara.Persistence.Repositories;
using Vistara.Persistence.Uploads;

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
        services.AddDbContext<AuthenticationCatalogDbContext>(options =>
        {
            if (persistenceOptions.Provider == VistaraDatabaseProvider.Sqlite)
            {
                options.UseSqlite(persistenceOptions.ConnectionString);
            }
            else
            {
                options.UseNpgsql(persistenceOptions.ConnectionString);
            }
        });
        services.AddDbContext<Identity.IdentityCatalogDbContext>(options =>
        {
            if (persistenceOptions.Provider == VistaraDatabaseProvider.Sqlite)
            {
                options.UseSqlite(persistenceOptions.ConnectionString);
            }
            else
            {
                options.UseNpgsql(persistenceOptions.ConnectionString);
            }
        });
        services.AddDbContext<JwtRevocationCatalogDbContext>(options =>
        {
            if (persistenceOptions.Provider == VistaraDatabaseProvider.Sqlite)
            {
                options.UseSqlite(persistenceOptions.ConnectionString);
            }
            else
            {
                options.UseNpgsql(persistenceOptions.ConnectionString);
            }
        });
        services.AddDbContext<MediaCatalogDbContext>(options =>
        {
            if (persistenceOptions.Provider == VistaraDatabaseProvider.Sqlite)
            {
                options.UseSqlite(persistenceOptions.ConnectionString);
            }
            else
            {
                options.UseNpgsql(persistenceOptions.ConnectionString);
            }
        });
        services.AddDbContext<RateLimitCatalogDbContext>(options =>
        {
            if (persistenceOptions.Provider == VistaraDatabaseProvider.Sqlite)
            {
                options.UseSqlite(persistenceOptions.ConnectionString);
            }
            else
            {
                options.UseNpgsql(persistenceOptions.ConnectionString);
            }
        });
        services.AddScoped<RelationalAuthenticationStore>();
        services.AddScoped<Identity.RelationalIdentityCatalog>();
        services.AddScoped<Identity.RelationalUserPreferenceStore>();
        services.TryAddScoped<
            Vistara.Application.Common.Auditing.IAuditWriter,
            Auditing.RelationalAuditWriter>();
        services.AddScoped<RelationalDerivativeRequestStore>();
        services.AddScoped<RelationalEventStreamStore>();
        services.AddScoped<RelationalMediaCatalogStore>();
        services.AddScoped<RelationalRateLimitStore>();

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<IAuthSessionRepository, AuthSessionRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IBlobMetadataRepository, BlobMetadataRepository>();
        services.AddScoped<IUploadSessionRepository, UploadSessionRepository>();
        services.TryAddSingleton(new UploadPersistenceOptions());
        services.TryAddScoped<RelationalUploadApplicationStore>();
        services.TryAddScoped<IQuotaReservationStore, RelationalQuotaReservationStore>();
        services.TryAddScoped<RelationalAssetIngestUnitOfWork>();
        services.TryAddScoped<RelationalIngestStore>();
        return services;
    }
}
