using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Auth.Oidc;

/// <summary>
/// The provider endpoints and signing keys one sign-in needs. The record is a
/// validated projection of the discovery document, never the document itself.
/// </summary>
public sealed record OidcProviderMetadata(
    string Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri JwksUri,
    Uri? EndSessionEndpoint,
    IReadOnlyCollection<SecurityKey> SigningKeys,
    DateTimeOffset RetrievedAt);

public interface IOidcMetadataProvider
{
    ValueTask<Result<OidcProviderMetadata>> GetAsync(CancellationToken cancellationToken);

    ValueTask<Result<OidcProviderMetadata>> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Retrieves and caches the provider discovery document and signing keys.
/// The discovery document is untrusted input from the network, so the reader
/// bounds status, media type, and byte count, pins the issuer to the
/// configured authority, and refuses any endpoint that leaves the configured
/// authority host. That last check is what stops a poisoned or hijacked
/// discovery document from turning the API into a server-side request forgery
/// proxy against cloud metadata or internal services.
/// </summary>
public sealed class OidcMetadataCache : IOidcMetadataProvider, IDisposable
{
    public const int MaximumDocumentBytes = 256 * 1024;
    public const int MaximumSigningKeys = 20;
    public const int MinimumRsaKeySize = 2048;
    private const string JsonMediaType = "application/json";

    private readonly HttpClient _httpClient;
    private readonly OidcProviderOptions _options;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OidcProviderMetadata? _cached;
    private DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;

    public OidcMetadataCache(
        HttpClient httpClient,
        OidcProviderOptions options,
        IClock clock)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask<Result<OidcProviderMetadata>> GetAsync(CancellationToken cancellationToken) =>
        AcquireAsync(forceRefresh: false, cancellationToken);

    /// <summary>
    /// Forces a signing-key refresh, for example after a token arrived with an
    /// unknown key identifier. The refresh is rate limited so an attacker who
    /// can mint unknown key identifiers cannot use Vistara to hammer the
    /// provider.
    /// </summary>
    public ValueTask<Result<OidcProviderMetadata>> RefreshAsync(
        CancellationToken cancellationToken) =>
        AcquireAsync(forceRefresh: true, cancellationToken);

    public void Dispose() => _gate.Dispose();

