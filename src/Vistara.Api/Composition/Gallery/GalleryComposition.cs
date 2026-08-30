using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Albums;
using Vistara.Api.Features.Assets;
using Vistara.Api.Features.Favorites;
using Vistara.Api.Features.Lifecycle;
using Vistara.Api.Features.Shares;
using Vistara.Application.Gallery.Albums;
using Vistara.Application.Gallery.Favorites;
using Vistara.Application.Gallery.Queries;
using Vistara.Application.Gallery.Tags;
using Vistara.Application.Lifecycle;
using Vistara.Application.Sharing;
using Vistara.Auth.Sharing;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Curation;
using Vistara.Persistence.Gallery.Queries;
using Vistara.Persistence.Lifecycle;
using Vistara.Persistence.Sharing;

namespace Vistara.Api.Composition.Gallery;

public static class GalleryServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraGallery(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                AssetEndpointMapping.AssetQueryPolicyName,
                policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(
                GalleryCurationEndpointSupport.PolicyName,
                policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(
                LifecycleEndpointMapping.LifecyclePolicyName,
                policy => policy.RequireAuthenticatedUser());
        });
        services.AddHttpContextAccessor();
        services.AddGalleryQueryNormalization();
        ServiceDescriptor? rateLimit = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IPlatformRateLimitHook));
        if (rateLimit?.ImplementationType ==
            typeof(PlatformRateLimitPersistenceAdapter))
        {
            services.RemoveAll<IPlatformRateLimitHook>();
            services.TryAddScoped<PlatformRateLimitPersistenceAdapter>();
            services.AddSingleton<
                IPlatformRateLimitHook,
                GalleryScopedPlatformRateLimitHook>();
        }

        services.TryAddSingleton(static provider =>
            GalleryKeyMaterial.Create(
                provider.GetRequiredService<IOptions<PlatformOptions>>().Value));
        services.TryAddSingleton(static provider =>
            new AssetCursorProtector(
                provider.GetRequiredService<GalleryKeyMaterial>().AssetCursorKey));
        services.TryAddSingleton<ILifecycleCursorCodec>(static provider =>
            new LifecycleCursorCodec(
                provider.GetRequiredService<GalleryKeyMaterial>().LifecycleCursorKey));

        services.TryAddScoped<RelationalAssetQueryStore>();
        services.TryAddScoped<IAssetQueryStore>(static provider =>
            provider.GetRequiredService<RelationalAssetQueryStore>());
        services.TryAddScoped<IAssetQueryService, AssetQueryService>();
        services.TryAddScoped<
            IAssetQueryAuthorizationPort,
            GalleryAssetQueryAuthorizationPort>();

        services.TryAddScoped<RelationalGalleryCurationStore>();
        services.TryAddScoped<IAlbumCurationStore>(static provider =>
            provider.GetRequiredService<RelationalGalleryCurationStore>());
        services.TryAddScoped<ITagCurationStore>(static provider =>
            provider.GetRequiredService<RelationalGalleryCurationStore>());
        services.TryAddScoped<IFavoriteCurationStore>(static provider =>
            provider.GetRequiredService<RelationalGalleryCurationStore>());
        services.TryAddScoped<IAlbumApplication, AlbumApplication>();
        services.TryAddScoped<ITagApplication, TagApplication>();
        services.TryAddScoped<IFavoriteApplication, FavoriteApplication>();
        services.TryAddScoped<
            IGalleryCurationAuthorizationPort,
            GalleryCurationAuthorizationPort>();

        AddSharingPersistence(services, configuration);
        services.TryAddSingleton<IShareRandomSource, CryptographicShareRandomSource>();
        services.TryAddSingleton<ISharePepperProvider>(static provider =>
            provider.GetRequiredService<GalleryKeyMaterial>().SharePeppers);
        services.TryAddSingleton<IShareTokenProtector, ShareTokenProtector>();
        services.TryAddSingleton<IShareSessionProtector, ShareSessionProtector>();
        services.TryAddSingleton<ISharePasswordHasher, Pbkdf2SharePasswordHasher>();
        services.TryAddSingleton<IShareCursorProtector, ShareCursorProtector>();
        services.TryAddSingleton(
            new ShareOptions(
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(5),
                challengeLimit: 5));
        services.TryAddScoped<IShareStore, RelationalShareStore>();
        services.TryAddScoped<
            IShareChallengeRateLimiter,
            RelationalShareChallengeRateLimiter>();
        services.TryAddScoped<IShareAssetCatalog, GalleryShareAssetCatalog>();
        services.TryAddScoped<IShareAuditSink, GalleryShareAuditSink>();
        services.TryAddScoped<IShareAuthorizationPort, GalleryShareAuthorizationPort>();
        services.TryAddScoped<ShareService>();

        services.TryAddScoped<ILifecycleStore, RelationalLifecycleStore>();
        services.TryAddScoped<LifecycleService>();
        services.TryAddScoped<
            ILifecycleAuthorizationPort,
            GalleryLifecycleAuthorizationPort>();
        return services;
    }

    public static IServiceProvider ValidateVistaraGalleryComposition(
        this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using IServiceScope scope = services.CreateScope();
        IServiceProvider scoped = scope.ServiceProvider;
        _ = scoped.GetRequiredService<IAssetQueryAuthorizationPort>();
        _ = scoped.GetRequiredService<IAssetQueryService>();
        _ = scoped.GetRequiredService<IGalleryCurationAuthorizationPort>();
        _ = scoped.GetRequiredService<IAlbumApplication>();
        _ = scoped.GetRequiredService<ITagApplication>();
        _ = scoped.GetRequiredService<IFavoriteApplication>();
        _ = scoped.GetRequiredService<IShareAuthorizationPort>();
        _ = scoped.GetRequiredService<ShareService>();
        _ = scoped.GetRequiredService<ILifecycleAuthorizationPort>();
        _ = scoped.GetRequiredService<LifecycleService>();
        _ = scoped.GetRequiredService<ILifecycleCursorCodec>();
        return services;
    }

    private static void AddSharingPersistence(
        IServiceCollection services,
        IConfiguration configuration)
    {
        string? providerName = configuration["Persistence:Provider"];
        string? connectionString =
            configuration.GetConnectionString("Vistara") ??
            configuration["Persistence:ConnectionString"];
        if (!Enum.TryParse(
                providerName,
                ignoreCase: true,
                out VistaraDatabaseProvider provider) ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Gallery sharing requires the configured Vistara persistence provider.");
        }

        services.AddDbContext<SharingDbContext>(options =>
        {
            if (provider == VistaraDatabaseProvider.Sqlite)
            {
                options.UseSqlite(connectionString);
            }
            else
            {
                options.UseNpgsql(connectionString);
            }
        });
    }
}

