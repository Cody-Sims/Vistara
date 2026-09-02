using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.Oidc;

namespace Vistara.IntegrationTests.Auth.Oidc;

/// <summary>
/// A real identity provider on the HTTPS loopback interface.
///
/// It is a live TLS server rather than a stub message handler on purpose: the
/// hosted sign-in path composes one named, redirect-disabled
/// <see cref="HttpClient"/>, and only a real transport proves that discovery,
/// the key set, and the token exchange all travel over it and that the client
/// credential reaches the token endpoint in the request body. The certificate
/// is self-signed and the API trusts exactly this one certificate, so nothing
/// here depends on machine trust.
/// </summary>
internal sealed class LoopbackIdentityProvider : IAsyncDisposable
{
    internal const string SigningKeyId = "loopback-signing-key";

    private readonly WebApplication _app;
    private readonly RSA _signingRsa;
    private readonly RSA _foreignRsa;
    private readonly RSA _weakRsa;
    private readonly ConcurrentDictionary<string, AuthorizationGrant> _grants =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> _tokenRequests = new();
    private readonly Func<DateTimeOffset> _now;

    private LoopbackIdentityProvider(
        WebApplication app,
        X509Certificate2 certificate,
        RSA signingRsa,
        RSA foreignRsa,
        RSA weakRsa,
        Guid directoryTenantId,
        string clientId,
        Uri authority,
        Func<DateTimeOffset> now)
    {
        _app = app;
        _signingRsa = signingRsa;
        _foreignRsa = foreignRsa;
        _weakRsa = weakRsa;
        _now = now;
        Certificate = certificate;
        DirectoryTenantId = directoryTenantId;
        ClientId = clientId;
        Authority = authority;
    }

    internal X509Certificate2 Certificate { get; }

    internal Guid DirectoryTenantId { get; }

    internal string ClientId { get; }

    /// <summary>The issuer the API is configured with and pins responses to.</summary>
    internal Uri Authority { get; private set; }

    /// <summary>Serves a key set the API must refuse to take a key from.</summary>
    internal bool PublishHostileKeySetOnly { get; set; }

    /// <summary>Serves a token response body of the test's choosing.</summary>
    internal Func<string?>? TokenResponseBody { get; set; }

    /// <summary>
    /// Answers discovery with a redirect. A client that follows it would fetch
    /// the document from a URL that never passed the authority check.
    /// </summary>
    internal string? MetadataRedirectTo { get; set; }

    /// <summary>The key the provider never publishes, for forged tokens.</summary>
    internal RSA UnpublishedSigningKey => _foreignRsa;

    /// <summary>Every form the token endpoint received, in order.</summary>
    internal IReadOnlyList<IReadOnlyDictionary<string, string>> TokenRequests =>
        [.. _tokenRequests];

    internal static async Task<LoopbackIdentityProvider> StartAsync(
        Guid directoryTenantId,
        string clientId,
        Func<DateTimeOffset> now)
    {
        X509Certificate2 certificate = CreateLoopbackCertificate();
        RSA signingRsa = RSA.Create(2048);
        RSA foreignRsa = RSA.Create(2048);
        RSA weakRsa = RSA.Create(1024);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(
                IPAddress.Loopback,
                0,
                listen => listen.UseHttps(certificate)));
        WebApplication app = builder.Build();

        var provider = new LoopbackIdentityProvider(
            app,
            certificate,
            signingRsa,
            foreignRsa,
            weakRsa,
            directoryTenantId,
            clientId,
            new Uri($"https://127.0.0.1/{directoryTenantId:D}/v2.0"),
            now);
        provider.MapEndpoints(app);
        await app.StartAsync();

        var listening = new Uri(
            app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First());

