using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vistara.Api.Features.Admin;
using Vistara.Api.Features.Oidc;
using Vistara.Application.Common;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Vistara.Persistence.Auth;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Composes the hosted OpenID Connect entry point: validated provider
/// configuration, the transport every provider call must use, the client
/// credential sources, and the adapters behind the browser routes.
///
/// Configuration is bound and validated whether or not hosted sign-in is
/// switched on, so a broken provider fails the host either way. What is
/// composed does depend on it: a deployment with no configured provider gets
/// no sign-in adapters, no login request store, and no routes, which is why
/// adding the platform to a Compose deployment does not drag the identity
/// catalog and the browser session graph in behind it.
/// </summary>
public static class PlatformOidcServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraApiOidc(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PlatformOidcOptions>()
            .Bind(configuration.GetSection(PlatformOidcOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PlatformOidcOptions>,
                PlatformOidcOptionsValidator>());
        services.AddOptions<PlatformBootstrapOptions>()
            .Bind(configuration.GetSection(PlatformBootstrapOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PlatformBootstrapOptions>,
                PlatformBootstrapOptionsValidator>());

        // Redirects are disabled in the handler and re-checked on every
        // response by the library. The client budget is deliberately longer
        // than the per-provider budget so an elapsed provider call is
        // attributed to the provider instead of surfacing as a transport
        // fault, and the handler lifetime is infinite because connection
        // rotation is already bounded by PooledConnectionLifetime.
        services.AddHttpClient(OidcHttpDefaults.HttpClientName)
            .ConfigureHttpClient(static (provider, client) =>
                client.Timeout = PlatformOidcConfiguration.CreateHttpClientTimeout(
                    provider.GetRequiredService<IOptions<PlatformOidcOptions>>().Value))
            .ConfigurePrimaryHttpMessageHandler(OidcHttpDefaults.CreateHandler)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.TryAddSingleton<IOidcRandomSource, CryptographicOidcRandomSource>();
        services.TryAddSingleton<IOidcManagedIdentityTokenSource,
            ManagedIdentityOidcTokenSource>();
        services.TryAddSingleton<IOidcAuditSink, PlatformOidcAuditSink>();
        services.TryAddSingleton<IOidcHandleProtector, DataProtectionOidcHandleProtector>();
        services.TryAddSingleton(static provider =>
            PlatformOidcConfiguration.CreateFirstOwnerPolicy(
                provider.GetRequiredService<IOptions<PlatformBootstrapOptions>>().Value));
        services.TryAddSingleton<PlatformOidcProviderRegistry>();
        services.TryAddSingleton<IOidcProviderCatalog>(static provider =>
            provider.GetRequiredService<PlatformOidcProviderRegistry>());
        if (!HasConfiguredProvider(configuration))
        {
            return services;
        }

        services.AddVistaraOidcRouting();
        services.TryAddScoped<RelationalOidcLoginRequestStore>();
        services.TryAddScoped<IOidcLoginPort, PlatformOidcLoginAdapter>();
        services.TryAddScoped<IOidcSignOutPort, PlatformOidcSignOutAdapter>();
        return services;
    }

    /// <summary>
    /// Reads the switch and the provider list straight from configuration,
    /// because the decision to compose the sign-in graph has to be taken while
    /// the service collection is still being built. Nothing is validated here;
    /// a malformed provider still fails through the options validator.
    /// </summary>
    private static bool HasConfiguredProvider(IConfiguration configuration)
    {
        var configured = new PlatformOidcOptions();
        configuration.GetSection(PlatformOidcOptions.SectionName).Bind(configured);
        return configured.Enabled && configured.Providers.Count > 0;
    }

    /// <summary>
    /// Fails the host when the hosted sign-in graph is incomplete or when the
    /// bootstrap allowlist names a directory no configured provider accepts. A
    /// bootstrap pointing at the wrong tenant would otherwise look healthy
    /// until the one sign-in that mattered was refused.
    /// </summary>
    public static IServiceProvider ValidateVistaraApiOidcComposition(
        this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A composition root that never added the hosted entry point has
        // nothing to validate; it simply has no OIDC surface.
        if (services.GetService<PlatformOidcProviderRegistry>() is not { } registry)
        {
            return services;
        }

        PlatformFirstOwnerPolicy policy =
            services.GetRequiredService<PlatformFirstOwnerPolicy>();
        if (policy.IsEnabled)
        {
            PlatformOidcProvider? provider = registry.Find(policy.ProviderId);
            if (provider is null)
            {
                throw new InvalidOperationException(
                    "External first-owner bootstrap names OIDC provider "
                    + $"'{policy.ProviderId}', which is not configured.");
            }

            if (provider.Options.DirectoryTenantId != policy.DirectoryTenantId)
            {
                throw new InvalidOperationException(
                    "The external first-owner directory tenant does not match the "
                    + $"directory tenant configured for OIDC provider '{policy.ProviderId}'.");
            }
        }

        if (registry.Providers.Count == 0)
        {
            return services;
        }

        // The setup surface registers an empty catalog as a fallback, so a
        // composition root that mapped the surface before adding the hosted
        // entry point would advertise no provider while the routes worked.
        // That is a silent first-run failure, so it fails the host instead.
        if (!ReferenceEquals(
                services.GetRequiredService<IOidcProviderCatalog>(),
                registry))
        {
            throw new InvalidOperationException(
                "The hosted sign-in provider catalog is not the configured OIDC "
                + "registry. Add the platform composition before the platform "
                + "surface so the surface does not bind the empty catalog.");
        }

        using IServiceScope scope = services.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IOidcLoginPort>();
        _ = scope.ServiceProvider.GetRequiredService<IOidcSignOutPort>();
        _ = scope.ServiceProvider.GetRequiredService<RelationalOidcLoginRequestStore>();
        return services;
    }
}

/// <summary>
/// The per-provider protocol graph. One instance exists for the lifetime of
/// the process so discovery documents and signing keys are fetched once and
/// shared, rather than once per sign-in.
/// </summary>
public sealed class PlatformOidcProviderRegistry : IOidcProviderCatalog, IDisposable
{
    private readonly Dictionary<string, PlatformOidcProvider> _providers;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public PlatformOidcProviderRegistry(
        IOptions<PlatformOidcOptions> options,
        IHttpClientFactory httpClientFactory,
        IOidcManagedIdentityTokenSource managedIdentity,
        IOidcRandomSource randomSource,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(managedIdentity);
        ArgumentNullException.ThrowIfNull(randomSource);
        ArgumentNullException.ThrowIfNull(clock);

        _httpClient = httpClientFactory.CreateClient(OidcHttpDefaults.HttpClientName);
        _providers = new Dictionary<string, PlatformOidcProvider>(StringComparer.Ordinal);
        foreach (PlatformOidcProviderRegistration registration in
                 PlatformOidcConfiguration.CreateProviders(options.Value))
        {
            var metadata = new OidcMetadataCache(
                _httpClient,
                registration.Options,
                clock);
            var credentials = new OidcClientCredentialResolver(
                registration.ManagedIdentityClientId is null
                    ? null
                    : new ManagedIdentityOidcClientAssertionProvider(
                        managedIdentity,
                        registration.ManagedIdentityClientId),
                registration.ClientSecret is null
                    ? null
                    : new ConfiguredOidcClientSecretProvider(registration.ClientSecret));
            _providers[registration.ProviderId] = new PlatformOidcProvider(
                registration.ProviderId,
                registration.DisplayName,
                registration.Options,
                metadata,
                new OidcTokenClient(_httpClient, registration.Options, credentials, clock),
                new OidcIdTokenValidator(registration.Options, clock),
                new OidcLoginRequestFactory(registration.Options, randomSource, clock));
        }

        Providers = [.. _providers.Values];
    }

    public IReadOnlyList<PlatformOidcProvider> Providers { get; }

    /// <summary>
    /// The capabilities a first-run client may see. Only the provider key and
    /// the route to start with are published.
    /// </summary>
    IReadOnlyList<OidcProviderCapability> IOidcProviderCatalog.Providers =>
        [.. Providers.Select(provider => new OidcProviderCapability(
            provider.ProviderId,
            provider.DisplayName,
            OidcRoutes.StartPath(provider.ProviderId)))];

    public PlatformOidcProvider? Find(string? providerId) =>
        providerId is not null &&
        _providers.TryGetValue(providerId, out PlatformOidcProvider? provider)
            ? provider
            : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (PlatformOidcProvider provider in Providers)
        {
            provider.Dispose();
        }

        _httpClient.Dispose();
    }
}