internal sealed class GalleryScopedPlatformRateLimitHook(
    IServiceScopeFactory scopeFactory) : IPlatformRateLimitHook
{
    public async ValueTask<PlatformRateLimitDecision> CheckAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<PlatformRateLimitPersistenceAdapter>()
            .CheckAsync(context, cancellationToken);
    }
}

internal sealed record GalleryKeyMaterial(
    byte[] AssetCursorKey,
    byte[] LifecycleCursorKey,
    SharePepperSet SharePeppers)
{
    internal static GalleryKeyMaterial Create(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var derivedPeppers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[]? assetCursorKey = null;
        byte[]? lifecycleCursorKey = null;
        foreach ((string version, string encodedSecret) in
                 options.Authentication.ApiKeys.Peppers)
        {
            byte[] secret = Convert.FromBase64String(encodedSecret);
            try
            {
                derivedPeppers.Add(
                    version,
                    Derive(secret, "vistara.gallery.shares.v1"));
                if (string.Equals(
                        version,
                        options.Authentication.ApiKeys.CurrentPepperVersion,
                        StringComparison.Ordinal))
                {
                    assetCursorKey =
                        Derive(secret, "vistara.gallery.asset-cursor.v1");
                    lifecycleCursorKey =
                        Derive(secret, "vistara.gallery.lifecycle-cursor.v1");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        if (assetCursorKey is null || lifecycleCursorKey is null)
        {
            throw new InvalidOperationException(
                "The current platform pepper is required for gallery key derivation.");
        }

        return new GalleryKeyMaterial(
            assetCursorKey,
            lifecycleCursorKey,
            new SharePepperSet(
                options.Authentication.ApiKeys.CurrentPepperVersion!,
                derivedPeppers));
    }

    private static byte[] Derive(ReadOnlySpan<byte> secret, string label) =>
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(label));
}
