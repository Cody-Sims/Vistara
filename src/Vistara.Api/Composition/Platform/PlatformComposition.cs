using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vistara.Api.Features.Derivatives;
using Vistara.Api.Features.Events;
using Vistara.Api.Features.Media;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Common;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.ApiKeys;
using Vistara.Auth.Cookies;
using Vistara.Auth.Delivery;
using Vistara.Auth.Jwt;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Uploads;

namespace Vistara.Api.Composition.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraApiPlatform(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PlatformOptions>()
            .Bind(configuration.GetSection(PlatformOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PlatformOptions>,
                PlatformOptionsValidator>());

        // The persisted rate-limit ceiling is bound and bounded here rather
        // than compiled in, because the same limit is a per-client budget on a
        // Compose host and a deployment-wide ceiling behind a shared ingress.
        // The declared partition is checked against the security composition,
        // whose framework limiter counts the same peer, so a deployment that
        // raised one ceiling and not the other fails the host instead of
        // failing at the first request.
        services.AddOptions<PlatformRateLimitOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<PlatformRateLimitOptions>>(
                new PlatformRateLimitOptionsSetup(configuration)));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PlatformRateLimitOptions>,
                PlatformRateLimitOptionsValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PlatformRateLimitOptions>,
                PlatformRateLimitCouplingValidator>());
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme =
                    PlatformAuthenticationDefaults.SelectorScheme;
                options.DefaultChallengeScheme =
                    PlatformAuthenticationDefaults.SelectorScheme;
                options.DefaultForbidScheme =
                    PlatformAuthenticationDefaults.SelectorScheme;
            })
            .AddPolicyScheme(
                PlatformAuthenticationDefaults.SelectorScheme,
                PlatformAuthenticationDefaults.SelectorScheme,
                options => options.ForwardDefaultSelector =
                    context => PlatformAuthenticationSelector.Select(context.Request))
            .AddScheme<AuthenticationSchemeOptions, PlatformCookieAuthenticationHandler>(
                PlatformAuthenticationDefaults.CookieScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, PlatformApiKeyAuthenticationHandler>(
                PlatformAuthenticationDefaults.ApiKeyScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, PlatformBearerAuthenticationHandler>(
                PlatformAuthenticationDefaults.BearerScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, PlatformConfusedAuthenticationHandler>(
                PlatformAuthenticationDefaults.ConfusedScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, PlatformAnonymousAuthenticationHandler>(
                PlatformAuthenticationDefaults.AnonymousScheme,
                _ => { });

        services.TryAddScoped<PlatformTenantContext>();
        services.TryAddScoped<IPlatformTenantContext>(
            static provider => provider.GetRequiredService<PlatformTenantContext>());
        services.TryAddScoped<ITenantScope>(
            static provider => provider.GetRequiredService<PlatformTenantContext>());
        services.TryAddScoped<IMutableTenantScope>(
            static provider => provider.GetRequiredService<PlatformTenantContext>());
        services.TryAddSingleton<IPlatformRateLimitHook, PermitAllPlatformRateLimitHook>();
        services.TryAddScoped<
            IPlatformCookieAuthenticator,
            DefaultPlatformCookieAuthenticator>();
        services.TryAddScoped<
            IPlatformApiKeyAuthenticator,
            DefaultPlatformApiKeyAuthenticator>();
        services.TryAddScoped<
            IPlatformBearerAuthenticator,
            DefaultPlatformBearerAuthenticator>();

        services.TryAddSingleton<IClock>(
            Vistara.Application.Common.SystemClock.Instance);
        services.TryAddSingleton<IUuid7Generator, Uuid7Generator>();
        services.TryAddSingleton(new CookieAuthOptions());
        services.TryAddSingleton<CookieAntiforgeryPolicy>();
        services.TryAddSingleton<ICookieTokenSource, CryptographicCookieTokenSource>();
        services.TryAddSingleton<IApiKeyDigestComparer, FixedTimeApiKeyDigestComparer>();
        services.TryAddSingleton<IDeliveryGrantDigestComparer,
            FixedTimeDeliveryGrantDigestComparer>();
        services.TryAddSingleton<IDeliveryGrantRandomSource,
            CryptographicDeliveryGrantRandomSource>();
        services.TryAddSingleton(DeliveryGrantOptions.Default);
        services.TryAddSingleton<IApiKeyPepperProvider>(static provider =>
            PlatformConfiguration.CreatePepperSet(
                provider.GetRequiredService<IOptions<PlatformOptions>>().Value));
        services.TryAddSingleton<IDeliveryGrantPepperProvider>(static provider =>
            PlatformConfiguration.CreateDeliveryPepperSet(
                provider.GetRequiredService<IOptions<PlatformOptions>>().Value));
        services.AddHttpClient(
            PlatformJwtMetadataSigningKeyResolver.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10));
        services.TryAddSingleton<IJwtMetadataSigningKeyResolver,
            PlatformJwtMetadataSigningKeyResolver>();
        services.TryAddSingleton<PlatformDerivativePresetCatalog>();

        // Browser sessions are always built through the login session factory,
        // which binds one explicit tenant. A cookie session manager fed by the
        // ambient request scope cannot work here: authentication runs before
        // any tenant context exists, and it would resolve no tenant at all.
        // The factory is registered through a delegate so a composition root
        // without persistence still builds, exactly as before.
        services.TryAddScoped(static provider =>
            new PlatformLoginSessionFactory(
                provider.GetRequiredService<TenantDbContextFactory>(),
                provider.GetRequiredService<AuthenticationCatalogDbContext>(),
                provider.GetRequiredService<JwtRevocationCatalogDbContext>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IUuid7Generator>(),
                provider.GetRequiredService<ICookieTokenSource>(),
                provider.GetRequiredService<CookieAuthOptions>(),
                provider.GetRequiredService<ILogger<PlatformCookieAuthAuditSink>>()));
        services.TryAddScoped(static provider =>
            new ApiKeyAuthenticator(
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IApiKeyPepperProvider>(),
                provider.GetRequiredService<IApiKeyStore>(),
                provider.GetRequiredService<IApiKeyDigestComparer>(),
                provider.GetRequiredService<IApiKeyAuditSink>()));
        services.TryAddScoped(static provider =>
            new JwtAuthenticator(
                PlatformConfiguration.CreateIssuerProfiles(
                    provider.GetRequiredService<IOptions<PlatformOptions>>().Value),
                provider.GetRequiredService<IJwtMetadataSigningKeyResolver>(),
                provider.GetRequiredService<IJwtTenantMembershipProvider>(),
                provider.GetRequiredService<IJwtRevocationStore>(),
                provider.GetRequiredService<IClock>()));
        services.AddVistaraApiOidc(configuration);
        services.AddVistaraApiUploads();
        return services;
    }

    public static IServiceCollection AddVistaraApiPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

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
                "An explicit supported persistence provider and connection string are required.");
        }

        services.AddVistaraPersistence(options =>
        {
            options.Provider = provider;
            options.ConnectionString = connectionString;
        });
        bool hasRateLimitOverride = services.Any(descriptor =>
            descriptor.ServiceType == typeof(IPlatformRateLimitHook) &&
            descriptor.ImplementationType !=
                typeof(PermitAllPlatformRateLimitHook));
        if (!hasRateLimitOverride)
        {
            services.RemoveAll<IPlatformRateLimitHook>();
            services.AddScoped<IPlatformRateLimitHook,
                PlatformRateLimitPersistenceAdapter>();
        }

        services.TryAddScoped<ICookieSessionStore, PlatformCookieSessionStore>();
        services.TryAddScoped<ICookieAuthAuditSink, PlatformCookieAuthAuditSink>();
        services.TryAddScoped<IApiKeyStore, PlatformApiKeyStore>();
        services.TryAddScoped<IApiKeyAuditSink, PlatformApiKeyAuditSink>();
        services.TryAddScoped<IJwtTenantMembershipProvider,
            PlatformJwtTenantMembershipProvider>();
        services.TryAddScoped<IJwtRevocationStore, PlatformJwtRevocationStore>();
        services.TryAddScoped<IDeliveryGrantStore, PlatformDeliveryGrantStore>();
        services.TryAddScoped<IDeliveryGrantAuthorizationPort,
            PlatformDeliveryGrantAuthorizationPort>();
        services.TryAddScoped<IDeliveryGrantAuditSink,
            PlatformDeliveryGrantAuditSink>();
        services.TryAddScoped<DeliveryGrantValidator>(static serviceProvider =>
            new DeliveryGrantValidator(
                serviceProvider.GetRequiredService<IClock>(),
                serviceProvider.GetRequiredService<IDeliveryGrantPepperProvider>(),
                serviceProvider.GetRequiredService<IDeliveryGrantStore>(),
                serviceProvider.GetRequiredService<IDeliveryGrantAuthorizationPort>(),
                serviceProvider.GetRequiredService<IDeliveryGrantDigestComparer>(),
                serviceProvider.GetRequiredService<IDeliveryGrantAuditSink>(),
                serviceProvider.GetRequiredService<DeliveryGrantOptions>()));
        services.TryAddScoped<IMediaDeliveryAuthorizationPort,
            PlatformMediaDeliveryAuthorizationPort>();
        services.TryAddScoped<IMediaDeliveryApplicationPort,
            PlatformMediaDeliveryApplicationPort>();
        services.TryAddScoped<IDerivativeAuthorizationPort,
            PlatformDerivativeAuthorizationPort>();
        services.TryAddScoped<IDerivativeApplicationPort,
            PlatformDerivativePersistenceAdapter>();
        services.TryAddScoped<IEventStreamAuthorizationPort,
            PlatformEventStreamAuthorizationPort>();
        services.TryAddScoped<IEventStreamSource, PlatformEventStreamSource>();
        bool hasOverride = services.Any(descriptor =>
            descriptor.ServiceType == typeof(IUploadApplicationPort) &&
            descriptor.ImplementationType != typeof(
                PlatformUploadServiceCollectionExtensions
                    .MissingUploadApplicationPort));
        if (!hasOverride)
        {
            services.RemoveAll<IUploadApplicationPort>();
            services.AddScoped<
                IUploadApplicationPort,
                PlatformUploadPersistenceAdapter>();
        }

        return services;
    }
}

public static class PlatformEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapVistaraPlatformEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health/live", static () => Results.NoContent())
            .AllowAnonymous();
        endpoints.MapVistaraMedia();
        endpoints.MapVistaraDerivatives();
        endpoints.MapVistaraEventStream();
        endpoints.MapVistaraUploads();
        return endpoints;
    }
}
