using Vistara.Auth.Oidc;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// Deterministic client-credential doubles shared by every test that has to
/// drive a token exchange. Nothing here reaches a managed identity or a vault.
/// </summary>
internal static class OidcCredentialStubs
{
    internal const string AuthorizationCode = "0.AXkAauthorization-code-value";
    internal const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    internal static OidcAuthorizationCodeRedemption Redemption() =>
        new(AuthorizationCode, CodeVerifier);

    internal static OidcTokenClient CreateTokenClient(
        OidcProviderFixture provider,
        IOidcClientAssertionProvider? assertionProvider = null,
        IOidcClientSecretProvider? secretProvider = null,
        HttpClient? httpClient = null) =>
        new(
            httpClient ?? provider.HttpClient,
            provider.Options,
            new OidcClientCredentialResolver(
                assertionProvider ?? new StubClientAssertionProvider("federated-assertion"),
                secretProvider),
            provider.Clock);

    internal sealed class StubClientAssertionProvider : IOidcClientAssertionProvider
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

    internal sealed class StubClientSecretProvider : IOidcClientSecretProvider
    {
        private readonly string? _secret;

        internal StubClientSecretProvider(string? secret) => _secret = secret;

        public ValueTask<string?> GetSecretAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_secret);
    }
}
