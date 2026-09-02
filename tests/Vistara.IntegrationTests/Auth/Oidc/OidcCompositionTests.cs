using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Oidc;
using Vistara.Application.Common;
using Vistara.Auth.Oidc;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Oidc;

/// <summary>
/// Composition-time behaviour of hosted sign-in.
///
/// Everything here is a startup failure by design. A deployment that would
/// accept the wrong directory, serve a reply URL the provider never
/// registered, hold an empty or misdirected bootstrap allowlist, or run with
/// no client credential must refuse to start rather than fail at the one
/// sign-in that mattered.
/// </summary>
public sealed class OidcCompositionTests
{
    private static readonly Guid DirectoryTenantId =
        Guid.Parse("2c1a5b6e-4f10-4d0b-9c2a-9a1f3b7e5d01");

    private static readonly Guid OtherDirectoryTenantId =
        Guid.Parse("7f9d0a11-2b33-4c55-8677-99aabbccdd01");

    private const string ClientId = "b7d3f210-8e44-4b6a-9c31-0d5a7e2f4c88";

    private const string ObjectId = "11111111-2222-3333-4444-555555555502";

    [Fact]
    public void A_configured_provider_is_published_with_only_its_button_fields()
    {
        using ServiceProvider services = Compose(Provider());

        var catalog = services.GetRequiredService<IOidcProviderCatalog>();

        OidcProviderCapability capability = Assert.Single(catalog.Providers);
        Assert.Equal("entra", capability.ProviderId);
        Assert.Equal("Microsoft Entra ID", capability.DisplayName);
        Assert.Equal("/api/v1/auth/oidc/entra/start", capability.StartPath);
    }

    /// <summary>
    /// Hosted sign-in is off unless an operator turns it on, so a Compose
    /// deployment keeps local password sign-in and publishes no provider.
    /// </summary>
    [Fact]
    public void An_unconfigured_deployment_publishes_no_provider()
    {
        using ServiceProvider services = Compose([]);

        Assert.Empty(services.GetRequiredService<IOidcProviderCatalog>().Providers);
        services.ValidateVistaraApiOidcComposition();
    }

    /// <summary>
    /// A provider parked behind a disabled switch is still validated, so it
    /// cannot be discovered to be broken on the day it is enabled.
    /// </summary>
    [Fact]
    public void A_disabled_switch_composes_nothing_but_still_validates()
    {
        Dictionary<string, string?> disabled = Provider();
        disabled["Platform:Authentication:Oidc:Enabled"] = "false";

        using ServiceProvider healthy = Compose(disabled);
        Assert.Empty(healthy.GetRequiredService<IOidcProviderCatalog>().Providers);

        disabled["Platform:Authentication:Oidc:Providers:0:ClientId"] = "not-a-guid";
        using ServiceProvider broken = Compose(disabled);
        AssertRejects(
            PlatformOidcOptions.SectionName,
            () => broken.GetRequiredService<IOidcProviderCatalog>());
    }

    [Theory]
    [InlineData("Platform:Authentication:Oidc:Providers:0:ClientId", "not-a-guid")]
    [InlineData("Platform:Authentication:Oidc:Providers:0:TenantId", "")]
    [InlineData("Platform:Authentication:Oidc:Providers:0:ProviderId", "okta")]
    [InlineData("Platform:Authentication:Oidc:Providers:0:DisplayName", "")]
    [InlineData(
        "Platform:Authentication:Oidc:Providers:0:RedirectUri",
        "https://vistara.example.test/api/v1/auth/oidc/entra/reply")]
    [InlineData(
        "Platform:Authentication:Oidc:Providers:0:RedirectUri",
        "http://vistara.example.test/api/v1/auth/oidc/entra/callback")]
    [InlineData(
        "Platform:Authentication:Oidc:Providers:0:PostLogoutRedirectUri",
        "https://vistara.example.test/goodbye")]
    [InlineData(
        "Platform:Authentication:Oidc:Providers:0:Authority",
        "https://login.microsoftonline.com/common/v2.0")]
    [InlineData("Platform:Authentication:Oidc:Providers:0:ClientSecret", "")]
    [InlineData("Platform:Authentication:Oidc:Providers:0:ManagedIdentityClientId", "one")]
    public void A_provider_that_could_accept_the_wrong_thing_fails_the_host(
        string key,
        string value)
    {
        Dictionary<string, string?> settings = Provider();
        settings[key] = value;

        using ServiceProvider services = Compose(settings);

        AssertRejects(
            PlatformOidcOptions.SectionName,
            () => services.GetRequiredService<IOidcProviderCatalog>());
    }

    [Fact]
    public void A_provider_configured_twice_fails_the_host()
    {
        Dictionary<string, string?> settings = Provider();
        foreach ((string key, string? value) in Provider())
        {
            if (key.StartsWith(
                    "Platform:Authentication:Oidc:Providers:0:",
                    StringComparison.Ordinal))
            {
                settings[key.Replace(":0:", ":1:", StringComparison.Ordinal)] = value;
            }
        }

        using ServiceProvider services = Compose(settings);

        AssertRejects(
            PlatformOidcOptions.SectionName,
            () => services.GetRequiredService<IOidcProviderCatalog>());
    }