/// <summary>One configured provider and everything one sign-in needs from it.</summary>
public sealed class PlatformOidcProvider : IDisposable
{
    private readonly OidcMetadataCache _metadata;

    internal PlatformOidcProvider(
        string providerId,
        string displayName,
        OidcProviderOptions options,
        OidcMetadataCache metadata,
        OidcTokenClient tokenClient,
        OidcIdTokenValidator validator,
        OidcLoginRequestFactory loginRequests)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        Options = options;
        _metadata = metadata;
        TokenClient = tokenClient;
        Validator = validator;
        LoginRequests = loginRequests;
    }

    public string ProviderId { get; }

    /// <summary>The label a first-run client renders for this provider.</summary>
    public string DisplayName { get; }

    public OidcProviderOptions Options { get; }

    public IOidcMetadataProvider Metadata => _metadata;

    internal OidcTokenClient TokenClient { get; }

    internal OidcIdTokenValidator Validator { get; }

    internal OidcLoginRequestFactory LoginRequests { get; }

    public void Dispose() => _metadata.Dispose();
}

/// <summary>
/// Mints the managed-identity token that becomes the federated client
/// assertion. The seam exists so the composition can be exercised without an
/// Azure instance metadata endpoint; production always resolves the Azure
/// implementation.
/// </summary>
public interface IOidcManagedIdentityTokenSource
{
    ValueTask<string?> GetClientAssertionAsync(
        string managedIdentityClientId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Uses the user-assigned managed identity explicitly. <c>DefaultAzureCredential</c>
/// is deliberately not used: it probes developer credentials, environment
/// variables, and the shared token cache, so a misconfigured deployment could
/// silently authenticate as an operator instead of failing.
/// </summary>
public sealed class ManagedIdentityOidcTokenSource : IOidcManagedIdentityTokenSource
{
    /// <summary>The audience Entra requires for a federated credential exchange.</summary>
    public const string TokenExchangeScope = "api://AzureADTokenExchange/.default";

    private readonly Dictionary<string, TokenCredential> _credentials =
        new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public async ValueTask<string?> GetClientAssertionAsync(
        string managedIdentityClientId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedIdentityClientId);
        TokenCredential credential = Resolve(managedIdentityClientId);
        AccessToken token = await credential
            .GetTokenAsync(
                new TokenRequestContext([TokenExchangeScope]),
                cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token.Token) ? null : token.Token;
    }

    private TokenCredential Resolve(string managedIdentityClientId)
    {
        lock (_gate)
        {
            if (!_credentials.TryGetValue(managedIdentityClientId, out TokenCredential? credential))
            {
                credential = new ManagedIdentityCredential(
                    ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
                _credentials[managedIdentityClientId] = credential;
            }

            return credential;
        }
    }
}

/// <summary>
/// Presents the managed-identity token as an OAuth client assertion. Returning
/// null means the secretless path produced nothing, which lets the resolver
/// fall back to a configured secret rather than attempting an anonymous token
/// request.
/// </summary>
internal sealed class ManagedIdentityOidcClientAssertionProvider(
    IOidcManagedIdentityTokenSource tokenSource,
    string managedIdentityClientId) : IOidcClientAssertionProvider
{
    public async ValueTask<OidcClientAssertion?> GetAssertionAsync(
        Uri tokenEndpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        string? assertion = await tokenSource
            .GetClientAssertionAsync(managedIdentityClientId, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(assertion)
            ? null
            : new OidcClientAssertion(assertion);
    }
}

/// <summary>
/// The explicit application-secret fallback. The value only ever leaves the
/// redacted holder to build one token request body.
/// </summary>
internal sealed class ConfiguredOidcClientSecretProvider(RedactedSecret secret) :
    IOidcClientSecretProvider
{
    public ValueTask<string?> GetSecretAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(secret.Reveal());
    }
}
