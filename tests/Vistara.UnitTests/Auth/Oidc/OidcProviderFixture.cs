using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.Oidc;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// A deterministic stand-in for one Entra tenant: fixed clock, fixed signing
/// key, and a transport that answers only the URLs the fixture published.
/// Nothing here reaches the network.
/// </summary>
internal sealed class OidcProviderFixture : IDisposable
{
    internal const string SigningKeyId = "vistara-test-key";
    internal const string SecondarySigningKeyId = "vistara-test-key-2";

    private readonly RSA _signingRsa = RSA.Create(2048);
    private readonly RSA _secondaryRsa = RSA.Create(2048);
    private readonly RSA _weakRsa = RSA.Create(1024);
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal OidcProviderFixture(
        TimeSpan? httpTimeout = null,
        TimeSpan? metadataCacheLifetime = null,
        TimeSpan? metadataRefreshBackoff = null,
        IReadOnlyCollection<string>? scopes = null)
    {
        Options = OidcTestProvider.CreateOptions(
            scopes: scopes,
            httpTimeout: httpTimeout,
            metadataCacheLifetime: metadataCacheLifetime,
            metadataRefreshBackoff: metadataRefreshBackoff);
        string tenantPath =
            $"https://{OidcProviderOptions.EntraLoginHost}/{OidcTestProvider.TenantId:D}";
        AuthorizationEndpoint = new Uri($"{tenantPath}/oauth2/v2.0/authorize");
        TokenEndpoint = new Uri($"{tenantPath}/oauth2/v2.0/token");
        JwksUri = new Uri($"{tenantPath}/discovery/v2.0/keys");
        SigningKey = new RsaSecurityKey(_signingRsa) { KeyId = SigningKeyId };
        SecondarySigningKey = new RsaSecurityKey(_secondaryRsa) { KeyId = SecondarySigningKeyId };
        Transport = new OidcHttpTestTransport(Options.MetadataAddress, JwksUri, TokenEndpoint)
        {
            MetadataResponse = () => OidcHttpTestTransport.Json(BuildMetadataJson()),
            JwksResponse = () => OidcHttpTestTransport.Json(BuildJwksJson()),
        };
        HttpClient = new HttpClient(Transport, disposeHandler: false);
    }

    internal OidcProviderOptions Options { get; }

    internal FixedOidcClock Clock { get; } = new();

    internal OidcHttpTestTransport Transport { get; }

    internal HttpClient HttpClient { get; }

    internal Uri AuthorizationEndpoint { get; }

    internal Uri TokenEndpoint { get; }

    internal Uri JwksUri { get; }

    internal RsaSecurityKey SigningKey { get; }

    internal RsaSecurityKey SecondarySigningKey { get; }

    internal string MetadataJson => BuildMetadataJson();

    internal OidcMetadataCache CreateCache() => new(HttpClient, Options, Clock);

    internal OidcProviderMetadata CreateMetadata(
        IReadOnlyCollection<SecurityKey>? signingKeys = null) =>
        new(
            Options.ExpectedIssuer,
            AuthorizationEndpoint,
            TokenEndpoint,
            JwksUri,
            null,
            signingKeys ?? [SigningKey],
            Clock.UtcNow);

    internal string BuildMetadataJson(
        string? issuer = null,
        string? authorizationEndpoint = null,
        string? tokenEndpoint = null,
        string? jwksUri = null,
        string? padding = null) =>
        $$"""
        {
          "issuer": "{{issuer ?? Options.ExpectedIssuer}}",
          "authorization_endpoint": "{{authorizationEndpoint ?? AuthorizationEndpoint.AbsoluteUri}}",
          "token_endpoint": "{{tokenEndpoint ?? TokenEndpoint.AbsoluteUri}}",
          "jwks_uri": "{{jwksUri ?? JwksUri.AbsoluteUri}}",
          "response_types_supported": ["code"],
          "subject_types_supported": ["pairwise"],
          "id_token_signing_alg_values_supported": ["RS256"],
          "vistara_padding": "{{padding ?? string.Empty}}"
        }
        """;

    internal string BuildJwksJson() =>
        $$"""
        { "keys": [ {{RsaKeyJson(_signingRsa, SigningKeyId, "sig", "RS256")}} ] }
        """;

    /// <summary>
    /// A key set that mixes the one usable signing key with material an
    /// attacker or a sloppy provider might publish: a symmetric key, an
    /// encryption key, an undersized modulus, a curve outside the configured
    /// algorithms, a key with no identifier, and a downgraded algorithm.
    /// </summary>
    internal string BuildHostileJwksJson() =>
        $$"""
        {
          "keys": [
            {"kty":"oct","kid":"symmetric-key","use":"sig","alg":"HS256","k":"c3VwZXItc2VjcmV0LWtleS12YWx1ZS1oZXJl"},
            {{RsaKeyJson(_secondaryRsa, "encryption-key", "enc", "RSA-OAEP")}},
            {{RsaKeyJson(_weakRsa, "undersized-key", "sig", "RS256")}},
            {{RsaKeyJson(_secondaryRsa, "", "sig", "RS256")}},
            {{RsaKeyJson(_secondaryRsa, "downgraded-key", "sig", "HS256")}},
            {{EcKeyJson(_ecdsa, "curve-key", "sig", "ES256")}},
            {{RsaKeyJson(_signingRsa, SigningKeyId, "sig", "RS256")}}
          ]
        }
        """;

