using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

public sealed class OidcMetadataCacheTests
{
    [Fact]
    public async Task Metadata_resolves_the_configured_discovery_document_and_signing_keys()
    {
        using var provider = new OidcProviderFixture();
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.True(result.TryGetValue(out OidcProviderMetadata? metadata));
        Assert.Equal(provider.Options.ExpectedIssuer, metadata.Issuer);
        Assert.Equal(provider.AuthorizationEndpoint, metadata.AuthorizationEndpoint);
        Assert.Equal(provider.TokenEndpoint, metadata.TokenEndpoint);
        Assert.Equal(provider.JwksUri, metadata.JwksUri);
        Assert.Equal(provider.Clock.UtcNow, metadata.RetrievedAt);
        SecurityKey key = Assert.Single(metadata.SigningKeys);
        Assert.Equal(OidcProviderFixture.SigningKeyId, key.KeyId);
        Assert.Equal(
            [provider.Options.MetadataAddress, provider.JwksUri],
            provider.Transport.RequestedUris);
    }

    [Fact]
    public async Task Metadata_serves_a_cached_document_until_its_lifetime_expires()
    {
        using var provider = new OidcProviderFixture(
            metadataCacheLifetime: TimeSpan.FromMinutes(30));
        using OidcMetadataCache cache = provider.CreateCache();

        _ = await cache.GetAsync(CancellationToken.None);
        _ = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, provider.Transport.RequestedUris.Count);

