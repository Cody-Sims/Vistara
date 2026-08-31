using System.Net;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

public sealed class OidcTokenClientTests
{
    private const string AuthorizationCode = "0.AXkAauthorization-code-value";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Fact]
    public async Task Token_exchange_posts_the_code_verifier_and_a_managed_identity_assertion()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () =>
            OidcHttpTestTransport.Json(TokenResponseJson());
        var assertions = new StubClientAssertionProvider("federated-assertion");
        OidcTokenClient client = CreateClient(provider, assertions);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out OidcTokenSet? tokens));
        Assert.Equal("header.payload.signature", tokens.IdToken);
        IReadOnlyDictionary<string, string> form =
            OidcFormBody.Parse(provider.Transport.RequestBodies[^1]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(AuthorizationCode, form["code"]);
        Assert.Equal(CodeVerifier, form["code_verifier"]);
        Assert.Equal(provider.Options.RedirectUri.AbsoluteUri, form["redirect_uri"]);
        Assert.Equal(OidcTestProvider.ClientId, form["client_id"]);
        Assert.Equal(
            "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            form["client_assertion_type"]);
        Assert.Equal("federated-assertion", form["client_assertion"]);
        Assert.DoesNotContain("client_secret", form.Keys, StringComparer.Ordinal);
        Assert.Equal(provider.TokenEndpoint, assertions.RequestedAudience);
    }

    [Fact]
    public async Task Token_exchange_falls_back_to_a_client_secret_when_no_assertion_exists()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () =>
            OidcHttpTestTransport.Json(TokenResponseJson());
        OidcTokenClient client = CreateClient(
            provider,
            new StubClientAssertionProvider(null),
            new StubClientSecretProvider("fallback-secret"));

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        IReadOnlyDictionary<string, string> form =
            OidcFormBody.Parse(provider.Transport.RequestBodies[^1]);
        Assert.Equal("fallback-secret", form["client_secret"]);
        Assert.DoesNotContain("client_assertion", form.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Token_exchange_prefers_the_secretless_assertion_over_a_configured_secret()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () =>
            OidcHttpTestTransport.Json(TokenResponseJson());
        OidcTokenClient client = CreateClient(
            provider,
            new StubClientAssertionProvider("federated-assertion"),
            new StubClientSecretProvider("fallback-secret"));

        _ = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        string body = provider.Transport.RequestBodies[^1];
        Assert.Contains("client_assertion", body, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_exchange_reports_an_unavailable_credential_rather_than_going_anonymous()
    {
        using var provider = new OidcProviderFixture();
        OidcTokenClient client = CreateClient(provider, new StubClientAssertionProvider(null));

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.Equal(OidcErrors.ClientCredentialUnavailable.Code, result.Error?.Code);
        Assert.Empty(provider.Transport.RequestedUris);
    }

    [Fact]
    public async Task Token_exchange_falls_back_when_the_assertion_provider_faults()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () =>
            OidcHttpTestTransport.Json(TokenResponseJson());
        OidcTokenClient client = CreateClient(
            provider,
            new StubClientAssertionProvider(null, faults: true),
            new StubClientSecretProvider("fallback-secret"));

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(
            "fallback-secret",
            provider.Transport.RequestBodies[^1],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_exchange_never_requests_or_returns_a_refresh_token()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () => OidcHttpTestTransport.Json(
            """
            {
              "token_type": "Bearer",
              "expires_in": 3599,
              "id_token": "header.payload.signature",
              "access_token": "access-token-value",
              "refresh_token": "refresh-token-value"
            }
            """);
        OidcTokenClient client = CreateClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out OidcTokenSet? tokens));
        Assert.DoesNotContain(
            "offline_access",
            provider.Transport.RequestBodies[^1],
            StringComparison.Ordinal);
        Assert.Null(
            typeof(OidcTokenSet).GetProperty("RefreshToken"));
        Assert.Equal("[OidcTokenSet REDACTED]", tokens.ToString());
        Assert.DoesNotContain("refresh-token-value", tokens.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_exchange_projects_the_access_token_expiry_onto_the_injected_clock()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () =>
            OidcHttpTestTransport.Json(TokenResponseJson(expiresIn: 3599));
        OidcTokenClient client = CreateClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out OidcTokenSet? tokens));
        Assert.Equal("access-token-value", tokens.AccessToken);
        Assert.Equal(
            provider.Clock.UtcNow.AddSeconds(3599),
            tokens.AccessTokenExpiresAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"token_type\":\"Bearer\"}")]
    [InlineData("{\"token_type\":\"Bearer\",\"id_token\":\"\"}")]
    [InlineData("{\"token_type\":\"Bearer\",\"id_token\":\"   \"}")]
    [InlineData("{\"token_type\":\"Bearer\",\"id_token\":42}")]
    [InlineData("{\"token_type\":\"mac\",\"id_token\":\"header.payload.signature\"}")]
    public async Task Token_exchange_rejects_a_response_without_a_usable_bearer_id_token(
        string json)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () => OidcHttpTestTransport.Json(json);
        OidcTokenClient client = CreateClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Token_exchange_redacts_the_provider_error_body()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () => OidcHttpTestTransport.Status(
            HttpStatusCode.BadRequest,
            """
            {
              "error": "invalid_grant",
              "error_description": "AADSTS70008: expired code 00000000-secret-trace",
              "trace_id": "trace-value",
              "correlation_id": "correlation-value"
            }
            """);
        OidcTokenClient client = CreateClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, result.Error.Code);
        Assert.Equal(OidcErrors.TokenExchangeFailed.Message, result.Error.Message);
        foreach (string secret in new[]
        {
            "AADSTS70008",
            "expired code",
            "00000000-secret-trace",
            "trace-value",
            "correlation-value",
        })
        {
            Assert.DoesNotContain(secret, result.Error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Token_exchange_reports_provider_faults_as_unavailable(HttpStatusCode status)
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () =>
            OidcHttpTestTransport.Status(status, "{\"error\":\"temporarily_unavailable\"}");
        OidcTokenClient client = CreateClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenEndpointUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Token_exchange_rejects_an_oversized_or_non_json_response()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenResponse = () => OidcHttpTestTransport.Json(
            new string('a', OidcTokenClient.MaximumResponseBytes + 1));
        OidcTokenClient client = CreateClient(provider);

        Result<OidcTokenSet> oversized = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        provider.Transport.TokenResponse = () =>
            OidcHttpTestTransport.Json(TokenResponseJson(), "text/html");
        Result<OidcTokenSet> html = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, oversized.Error?.Code);
        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, html.Error?.Code);
    }

    [Fact]
    public async Task Token_exchange_bounds_a_slow_provider_with_the_configured_timeout()
    {
        using var provider = new OidcProviderFixture(httpTimeout: TimeSpan.FromMilliseconds(30));
        provider.Transport.TokenDelay = TimeSpan.FromSeconds(30);
        OidcTokenClient client = CreateClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenEndpointUnavailable.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Token_exchange_propagates_caller_cancellation()
    {
        using var provider = new OidcProviderFixture();
        provider.Transport.TokenDelay = TimeSpan.FromSeconds(30);
        OidcTokenClient client = CreateClient(provider);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.RedeemAuthorizationCodeAsync(
                new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
                provider.CreateMetadata(),
                cancellation.Token));
    }

    [Fact]
    public async Task Token_exchange_refuses_a_token_endpoint_outside_the_configured_authority()
    {
        using var provider = new OidcProviderFixture();
        OidcTokenClient client = CreateClient(provider);
        OidcProviderMetadata hostile = provider.CreateMetadata() with
        {
            TokenEndpoint = new Uri("https://attacker.example/token"),
        };

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier),
            hostile,
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, result.Error?.Code);
        Assert.Empty(provider.Transport.RequestedUris);
    }

    [Theory]
    [InlineData("", CodeVerifier)]
    [InlineData("   ", CodeVerifier)]
    [InlineData("code\nwith-newline", CodeVerifier)]
    [InlineData("code with space", CodeVerifier)]
    [InlineData("code\u0000", CodeVerifier)]
    [InlineData(AuthorizationCode, "")]
    [InlineData(AuthorizationCode, "too-short")]
    [InlineData(AuthorizationCode, "has spaces but is long enough to pass the length gate!!")]
    public void Redemption_rejects_malformed_codes_and_verifiers(string code, string verifier)
    {
        Assert.Throws<ArgumentException>(() =>
            new OidcAuthorizationCodeRedemption(code, verifier));
    }

    [Fact]
    public void Redemption_never_renders_its_secrets_in_string_form()
    {
        var redemption = new OidcAuthorizationCodeRedemption(AuthorizationCode, CodeVerifier);

        string rendered = redemption.ToString();

        Assert.Equal("[OidcAuthorizationCodeRedemption REDACTED]", rendered);
        Assert.DoesNotContain(AuthorizationCode, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeVerifier, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_assertion_never_renders_its_value_in_string_form()
    {
        var assertion = new OidcClientAssertion("assertion-value");

        Assert.Equal("[OidcClientAssertion REDACTED]", assertion.ToString());
        Assert.Throws<ArgumentException>(() => new OidcClientAssertion("  "));
    }

    [Fact]
    public void Token_client_requires_its_collaborators()
    {
        using var provider = new OidcProviderFixture();
        var credentials = new OidcClientCredentialResolver(
            new StubClientAssertionProvider("assertion"),
            null);

        Assert.Throws<ArgumentNullException>(() =>
            new OidcTokenClient(null!, provider.Options, credentials, provider.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcTokenClient(provider.HttpClient, null!, credentials, provider.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcTokenClient(provider.HttpClient, provider.Options, null!, provider.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcTokenClient(provider.HttpClient, provider.Options, credentials, null!));
        Assert.Throws<ArgumentException>(() =>
            new OidcClientCredentialResolver(null, null));
    }

    private static string TokenResponseJson(int expiresIn = 3599) =>
        $$"""
        {
          "token_type": "Bearer",
          "expires_in": {{OidcFormBody.ToInvariant(expiresIn)}},
          "scope": "openid profile email",
          "id_token": "header.payload.signature",
          "access_token": "access-token-value"
        }
        """;

    private static OidcTokenClient CreateClient(
        OidcProviderFixture provider,
        IOidcClientAssertionProvider? assertionProvider = null,
        IOidcClientSecretProvider? secretProvider = null) =>
        new(
            provider.HttpClient,
            provider.Options,
            new OidcClientCredentialResolver(
                assertionProvider ?? new StubClientAssertionProvider("federated-assertion"),
                secretProvider),
            provider.Clock);

    private sealed class StubClientAssertionProvider : IOidcClientAssertionProvider
    {
        private readonly string? _assertion;
        private readonly bool _faults;

        internal StubClientAssertionProvider(string? assertion, bool faults = false)
        {
            _assertion = assertion;
            _faults = faults;
        }

        internal Uri? RequestedAudience { get; private set; }

        public ValueTask<OidcClientAssertion?> GetAssertionAsync(
            Uri tokenEndpoint,
            CancellationToken cancellationToken)
        {
            RequestedAudience = tokenEndpoint;
            if (_faults)
            {
                throw new InvalidOperationException("managed identity unavailable");
            }

            return ValueTask.FromResult(
                _assertion is null ? null : new OidcClientAssertion(_assertion));
        }
    }

    private sealed class StubClientSecretProvider : IOidcClientSecretProvider
    {
        private readonly string? _secret;

        internal StubClientSecretProvider(string? secret) => _secret = secret;

        public ValueTask<string?> GetSecretAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_secret);
    }
}