    internal string BuildDuplicateKidJwksJson() =>
        $$"""
        {
          "keys": [
            {{RsaKeyJson(_signingRsa, SigningKeyId, "sig", "RS256")}},
            {{RsaKeyJson(_secondaryRsa, SigningKeyId, "sig", "RS256")}}
          ]
        }
        """;

    public void Dispose()
    {
        HttpClient.Dispose();
        Transport.Dispose();
        _signingRsa.Dispose();
        _secondaryRsa.Dispose();
        _weakRsa.Dispose();
        _ecdsa.Dispose();
    }

    private static string RsaKeyJson(RSA rsa, string kid, string use, string alg)
    {
        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
        return $$"""
            {"kty":"RSA","kid":"{{kid}}","use":"{{use}}","alg":"{{alg}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus!)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent!)}}"}
            """;
    }

    private static string EcKeyJson(ECDsa ecdsa, string kid, string use, string alg)
    {
        ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        return $$"""
            {"kty":"EC","kid":"{{kid}}","use":"{{use}}","alg":"{{alg}}","crv":"P-256","x":"{{Base64UrlEncoder.Encode(parameters.Q.X!)}}","y":"{{Base64UrlEncoder.Encode(parameters.Q.Y!)}}"}
            """;
    }
}

internal sealed class OidcHttpTestTransport : HttpMessageHandler
{
    private const int EndlessStreamCeiling = 4 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly List<Uri> _requestedUris = [];
    private readonly List<string> _requestBodies = [];
    private readonly Uri _metadataAddress;
    private readonly Uri _jwksUri;
    private readonly Uri _tokenEndpoint;

    internal OidcHttpTestTransport(Uri metadataAddress, Uri jwksUri, Uri tokenEndpoint)
    {
        _metadataAddress = metadataAddress;
        _jwksUri = jwksUri;
        _tokenEndpoint = tokenEndpoint;
    }

    internal Func<HttpResponseMessage>? MetadataResponse { get; set; }

    internal Func<HttpResponseMessage>? JwksResponse { get; set; }

    internal Func<HttpResponseMessage>? TokenResponse { get; set; }

    internal TimeSpan MetadataDelay { get; set; }

    internal TimeSpan TokenDelay { get; set; }

    internal IReadOnlyList<Uri> RequestedUris
    {
        get
        {
            lock (_gate)
            {
                return _requestedUris.ToArray();
            }
        }
    }

    internal IReadOnlyList<string> RequestBodies
    {
        get
        {
            lock (_gate)
            {
                return _requestBodies.ToArray();
            }
        }
    }

    internal IReadOnlyList<HttpRequestMessage> Requests { get; } = [];

    internal static HttpResponseMessage Json(
        string json,
        string mediaType = "application/json") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue(mediaType)),
        };

    internal static HttpResponseMessage Status(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json")),
        };

    internal static HttpResponseMessage EndlessStream()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new RepeatingStream(EndlessStreamCeiling)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    /// <summary>
    /// Produces an endless body through a stream that records the largest read
    /// the reader ever asks for, so a test can prove the ceiling is applied to
    /// each read rather than to the rented ArrayPool bucket.
    /// </summary>
    internal static (HttpResponseMessage Response, RepeatingStream Recorder) RecordedEndlessStream()
    {
        var stream = new RepeatingStream(EndlessStreamCeiling);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return (response, stream);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _requestedUris.Add(uri);
            _requestBodies.Add(body);
        }

        if (uri == _metadataAddress)
        {
            await DelayAsync(MetadataDelay, cancellationToken).ConfigureAwait(false);
            return (MetadataResponse ?? (() => Json("{}")))();
        }

        if (uri == _jwksUri)
        {
            return (JwksResponse ?? (() => Json("{\"keys\":[]}")))();
        }

        if (uri == _tokenEndpoint)
        {
            await DelayAsync(TokenDelay, cancellationToken).ConfigureAwait(false);
            return (TokenResponse ?? (() => Json("{}")))();
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Produces an unbounded-looking body with no content length so a reader
    /// must enforce its own byte ceiling rather than trusting the provider.
    /// </summary>
    internal sealed class RepeatingStream : Stream
    {
        private readonly int _ceiling;
        private int _produced;

        internal RepeatingStream(int ceiling) => _ceiling = ceiling;

        internal int LargestRequestedRead { get; private set; }

        internal int TotalBytesRead => _produced;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            LargestRequestedRead = Math.Max(LargestRequestedRead, buffer.Length);
            if (_produced >= _ceiling)
            {
                return 0;
            }

            int written = Math.Min(buffer.Length, _ceiling - _produced);
            buffer[..written].Fill((byte)'a');
            _produced += written;
            return written;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

internal static class OidcFormBody
{
    internal static IReadOnlyDictionary<string, string> Parse(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            string name = separator < 0 ? pair : pair[..separator];
            string value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            values[Uri.UnescapeDataString(name.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    internal static string ToInvariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
