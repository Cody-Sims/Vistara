using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Admin;
using Vistara.Api.Features.ApiKeys;
using Vistara.Api.Features.Tenants;
using Vistara.Application.Common.Storage;
using Vistara.Auth.Cookies;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Verifies that the platform and account surface can be wired by a
/// composition root without editing the platform composition file: services
/// resolve, policies exist, and the bootstrap routes authenticate anonymously.
/// </summary>
public sealed class AccountSurfaceCompositionTests
{
    [Fact]
    public void The_account_surface_resolves_every_service_it_registers()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddVistaraAccountSurface());

        using IServiceScope scope = provider.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<IAccountAuthorizationPort>());
        Assert.NotNull(services.GetRequiredService<ILocalCredentialVerifier>());
        Assert.NotNull(services.GetRequiredService<IBrowserSessionPort>());
        Assert.NotNull(services.GetRequiredService<IFirstOwnerProvisioningPort>());
        Assert.NotNull(services.GetRequiredService<IFirstOwnerProvisioningGuard>());
        Assert.NotNull(services.GetRequiredService<ILocalPasswordHasher>());
        Assert.NotNull(services.GetRequiredService<DummyLocalPasswordVerifier>());
        Assert.NotNull(services.GetRequiredService<CookieAuthOptions>());
        Assert.NotNull(services.GetRequiredService<RelationalIdentityCatalog>());
        Assert.NotNull(
            services.GetRequiredService<RelationalFirstOwnerProvisioningStore>());
    }

    [Fact]
    public void Tenant_and_api_key_administration_resolve_their_ports()
    {
        using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddVistaraAccountSurface();
            services.AddVistaraTenantAdministration();
            services.AddVistaraAdministration();
            services.AddSingleton<IBlobStore>(
                new AccountSurfaceHarness.ReachableBlobStore());
        });

        using IServiceScope scope = provider.CreateScope();
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<ITenantDirectoryPort>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAdminPort>());
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<IUserPreferencesPort>());
    }

    [Fact]
    public void A_host_may_replace_any_account_service_before_registration()
    {
        var guard = new RecordingGuard();
        using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddSingleton<IFirstOwnerProvisioningGuard>(guard);
            services.AddVistaraAccountSurface();
        });

        using IServiceScope scope = provider.CreateScope();
        Assert.Same(
            guard,
            scope.ServiceProvider.GetRequiredService<IFirstOwnerProvisioningGuard>());
    }

    [Fact]
    public async Task Every_platform_surface_policy_is_registered()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddVistaraPlatformSurfacePolicies());
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (string name in PlatformSurfacePolicies.All)
        {
            AuthorizationPolicy? policy = await policies.GetPolicyAsync(name);
            Assert.NotNull(policy);
            Assert.Contains(
                policy.Requirements,
                requirement => requirement is DenyAnonymousAuthorizationRequirement);
        }
    }

    [Fact]
    public void The_platform_surface_maps_every_account_route()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddVistaraPersistence(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = "Data Source=:memory:";
        });
        builder.Services.AddVistaraAccountSurface();
        WebApplication app = builder.Build();

        app.MapVistaraAccount();

        string[] routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToArray();
        Assert.Contains("/api/v1/me", routes);
        Assert.All(
            AccountEndpointMapping.AnonymousRoutes,
            route => Assert.Contains(route, routes));
    }

    [Theory]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/api/v1/setup")]
    public void Bootstrap_routes_select_the_anonymous_scheme_despite_a_stale_cookie(
        string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.Headers.Cookie = "__Host-vistara-session=stale-token";

        string scheme = PlatformAuthenticationSelector.Select(context.Request);

        Assert.Equal(PlatformAuthenticationDefaults.AnonymousScheme, scheme);
    }

    [Fact]
    public void Bootstrap_routes_ignore_presented_tokens_and_never_report_confusion()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/auth/login";
        context.Request.Headers.Cookie = "__Host-vistara-session=stale-token";
        context.Request.Headers.Authorization = "Bearer token";
        context.Request.Headers["X-API-Key"] = "vst_key";

        string scheme = PlatformAuthenticationSelector.Select(context.Request);

        Assert.Equal(PlatformAuthenticationDefaults.AnonymousScheme, scheme);
    }

    [Fact]
    public void Guarded_routes_still_select_the_cookie_scheme()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/me";
        context.Request.Headers.Cookie = "__Host-vistara-session=live-token";

        string scheme = PlatformAuthenticationSelector.Select(context.Request);

        Assert.Equal(PlatformAuthenticationDefaults.CookieScheme, scheme);
    }

    [Fact]
    public void A_get_to_a_bootstrap_path_is_not_treated_as_anonymous()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/setup";
        context.Request.Headers.Cookie = "__Host-vistara-session=live-token";

        string scheme = PlatformAuthenticationSelector.Select(context.Request);

        Assert.Equal(PlatformAuthenticationDefaults.CookieScheme, scheme);
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddVistaraPersistence(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = "Data Source=:memory:";
        });
        services.AddScoped<AccountSurfaceHarness.AmbientTenantScope>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<AccountSurfaceHarness.AmbientTenantScope>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<AccountSurfaceHarness.AmbientTenantScope>());
        configure(services);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class RecordingGuard : IFirstOwnerProvisioningGuard
    {
        public ValueTask BeforeCommitAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
