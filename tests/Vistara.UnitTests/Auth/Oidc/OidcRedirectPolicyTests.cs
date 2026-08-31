using System.Net;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// Redirect handling is exercised against a real loopback server and a real
/// HttpClientHandler. A stub message handler cannot follow a redirect, so only
/// a live transport proves that a followed hop is caught.
///
/// Discovery, JWKS, and token endpoints are validated against the configured
/// authority before the request is issued. A followed redirect moves the
/// actual request to a URL that never passed that check, so every hop must
/// fail closed, whether it lands off-host or back on the same host.
/// </summary>
public sealed class OidcRedirectPolicyTests : IDisposable
{
    private const string MetadataPath = "/tenant/v2.0/.well-known/openid-configuration";
    private const string JwksPath = "/tenant/discovery/v2.0/keys";
    private const string TokenPath = "/tenant/oauth2/v2.0/token";
    private const string AuthorizePath = "/tenant/oauth2/v2.0/authorize";
    private const string ElsewherePath = "/tenant/relocated";

    private readonly LoopbackHttpServer _authority = new();
    private readonly LoopbackHttpServer _offHost = new();
    private readonly OidcProviderFixture _keys = new();

    public void Dispose()
    {
        _authority.Dispose();
        _offHost.Dispose();
        _keys.Dispose();
    }