        provider.Clock.Advance(TimeSpan.FromMinutes(29));
        _ = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, provider.Transport.RequestedUris.Count);

        provider.Clock.Advance(TimeSpan.FromMinutes(2));
        Result<OidcProviderMetadata> refreshed = await cache.GetAsync(CancellationToken.None);
        Assert.True(refreshed.IsSuccess);
        Assert.Equal(4, provider.Transport.RequestedUris.Count);
    }

    [Fact]
    public async Task Metadata_refresh_is_rate_limited_so_an_unknown_key_cannot_drive_traffic()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        using OidcMetadataCache cache = provider.CreateCache();

        _ = await cache.GetAsync(CancellationToken.None);
        _ = await cache.RefreshAsync(CancellationToken.None);
        _ = await cache.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, provider.Transport.RequestedUris.Count);

        provider.Clock.Advance(TimeSpan.FromMinutes(6));
        Result<OidcProviderMetadata> refreshed = await cache.RefreshAsync(CancellationToken.None);

        Assert.True(refreshed.IsSuccess);
        Assert.Equal(4, provider.Transport.RequestedUris.Count);
    }

    [Fact]
    public async Task Metadata_serves_the_cached_document_when_a_refresh_fails()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromSeconds(1));
        using OidcMetadataCache cache = provider.CreateCache();
        Result<OidcProviderMetadata> first = await cache.GetAsync(CancellationToken.None);
        Assert.True(first.TryGetValue(out OidcProviderMetadata? cached));

        provider.Clock.Advance(TimeSpan.FromMinutes(1));
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Result<OidcProviderMetadata> refreshed = await cache.RefreshAsync(CancellationToken.None);

        Assert.True(refreshed.TryGetValue(out OidcProviderMetadata? served));
        Assert.Equal(cached.Issuer, served.Issuer);
        Assert.Equal(cached.RetrievedAt, served.RetrievedAt);
    }

    [Fact]
    public async Task Metadata_reports_unavailable_when_nothing_was_ever_cached()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Metadata_accepts_only_a_200_json_discovery_response(HttpStatusCode status)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () => new HttpResponseMessage(status);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    public async Task Metadata_rejects_a_discovery_response_that_is_not_json(string mediaType)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () =>
            OidcHttpTestTransport.Json(provider.MetadataJson, mediaType);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_rejects_a_document_larger_than_the_read_bound()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () =>
            OidcHttpTestTransport.Json(
                new string('a', OidcMetadataCache.MaximumDocumentBytes + 1));
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_rejects_an_unbounded_streamed_document_without_reading_it_all()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = OidcHttpTestTransport.EndlessStream;
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_rejects_an_issuer_that_is_not_the_configured_authority()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () => OidcHttpTestTransport.Json(
            provider.BuildMetadataJson(issuer: "https://login.microsoftonline.com/other/v2.0"));
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("https://attacker.example/oauth2/v2.0/token")]
    [InlineData("http://login.microsoftonline.com/tenant/oauth2/v2.0/token")]
    [InlineData("https://login.microsoftonline.com:8443/tenant/oauth2/v2.0/token")]
    [InlineData("https://login.microsoftonline.com.attacker.example/token")]
    [InlineData("http://169.254.169.254/metadata/identity/oauth2/token")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/token")]
    public async Task Metadata_rejects_endpoints_that_leave_the_configured_authority_host(
        string tokenEndpoint)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () => OidcHttpTestTransport.Json(
            provider.BuildMetadataJson(tokenEndpoint: tokenEndpoint));
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_rejects_a_jwks_uri_that_leaves_the_configured_authority_host()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () => OidcHttpTestTransport.Json(
            provider.BuildMetadataJson(jwksUri: "https://attacker.example/keys"));
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
        Assert.DoesNotContain(
            provider.Transport.RequestedUris,
            uri => uri.Host == "attacker.example");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    [InlineData("{\"issuer\":\"https://login.microsoftonline.com/tenant/v2.0\"}")]
    public async Task Metadata_rejects_a_document_without_the_required_endpoints(string json)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () => OidcHttpTestTransport.Json(json);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_keeps_only_asymmetric_signing_keys_from_a_hostile_key_set()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.JwksResponse = () => OidcHttpTestTransport.Json(
            provider.BuildHostileJwksJson());
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.True(result.TryGetValue(out OidcProviderMetadata? metadata));
        SecurityKey key = Assert.Single(metadata.SigningKeys);
        Assert.Equal(OidcProviderFixture.SigningKeyId, key.KeyId);
        JsonWebKey webKey = Assert.IsType<JsonWebKey>(key);
        Assert.Equal(JsonWebAlgorithmsKeyTypes.RSA, webKey.Kty);
        Assert.True(webKey.KeySize >= OidcMetadataCache.MinimumRsaKeySize);
    }

    [Fact]
    public async Task Metadata_drops_every_key_that_shares_an_ambiguous_key_identifier()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.JwksResponse = () => OidcHttpTestTransport.Json(
            provider.BuildDuplicateKidJwksJson());
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    public async Task Metadata_rejects_a_key_set_with_no_usable_signing_key(string json)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.JwksResponse = () => OidcHttpTestTransport.Json(json);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_rejects_a_key_set_larger_than_the_read_bound()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.JwksResponse = () => OidcHttpTestTransport.Json(
            new string('a', OidcMetadataCache.MaximumDocumentBytes + 1));
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_bounds_a_slow_provider_with_the_configured_timeout()
    {
        using var provider = new OidcProviderFixture(httpTimeout: TimeSpan.FromMilliseconds(30));
        provider.Transport.MetadataDelay = TimeSpan.FromSeconds(30);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Metadata_propagates_caller_cancellation_rather_than_reporting_unavailable()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataDelay = TimeSpan.FromSeconds(30);
        using OidcMetadataCache cache = provider.CreateCache();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.GetAsync(cancellation.Token));
    }

    [Fact]
    public async Task Metadata_never_leaks_provider_response_text_in_its_failure()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.MetadataResponse = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "client_secret=super-secret-value",
                    Encoding.UTF8,
                    new MediaTypeHeaderValue("text/plain")),
            };
            return response;
        };
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.DoesNotContain("super-secret-value", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(OidcErrors.MetadataUnavailable.Message, result.Error.Message);
    }

    [Fact]
    public async Task Metadata_collapses_concurrent_callers_onto_one_provider_fetch()
    {
        using var provider = new OidcProviderFixture();
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata>[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
                await cache.GetAsync(CancellationToken.None).ConfigureAwait(false)));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(2, provider.Transport.RequestedUris.Count);
    }

    [Fact]
    public void Metadata_cache_requires_its_collaborators()
    {
        using var provider = new OidcProviderFixture();

        Assert.Throws<ArgumentNullException>(() =>
            new OidcMetadataCache(null!, provider.Options, provider.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcMetadataCache(provider.HttpClient, null!, provider.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcMetadataCache(provider.HttpClient, provider.Options, null!));
    }
}