        // The port is only known once Kestrel has bound, and the discovery
        // document, the issuer, and the endpoint allowlist all have to agree
        // on it.
        provider.Authority = new Uri(
            $"https://127.0.0.1:{listening.Port}/{directoryTenantId:D}/v2.0");
        return provider;
    }

    /// <summary>
    /// Plays the part of the visitor consenting at the provider: reads the
    /// authorization request the API produced and issues a code bound to it.
    /// </summary>
    internal string Authorize(
        Uri authorizationUri,
        Guid objectId,
        Guid? directoryTenantId = null,
        string email = "owner@example.test",
        string displayName = "Directory Owner")
    {
        string code = $"code-{Guid.NewGuid():N}";
        _grants[code] = DescribeGrant(
            authorizationUri,
            objectId,
            directoryTenantId,
            email,
            displayName);
        return code;
    }

    internal AuthorizationGrant DescribeGrant(
        Uri authorizationUri,
        Guid objectId,
        Guid? directoryTenantId = null,
        string email = "owner@example.test",
        string displayName = "Directory Owner")
    {
        Dictionary<string, string> query = ParseQuery(authorizationUri.Query);
        return new AuthorizationGrant(
            query["nonce"],
            query["code_challenge"],
            query["redirect_uri"],
            objectId,
            directoryTenantId ?? DirectoryTenantId,
            email,
            displayName);
    }

    /// <summary>The parameters of the authorization request, for assertions.</summary>
    internal static IReadOnlyDictionary<string, string> ReadAuthorizationRequest(
        Uri authorizationUri) =>
        ParseQuery(authorizationUri.Query);

    internal string CreateIdToken(
        AuthorizationGrant grant,
        string? issuerOverride = null,
        string? audienceOverride = null,
        string? keyIdOverride = null,
        RSA? signingKeyOverride = null,
        TimeSpan? lifetime = null)
    {
        DateTimeOffset now = _now();
        DateTimeOffset expires = now.Add(lifetime ?? TimeSpan.FromMinutes(10));
        string payload = $$"""
            {
              "iss": "{{issuerOverride ?? Authority.AbsoluteUri.TrimEnd('/')}}",
              "aud": "{{audienceOverride ?? ClientId}}",
              "sub": "subject-{{grant.ObjectId:N}}",
              "oid": "{{grant.ObjectId:D}}",
              "tid": "{{grant.DirectoryTenantId:D}}",
              "ver": "2.0",
              "nonce": "{{grant.Nonce}}",
              "preferred_username": "{{grant.Email}}",
              "name": "{{grant.DisplayName}}",
              "iat": {{now.ToUnixTimeSeconds()}},
              "nbf": {{now.AddMinutes(-1).ToUnixTimeSeconds()}},
              "exp": {{expires.ToUnixTimeSeconds()}}
            }
            """;
        var key = new RsaSecurityKey(signingKeyOverride ?? _signingRsa)
        {
            KeyId = keyIdOverride ?? SigningKeyId,
        };
        return new JsonWebTokenHandler().CreateToken(
            payload,
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
    }

    /// <summary>A token response body carrying one identity token.</summary>
    internal static string TokenResponse(string idToken) =>
        $$"""
        {"token_type":"Bearer","expires_in":3600,"id_token":"{{idToken}}"}
        """;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _signingRsa.Dispose();
        _foreignRsa.Dispose();
        _weakRsa.Dispose();
        Certificate.Dispose();
    }

    private void MapEndpoints(WebApplication app)
    {
        string tenant = DirectoryTenantId.ToString("D");
        app.MapGet(
            $"/{tenant}/v2.0/.well-known/openid-configuration",
            (HttpContext context) =>
            {
                if (MetadataRedirectTo is { } location)
                {
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location = location;
                    return Task.CompletedTask;
                }

                return WriteJsonAsync(context, MetadataJson());
            });
        app.MapGet(
            $"/{tenant}/v2.0/relocated-configuration",
            (HttpContext context) => WriteJsonAsync(context, MetadataJson()));
        app.MapGet(
            $"/{tenant}/discovery/v2.0/keys",
            (HttpContext context) => WriteJsonAsync(
                context,
                PublishHostileKeySetOnly ? HostileKeySetJson() : KeySetJson()));
        app.MapPost(
            $"/{tenant}/oauth2/v2.0/token",
            (HttpContext context) => IssueTokenAsync(context));
    }

    private async Task IssueTokenAsync(HttpContext context)
    {
        Dictionary<string, string> form = ParseQuery(
            "?" + await new StreamReader(context.Request.Body).ReadToEndAsync(
                context.RequestAborted));
        _tokenRequests.Enqueue(form);

        if (TokenResponseBody is { } custom)
        {
            string? body = custom();
            if (body is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(context, """{"error":"invalid_grant"}""");
                return;
            }

            await WriteJsonAsync(context, body);
            return;
        }

        if (!form.TryGetValue("code", out string? code) ||
            !_grants.TryRemove(code, out AuthorizationGrant? grant) ||
            !form.TryGetValue("client_id", out string? clientId) ||
            !string.Equals(clientId, ClientId, StringComparison.Ordinal) ||
            !form.TryGetValue("redirect_uri", out string? redirectUri) ||
            !string.Equals(redirectUri, grant.RedirectUri, StringComparison.Ordinal) ||
            !form.TryGetValue("code_verifier", out string? verifier) ||
            !string.Equals(
                OidcPkce.CreateChallenge(verifier),
                grant.CodeChallenge,
                StringComparison.Ordinal) ||
            !HasClientCredential(form))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonAsync(context, """{"error":"invalid_grant"}""");
            return;
        }

        await WriteJsonAsync(context, TokenResponse(CreateIdToken(grant)));
    }

    private static bool HasClientCredential(Dictionary<string, string> form) =>
        (form.TryGetValue("client_secret", out string? secret) &&
            !string.IsNullOrWhiteSpace(secret)) ||
        (form.TryGetValue("client_assertion", out string? assertion) &&
            !string.IsNullOrWhiteSpace(assertion) &&
            form.TryGetValue("client_assertion_type", out string? assertionType) &&
            string.Equals(
                assertionType,
                OidcClientAssertion.AssertionType,
                StringComparison.Ordinal));

    private string MetadataJson() =>
        $$"""
        {
          "issuer": "{{Authority.AbsoluteUri.TrimEnd('/')}}",
          "authorization_endpoint": "{{Origin}}/{{DirectoryTenantId:D}}/oauth2/v2.0/authorize",
          "token_endpoint": "{{Origin}}/{{DirectoryTenantId:D}}/oauth2/v2.0/token",
          "jwks_uri": "{{Origin}}/{{DirectoryTenantId:D}}/discovery/v2.0/keys",
          "response_types_supported": ["code"],
          "subject_types_supported": ["pairwise"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;

    private string Origin => $"https://127.0.0.1:{Authority.Port}";

    private string KeySetJson() =>
        $$"""
        { "keys": [ {{RsaKeyJson(_signingRsa, SigningKeyId, "sig", "RS256")}} ] }
        """;

    /// <summary>
    /// Material a hostile or careless provider might publish: a symmetric key,
    /// an encryption key, an undersized modulus, a key with no identifier, and
    /// a downgraded algorithm. None of it may sign an accepted identity token.
    /// </summary>
    private string HostileKeySetJson() =>
        $$"""
        {
          "keys": [
            {"kty":"oct","kid":"symmetric-key","use":"sig","alg":"HS256","k":"c3VwZXItc2VjcmV0LWtleS12YWx1ZS1oZXJl"},
            {{RsaKeyJson(_foreignRsa, "encryption-key", "enc", "RSA-OAEP")}},
            {{RsaKeyJson(_weakRsa, "undersized-key", "sig", "RS256")}},
            {{RsaKeyJson(_foreignRsa, "", "sig", "RS256")}},
            {{RsaKeyJson(_foreignRsa, "downgraded-key", "sig", "HS256")}}
          ]
        }
        """;

    private static string RsaKeyJson(RSA rsa, string kid, string use, string alg)
    {
        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
        return $$"""
            {"kty":"RSA","kid":"{{kid}}","use":"{{use}}","alg":"{{alg}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus!)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent!)}}"}
            """;
    }

    private static async Task WriteJsonAsync(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json, Encoding.UTF8, context.RequestAborted);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split(
            '&',
            StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            string name = separator < 0 ? pair : pair[..separator];
            string value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            values[Uri.UnescapeDataString(name.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    /// <summary>
    /// A self-signed certificate for the loopback address. It is exported and
    /// reloaded as PKCS#12 so the private key is usable by the server on every
    /// supported platform.
    /// </summary>
    private static X509Certificate2 CreateLoopbackCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=127.0.0.1",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        alternativeNames.AddDnsName("localhost");
        request.CertificateExtensions.Add(alternativeNames.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 generated = request.CreateSelfSigned(
            now.AddDays(-1),
            now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null);
    }

    internal sealed record AuthorizationGrant(
        string Nonce,
        string CodeChallenge,
        string RedirectUri,
        Guid ObjectId,
        Guid DirectoryTenantId,
        string Email,
        string DisplayName);
}
