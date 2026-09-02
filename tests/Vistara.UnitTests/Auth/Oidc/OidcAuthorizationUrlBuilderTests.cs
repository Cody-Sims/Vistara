using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

public sealed class OidcAuthorizationUrlBuilderTests
{
    private static readonly string[] ExpectedAuthorizationParameters =
    [
        "client_id",
        "code_challenge",
        "code_challenge_method",
        "nonce",
        "redirect_uri",
        "response_mode",
        "response_type",
        "scope",
        "state",
    ];

    [Fact]
    public void Authorization_url_carries_exactly_the_code_and_pkce_parameters()
    {
        using var provider = new OidcProviderFixture();
        OidcLoginHandle handle = CreateHandle(provider);

        Uri url = OidcAuthorizationUrlBuilder.Build(
            provider.Options,
            provider.CreateMetadata(),
            handle);

        Assert.Equal(provider.AuthorizationEndpoint.GetLeftPart(UriPartial.Path), url.GetLeftPart(UriPartial.Path));
        IReadOnlyDictionary<string, string> query = OidcFormBody.Parse(url.Query.TrimStart('?'));
        Assert.Equal(
            ExpectedAuthorizationParameters,
            query.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(OidcTestProvider.ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("query", query["response_mode"]);
        Assert.Equal(provider.Options.RedirectUri.AbsoluteUri, query["redirect_uri"]);
        Assert.Equal("openid profile email", query["scope"]);
        Assert.Equal(handle.State, query["state"]);
        Assert.Equal(handle.Nonce, query["nonce"]);
        Assert.Equal(handle.CodeChallenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
    }

    [Fact]
    public void Authorization_url_never_carries_the_code_verifier_or_the_return_target()
    {
        using var provider = new OidcProviderFixture();
        OidcLoginHandle handle = CreateHandle(provider, "/gallery?album=summer");

        Uri url = OidcAuthorizationUrlBuilder.Build(
            provider.Options,
            provider.CreateMetadata(),
            handle);

        Assert.DoesNotContain(handle.CodeVerifier, url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("album", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("offline_access", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorization_url_escapes_every_parameter_value()
    {
        using var provider = new OidcProviderFixture(scopes: ["openid", "api://vistara/read"]);
        OidcLoginHandle handle = CreateHandle(provider);

        Uri url = OidcAuthorizationUrlBuilder.Build(
            provider.Options,
            provider.CreateMetadata(),
            handle);

        Assert.Contains(
            "scope=openid%20api%3A%2F%2Fvistara%2Fread",
            url.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Equal(
            "openid api://vistara/read",
            OidcFormBody.Parse(url.Query.TrimStart('?'))["scope"]);
    }

    [Fact]
    public void Authorization_url_refuses_an_endpoint_outside_the_configured_authority()
    {
        using var provider = new OidcProviderFixture();
        OidcLoginHandle handle = CreateHandle(provider);
        OidcProviderMetadata hostile = provider.CreateMetadata() with
        {
            AuthorizationEndpoint = new Uri("https://attacker.example/authorize"),
        };

        Assert.Throws<ArgumentException>(() =>
            OidcAuthorizationUrlBuilder.Build(provider.Options, hostile, handle));
    }

    [Fact]
    public void Authorization_url_refuses_an_endpoint_that_smuggles_a_query_or_fragment()
    {
        using var provider = new OidcProviderFixture();
        OidcLoginHandle handle = CreateHandle(provider);

        foreach (string endpoint in new[]
        {
            $"{provider.AuthorizationEndpoint.AbsoluteUri}?client_id=attacker",
            $"{provider.AuthorizationEndpoint.AbsoluteUri}#fragment",
        })
        {
            OidcProviderMetadata hostile = provider.CreateMetadata() with
            {
                AuthorizationEndpoint = new Uri(endpoint),
            };

            Assert.Throws<ArgumentException>(() =>
                OidcAuthorizationUrlBuilder.Build(provider.Options, hostile, handle));
        }
    }

    [Fact]
    public void Authorization_url_requires_its_inputs()
    {
        using var provider = new OidcProviderFixture();
        OidcLoginHandle handle = CreateHandle(provider);

        Assert.Throws<ArgumentNullException>(() =>
            OidcAuthorizationUrlBuilder.Build(null!, provider.CreateMetadata(), handle));
        Assert.Throws<ArgumentNullException>(() =>
            OidcAuthorizationUrlBuilder.Build(provider.Options, null!, handle));
        Assert.Throws<ArgumentNullException>(() =>
            OidcAuthorizationUrlBuilder.Build(provider.Options, provider.CreateMetadata(), null!));
    }

    private static OidcLoginHandle CreateHandle(
        OidcProviderFixture provider,
        string? returnTo = "/")
    {
        var factory = new OidcLoginRequestFactory(
            provider.Options,
            new SequentialOidcRandomSource(),
            provider.Clock);
        Result<OidcLoginHandle> result = factory.Create(returnTo);
        Assert.True(result.TryGetValue(out OidcLoginHandle? handle));
        return handle;
    }
}