    [Theory]
    [InlineData("Platform:Bootstrap:FirstOwner:AllowedObjectIds:0", "not-a-guid")]
    [InlineData("Platform:Bootstrap:FirstOwner:AllowedObjectIds:0", "")]
    [InlineData("Platform:Bootstrap:FirstOwner:DirectoryTenantId", "")]
    [InlineData("Platform:Bootstrap:FirstOwner:TenantSlug", "Not A Slug")]
    [InlineData("Platform:Bootstrap:FirstOwner:TenantName", "")]
    [InlineData("Platform:Bootstrap:FirstOwner:ProviderId", "okta")]
    public void A_bootstrap_allowlist_that_is_not_exact_fails_the_host(
        string key,
        string value)
    {
        Dictionary<string, string?> settings = Bootstrap(Provider());
        settings[key] = value;

        using ServiceProvider services = Compose(settings);

        AssertRejects(
            PlatformBootstrapOptions.SectionName,
            () => services.GetRequiredService<PlatformFirstOwnerPolicy>());
    }

    /// <summary>
    /// A bootstrap allowlist pointing at a directory no configured provider
    /// accepts would look healthy right up to the one sign-in that mattered,
    /// so the mismatch is a startup failure.
    /// </summary>
    [Fact]
    public void A_bootstrap_directory_no_provider_accepts_fails_the_host()
    {
        Dictionary<string, string?> settings = Bootstrap(Provider());
        settings["Platform:Bootstrap:FirstOwner:DirectoryTenantId"] =
            OtherDirectoryTenantId.ToString("D");

        using ServiceProvider services = Compose(settings);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            services.ValidateVistaraApiOidcComposition);
        Assert.Contains("directory tenant", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bootstrap_provider_that_is_not_configured_fails_the_host()
    {
        Dictionary<string, string?> settings = Bootstrap([]);

        using ServiceProvider services = Compose(settings);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            services.ValidateVistaraApiOidcComposition);
        Assert.Contains("not configured", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The setup surface registers an empty catalog as a fallback. A
    /// composition root that bound that fallback would advertise no provider
    /// while the routes worked, which is a silent first-run failure.
    /// </summary>
    [Fact]
    public void A_surface_composed_before_the_platform_fails_the_host()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IOidcProviderCatalog, EmptyOidcProviderCatalog>();
        services.AddVistaraApiOidc(
            new ConfigurationBuilder().AddInMemoryCollection(Provider()).Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            provider.ValidateVistaraApiOidcComposition);
        Assert.Contains("empty catalog", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Redirect following is a server-side request forgery primitive here: the
    /// discovery URL is validated against the configured authority before the
    /// request is issued, and a followed redirect would move the actual request
    /// somewhere that never passed that check.
    /// </summary>
    [Fact]
    public async Task A_redirecting_discovery_endpoint_is_not_followed()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        host.IdentityProvider.MetadataRedirectTo =
            $"{host.IdentityProvider.Authority.AbsoluteUri.TrimEnd('/')}/relocated-configuration";

        StartedSignIn started = await host.StartAsync();

        Assert.Equal(HttpStatusCode.Found, started.Response.StatusCode);
        Assert.Equal("/login?error=oidc_sign_in_failed", started.Response.Location);
        Assert.Null(started.HandleCookie);
    }

    /// <summary>
    /// Every configuration failure surfaces as an options validation failure
    /// naming the section, which is what turns a misconfigured deployment into
    /// a host that refuses to start.
    /// </summary>
    private static void AssertRejects(string section, Func<object> resolve)
    {
        OptionsValidationException error =
            Assert.Throws<OptionsValidationException>(() => resolve());
        Assert.Contains(
            $"{section} is invalid",
            string.Join(" ", error.Failures),
            StringComparison.Ordinal);
    }

    private static ServiceProvider Compose(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddVistaraApiOidc(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private static Dictionary<string, string?> Provider() =>
        new(StringComparer.Ordinal)
        {
            ["Platform:Authentication:Oidc:Enabled"] = "true",
            ["Platform:Authentication:Oidc:Providers:0:ProviderId"] = "entra",
            ["Platform:Authentication:Oidc:Providers:0:DisplayName"] =
                "Microsoft Entra ID",
            ["Platform:Authentication:Oidc:Providers:0:TenantId"] =
                DirectoryTenantId.ToString("D"),
            ["Platform:Authentication:Oidc:Providers:0:ClientId"] = ClientId,
            ["Platform:Authentication:Oidc:Providers:0:RedirectUri"] =
                "https://vistara.example.test/api/v1/auth/oidc/entra/callback",
            ["Platform:Authentication:Oidc:Providers:0:PostLogoutRedirectUri"] =
                "https://vistara.example.test/api/v1/auth/oidc/entra/signed-out",
            ["Platform:Authentication:Oidc:Providers:0:ClientSecret"] = "a-client-secret",
        };

    private static Dictionary<string, string?> Bootstrap(
        Dictionary<string, string?> settings)
    {
        settings["Platform:Bootstrap:FirstOwner:Enabled"] = "true";
        settings["Platform:Bootstrap:FirstOwner:ProviderId"] = "entra";
        settings["Platform:Bootstrap:FirstOwner:DirectoryTenantId"] =
            DirectoryTenantId.ToString("D");
        settings["Platform:Bootstrap:FirstOwner:AllowedObjectIds:0"] = ObjectId;
        settings["Platform:Bootstrap:FirstOwner:TenantSlug"] = "vistara";
        settings["Platform:Bootstrap:FirstOwner:TenantName"] = "Vistara";
        return settings;
    }
}