    /// <summary>
    /// The positive control. Without it, every redirect assertion below could
    /// be passing because the loopback harness never works at all.
    /// </summary>
    [Fact]
    public async Task Discovery_without_a_redirect_succeeds_over_a_real_loopback_transport()
    {
        RouteMetadata();
        RouteJwks();
        using HttpClient client = CreateRedirectFollowingClient();
        using OidcMetadataCache cache = CreateCache(client);

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Discovery_fails_closed_when_a_same_host_redirect_is_followed(
        HttpStatusCode status)
    {
        Uri relocated = _authority.Route(ElsewherePath, () => LoopbackHttpServer.Json(Metadata()));
        _authority.Route(MetadataPath, () => LoopbackHttpServer.Redirect(relocated, status));
        RouteJwks();
        using HttpClient client = CreateRedirectFollowingClient();
        using OidcMetadataCache cache = CreateCache(client);

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
        Assert.Contains(ElsewherePath, _authority.RequestedPaths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Discovery_fails_closed_when_an_off_host_redirect_is_followed()
    {
        Uri relocated = _offHost.Route(ElsewherePath, () => LoopbackHttpServer.Json(Metadata()));
        _authority.Route(
            MetadataPath,
            () => LoopbackHttpServer.Redirect(relocated, HttpStatusCode.Found));
        RouteJwks();
        using HttpClient client = CreateRedirectFollowingClient();
        using OidcMetadataCache cache = CreateCache(client);

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
        Assert.Contains(ElsewherePath, _offHost.RequestedPaths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Jwks_fails_closed_when_a_same_host_redirect_is_followed()
    {
        RouteMetadata();
        Uri relocated = _authority.Route(ElsewherePath, () => LoopbackHttpServer.Json(Jwks()));
        _authority.Route(
            JwksPath,
            () => LoopbackHttpServer.Redirect(relocated, HttpStatusCode.Found));
        using HttpClient client = CreateRedirectFollowingClient();
        using OidcMetadataCache cache = CreateCache(client);

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
        Assert.Contains(ElsewherePath, _authority.RequestedPaths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Jwks_fails_closed_when_an_off_host_redirect_is_followed()
    {
        RouteMetadata();
        Uri relocated = _offHost.Route(ElsewherePath, () => LoopbackHttpServer.Json(Jwks()));
        _authority.Route(
            JwksPath,
            () => LoopbackHttpServer.Redirect(relocated, HttpStatusCode.Found));
        using HttpClient client = CreateRedirectFollowingClient();
        using OidcMetadataCache cache = CreateCache(client);

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
        Assert.Contains(ElsewherePath, _offHost.RequestedPaths, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Token_exchange_fails_closed_when_a_same_host_redirect_is_followed(
        HttpStatusCode status)
    {
        Uri relocated = _authority.Route(
            ElsewherePath,
            () => LoopbackHttpServer.Json(TokenResponse()));
        _authority.Route(TokenPath, () => LoopbackHttpServer.Redirect(relocated, status));
        using HttpClient client = CreateRedirectFollowingClient();
        OidcTokenClient tokenClient = CreateTokenClient(client);

        Result<OidcTokenSet> result = await tokenClient.RedeemAuthorizationCodeAsync(
            OidcCredentialStubs.Redemption(),
            Metadata(signingKeys: true),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, result.Error?.Code);
        Assert.Contains(ElsewherePath, _authority.RequestedPaths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Token_exchange_fails_closed_when_an_off_host_redirect_is_followed()
    {
        Uri relocated = _offHost.Route(
            ElsewherePath,
            () => LoopbackHttpServer.Json(TokenResponse()));
        _authority.Route(
            TokenPath,
            () => LoopbackHttpServer.Redirect(relocated, HttpStatusCode.TemporaryRedirect));
        using HttpClient client = CreateRedirectFollowingClient();
        OidcTokenClient tokenClient = CreateTokenClient(client);

        Result<OidcTokenSet> result = await tokenClient.RedeemAuthorizationCodeAsync(
            OidcCredentialStubs.Redemption(),
            Metadata(signingKeys: true),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, result.Error?.Code);
        Assert.Contains(ElsewherePath, _offHost.RequestedPaths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Token_exchange_without_a_redirect_succeeds_over_a_real_loopback_transport()
    {
        _authority.Route(TokenPath, () => LoopbackHttpServer.Json(TokenResponse()));
        using HttpClient client = CreateRedirectFollowingClient();
        OidcTokenClient tokenClient = CreateTokenClient(client);

        Result<OidcTokenSet> result = await tokenClient.RedeemAuthorizationCodeAsync(
            OidcCredentialStubs.Redemption(),
            Metadata(signingKeys: true),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// The handler a composition root is required to register never issues the
    /// second request at all, so the relocated resource is never contacted.
    /// </summary>
    [Fact]
    public async Task The_required_handler_never_follows_a_redirect_in_the_first_place()
    {
        Uri relocated = _offHost.Route(ElsewherePath, () => LoopbackHttpServer.Json(Metadata()));
        _authority.Route(
            MetadataPath,
            () => LoopbackHttpServer.Redirect(relocated, HttpStatusCode.Found));
        RouteJwks();
        using HttpClient client = new(OidcHttpDefaults.CreateHandler(), disposeHandler: true);
        using OidcMetadataCache cache = CreateCache(client);

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
        Assert.Empty(_offHost.RequestedPaths);
    }

    [Fact]
    public void The_required_handler_disables_redirects_cookies_and_ambient_credentials()
    {
        using SocketsHttpHandler handler = OidcHttpDefaults.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Credentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal("vistara-oidc", OidcHttpDefaults.HttpClientName);
    }

    /// <summary>
    /// A handler that reports no originating request at all is as untrusted as
    /// one that reports a different URL: neither proves the body came from the
    /// validated endpoint.
    /// </summary>
    [Fact]
    public async Task Discovery_fails_closed_when_a_handler_reports_no_originating_request()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.StripRequestMessage = true;
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/tenant/relocated")]
    [InlineData("https://attacker.example/tenant/v2.0/.well-known/openid-configuration")]
    [InlineData("https://user:pass@login.microsoftonline.com/tenant/v2.0")]
    public async Task Discovery_fails_closed_when_the_reported_uri_differs_from_the_requested_one(
        string finalUri)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.RedirectedTo = new Uri(finalUri);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Token_exchange_fails_closed_when_the_reported_uri_differs()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () => OidcHttpTestTransport.Json(TokenResponse());
        provider.Transport.RedirectedTo =
            new Uri("https://login.microsoftonline.com/tenant/relocated");
        OidcTokenClient client = OidcCredentialStubs.CreateTokenClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            OidcCredentialStubs.Redemption(),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, result.Error?.Code);
    }

    /// <summary>
    /// .NET normalizes scheme and host case when a Uri is constructed, so the
    /// meaningful case risk is the path, which RFC 3986 treats as
    /// case-sensitive. A path that differs only in case is a different
    /// resource and must be treated as a redirect.
    /// </summary>
    [Fact]
    public async Task Discovery_treats_a_path_case_difference_as_a_redirect()
    {
        using var unchanged = new OidcProviderFixture();
        using OidcMetadataCache unchangedCache = unchanged.CreateCache();

        using var recased = new OidcProviderFixture();
        recased.Transport.RedirectedTo = new Uri(
            recased.Options.MetadataAddress.AbsoluteUri.Replace(
                ".well-known",
                ".WELL-KNOWN",
                StringComparison.Ordinal));
        using OidcMetadataCache recasedCache = recased.CreateCache();

        Result<OidcProviderMetadata> allowed =
            await unchangedCache.GetAsync(CancellationToken.None);
        Result<OidcProviderMetadata> blocked =
            await recasedCache.GetAsync(CancellationToken.None);

        Assert.True(allowed.IsSuccess);
        Assert.Equal(OidcErrors.MetadataUnavailable.Code, blocked.Error?.Code);
    }

    private static HttpClient CreateRedirectFollowingClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = true }, disposeHandler: true);

    private OidcProviderOptions CreateOptions() =>
        new(
            OidcTestProvider.TenantId,
            OidcTestProvider.ClientId,
            new Uri(_authority.Origin, "/signin-entra"),
            authority: new Uri(_authority.Origin, "/tenant/v2.0"),
            requireHttps: false);

    private OidcMetadataCache CreateCache(HttpClient client) =>
        new(client, CreateOptions(), _keys.Clock);

    private OidcTokenClient CreateTokenClient(HttpClient client) =>
        new(
            client,
            CreateOptions(),
            new OidcClientCredentialResolver(
                new OidcCredentialStubs.StubClientAssertionProvider("federated-assertion"),
                null),
            _keys.Clock);

    private void RouteMetadata() =>
        _authority.Route(MetadataPath, () => LoopbackHttpServer.Json(Metadata()));

    private void RouteJwks() =>
        _authority.Route(JwksPath, () => LoopbackHttpServer.Json(Jwks()));

    private string Metadata()
    {
        OidcProviderOptions options = CreateOptions();
        return $$"""
        {
          "issuer": "{{options.ExpectedIssuer}}",
          "authorization_endpoint": "{{new Uri(_authority.Origin, AuthorizePath).AbsoluteUri}}",
          "token_endpoint": "{{new Uri(_authority.Origin, TokenPath).AbsoluteUri}}",
          "jwks_uri": "{{new Uri(_authority.Origin, JwksPath).AbsoluteUri}}",
          "response_types_supported": ["code"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;
    }

    private OidcProviderMetadata Metadata(bool signingKeys)
    {
        _ = signingKeys;
        OidcProviderOptions options = CreateOptions();
        return new OidcProviderMetadata(
            options.ExpectedIssuer,
            new Uri(_authority.Origin, AuthorizePath),
            new Uri(_authority.Origin, TokenPath),
            new Uri(_authority.Origin, JwksPath),
            null,
            [_keys.SigningKey],
            _keys.Clock.UtcNow);
    }

    private string Jwks() => _keys.BuildJwksJson();

    private static string TokenResponse() =>
        """
        {
          "token_type": "Bearer",
          "expires_in": 3599,
          "id_token": "header.payload.signature",
          "access_token": "access-token-value"
        }
        """;
}