    private async ValueTask<Result<OidcProviderMetadata>> AcquireAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
        if (TryUseCache(forceRefresh, now, out OidcProviderMetadata? cached))
        {
            return Result.Success(cached);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _clock.UtcNow.ToUniversalTime();
            if (TryUseCache(forceRefresh, now, out cached))
            {
                return Result.Success(cached);
            }

            _lastAttemptAt = now;
            OidcProviderMetadata? fetched = await FetchAsync(now, cancellationToken)
                .ConfigureAwait(false);
            if (fetched is not null)
            {
                _cached = fetched;
                return Result.Success(fetched);
            }

            // A failed refresh must never discard a document that is still
            // inside its lifetime; a transient provider outage should not sign
            // every user out.
            return _cached is not null && now - _cached.RetrievedAt < _options.MetadataCacheLifetime
                ? Result.Success(_cached)
                : Result.Failure<OidcProviderMetadata>(OidcErrors.MetadataUnavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryUseCache(
        bool forceRefresh,
        DateTimeOffset now,
        out OidcProviderMetadata cached)
    {
        OidcProviderMetadata? candidate = _cached;
        cached = candidate!;
        if (candidate is null)
        {
            return false;
        }

        return forceRefresh
            ? now - _lastAttemptAt < _options.MetadataRefreshBackoff
            : now - candidate.RetrievedAt < _options.MetadataCacheLifetime;
    }

    private async Task<OidcProviderMetadata?> FetchAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string? document = await ReadDocumentAsync(_options.MetadataAddress, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = OpenIdConnectConfiguration.Create(document);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031

        if (!string.Equals(configuration.Issuer, _options.ExpectedIssuer, StringComparison.Ordinal) ||
            !TryReadEndpoint(configuration.AuthorizationEndpoint, out Uri? authorizationEndpoint) ||
            !TryReadEndpoint(configuration.TokenEndpoint, out Uri? tokenEndpoint) ||
            !TryReadEndpoint(configuration.JwksUri, out Uri? jwksUri))
        {
            return null;
        }

        Uri? endSessionEndpoint =
            TryReadEndpoint(configuration.EndSessionEndpoint, out Uri? parsedEndSession)
                ? parsedEndSession
                : null;

        string? keySetDocument = await ReadDocumentAsync(jwksUri, cancellationToken)
            .ConfigureAwait(false);
        if (keySetDocument is null)
        {
            return null;
        }

        SecurityKey[] signingKeys = ReadSigningKeys(keySetDocument);
        return signingKeys.Length == 0
            ? null
            : new OidcProviderMetadata(
                configuration.Issuer,
                authorizationEndpoint,
                tokenEndpoint,
                jwksUri,
                endSessionEndpoint,
                Array.AsReadOnly(signingKeys),
                now);
    }

    private async Task<string?> ReadDocumentAsync(Uri address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HttpTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            // A followed redirect would mean the body came from a URL that
            // never passed the authority check performed before the request.
            if (!OidcRequestIntegrity.CameFromRequestedUri(response, address))
            {
                return null;
            }

            return response.StatusCode != HttpStatusCode.OK ||
                !IsJson(response.Content.Headers.ContentType) ||
                response.Content.Headers.ContentLength > MaximumDocumentBytes
                ? null
                : await OidcBoundedContent
                    .ReadAsync(response.Content, MaximumDocumentBytes, timeout.Token)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            // Transport, TLS, timeout, and protocol failures are all reported
            // as one redacted unavailability so a provider error body can never
            // reach a Vistara response or log.
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Keeps only asymmetric keys that carry a unique identifier, declare a
    /// signing use, and match an algorithm the deployment allows. Symmetric
    /// keys are dropped outright: accepting one would let a published key
    /// double as an HMAC secret and defeat signature validation entirely.
    /// </summary>
    private SecurityKey[] ReadSigningKeys(string keySetDocument)
    {
        JsonWebKeySet keySet;
        try
        {
            keySet = new JsonWebKeySet(keySetDocument);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return [];
        }
#pragma warning restore CA1031

        JsonWebKey[] candidates = keySet.Keys
            .Where(IsUsableSigningKey)
            .Take(MaximumSigningKeys + 1)
            .ToArray();
        if (candidates.Length > MaximumSigningKeys)
        {
            return [];
        }

        HashSet<string> duplicateKeyIds = candidates
            .GroupBy(key => key.Kid, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(key => !duplicateKeyIds.Contains(key.Kid))
            .Cast<SecurityKey>()
            .ToArray();
    }

    private bool IsUsableSigningKey(JsonWebKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Kid) ||
            (!string.IsNullOrEmpty(key.Use) &&
                !string.Equals(key.Use, "sig", StringComparison.Ordinal)))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(key.Alg) &&
            !_options.AllowedSigningAlgorithms.Contains(key.Alg, StringComparer.Ordinal))
        {
            return false;
        }

        return key.Kty switch
        {
            JsonWebAlgorithmsKeyTypes.RSA =>
                !string.IsNullOrEmpty(key.N) &&
                !string.IsNullOrEmpty(key.E) &&
                key.KeySize >= MinimumRsaKeySize &&
                _options.AllowedSigningAlgorithms.Any(IsRsaAlgorithm),
            JsonWebAlgorithmsKeyTypes.EllipticCurve =>
                !string.IsNullOrEmpty(key.X) &&
                !string.IsNullOrEmpty(key.Y) &&
                !string.IsNullOrEmpty(key.Crv) &&
                _options.AllowedSigningAlgorithms.Any(IsEcdsaAlgorithm),
            _ => false,
        };
    }

    private bool TryReadEndpoint(string? value, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) ||
            !_options.IsAllowedEndpoint(parsed))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static bool IsRsaAlgorithm(string algorithm) =>
        algorithm.StartsWith("RS", StringComparison.Ordinal) ||
        algorithm.StartsWith("PS", StringComparison.Ordinal);

    private static bool IsEcdsaAlgorithm(string algorithm) =>
        algorithm.StartsWith("ES", StringComparison.Ordinal);

    private static bool IsJson(MediaTypeHeaderValue? contentType) =>
        contentType is not null &&
        (string.Equals(contentType.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase) ||
            contentType.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true);
}
