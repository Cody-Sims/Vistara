using Vistara.Auth.Oidc;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

public sealed class OidcProviderOptionsTests
{
    private static readonly Guid TenantId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Uri RedirectUri =
        new("https://vistara.example/signin-entra");

    [Fact]
    public void Options_derive_the_entra_authority_metadata_and_issuer_from_the_tenant()
    {
        var options = new OidcProviderOptions(TenantId, ClientId, RedirectUri);

        Assert.Equal(
            new Uri($"https://login.microsoftonline.com/{TenantId:D}/v2.0"),
            options.Authority);
        Assert.Equal(
            new Uri(
                $"https://login.microsoftonline.com/{TenantId:D}/v2.0/.well-known/openid-configuration"),
            options.MetadataAddress);
        Assert.Equal(
            $"https://login.microsoftonline.com/{TenantId:D}/v2.0",
            options.ExpectedIssuer);
        Assert.Equal(new Uri("https://vistara.example/"), options.ApplicationBaseUri);
        Assert.Equal(TenantId.ToString("D"), options.TenantIdValue);
        Assert.Equal(ClientId, options.ClientId);
    }

    [Fact]
    public void Options_default_to_a_narrow_scope_algorithm_and_timeout_policy()
    {
        var options = new OidcProviderOptions(TenantId, ClientId, RedirectUri);

        Assert.Equal(["openid", "profile", "email"], options.Scopes);
        Assert.Equal(["RS256"], options.AllowedSigningAlgorithms);
        Assert.Equal(TimeSpan.FromMinutes(2), options.ClockSkew);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HttpTimeout);
        Assert.Equal(TimeSpan.FromHours(12), options.MetadataCacheLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), options.MetadataRefreshBackoff);
        Assert.True(options.RequireHttps);
        Assert.Equal([options.Authority.Host], options.AllowedEndpointHosts);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("consumers")]
    public void Options_reject_multi_tenant_authority_aliases(string alias)
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            authority: new Uri($"https://login.microsoftonline.com/{alias}/v2.0")));
    }

    [Fact]
    public void Options_reject_the_personal_microsoft_account_tenant()
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad"),
            ClientId,
            RedirectUri));
    }

    [Fact]
    public void Options_reject_an_empty_tenant_and_a_non_guid_client()
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            Guid.Empty,
            ClientId,
            RedirectUri));
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            "not-a-guid",
            RedirectUri));
    }

    [Fact]
    public void Options_reject_an_authority_whose_tenant_segment_is_not_the_configured_tenant()
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            authority: new Uri(
                "https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0")));
    }

    [Theory]
    [InlineData("http://vistara.example/signin-entra")]
    [InlineData("https://vistara.example/signin-entra#fragment")]
    [InlineData("https://vistara.example/signin-entra?next=/a")]
    [InlineData("/signin-entra")]
    public void Options_reject_unsafe_redirect_uris(string redirectUri)
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            new Uri(redirectUri, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void Options_require_https_endpoints_unless_a_loopback_development_host_is_used()
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            new Uri("http://vistara.example/signin-entra"),
            authority: new Uri("http://vistara.example/tenant/v2.0"),
            requireHttps: false));

        var development = new OidcProviderOptions(
            TenantId,
            ClientId,
            new Uri("http://localhost:5080/signin-entra"),
            authority: new Uri("http://localhost:5081/tenant/v2.0"),
            requireHttps: false);

        Assert.False(development.RequireHttps);
        Assert.Equal("http://localhost:5081/tenant/v2.0", development.ExpectedIssuer);
    }

    [Fact]
    public void Options_reject_scopes_that_are_malformed_duplicated_or_request_refresh_tokens()
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            scopes: ["profile"]));
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            scopes: ["openid", "openid"]));
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            scopes: ["openid", "offline_access"]));
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            scopes: ["openid", "pro file"]));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS256 ")]
    public void Options_reject_signing_algorithms_outside_the_asymmetric_allowlist(string algorithm)
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            allowedSigningAlgorithms: [algorithm]));
    }

    [Fact]
    public void Options_reject_an_application_base_uri_from_a_different_origin()
    {
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            applicationBaseUri: new Uri("https://attacker.example/")));
        Assert.Throws<ArgumentException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            postLogoutRedirectUri: new Uri("https://attacker.example/bye")));
    }

    [Fact]
    public void Options_bound_clock_skew_timeout_and_cache_windows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            clockSkew: TimeSpan.FromMinutes(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            clockSkew: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            httpTimeout: TimeSpan.FromMinutes(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            metadataCacheLifetime: TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OidcProviderOptions(
            TenantId,
            ClientId,
            RedirectUri,
            metadataCacheLifetime: TimeSpan.FromMinutes(10),
            metadataRefreshBackoff: TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public void Options_never_render_configuration_details_in_their_string_form()
    {
        var options = new OidcProviderOptions(TenantId, ClientId, RedirectUri);

        Assert.Equal("[OidcProviderOptions REDACTED]", options.ToString());
    }

    private const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
}
