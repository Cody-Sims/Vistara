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
    private bool _hasAttempted;

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

    /// <summary>
    /// Resolution policy, in order:
    ///
    /// 1. A document inside <see cref="OidcProviderOptions.MetadataCacheLifetime"/>
    ///    is served with no provider call.
    /// 2. Otherwise a fetch is warranted, but only if the last attempt is older
    ///    than <see cref="OidcProviderOptions.MetadataRefreshBackoff"/>. The
    ///    backoff applies to the cold and expired paths as well as to forced
    ///    refreshes, so a provider outage produces one attempt per backoff
    ///    window no matter how many callers arrive; every other caller returns
    ///    immediately without queueing behind a network timeout.
    /// 3. When a fetch is suppressed or fails, an expired document is served
    ///    only inside the explicit
    ///    <see cref="OidcProviderOptions.MetadataStaleWhileUnavailable"/>
    ///    window. Beyond it, the cache fails closed rather than authenticating
    ///    against keys of unbounded age.
    /// </summary>
    private async ValueTask<Result<OidcProviderMetadata>> AcquireAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OidcProviderMetadata? snapshot = Volatile.Read(ref _cached);
        DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
        if (!forceRefresh && IsFresh(snapshot, now))
        {
            return Result.Success(snapshot!);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A caller that went away while queued behind the gate must not
            // start a provider request on its way out. SemaphoreSlim can hand
            // the gate to a waiter at the same moment its token is cancelled,
            // so re-check before spending any budget.
            cancellationToken.ThrowIfCancellationRequested();
            now = _clock.UtcNow.ToUniversalTime();
            if (!forceRefresh && IsFresh(_cached, now))
            {
                return Result.Success(_cached!);
            }

            if (_hasAttempted && now - _lastAttemptAt < _options.MetadataRefreshBackoff)
            {
                return Resolve(_cached, now);
            }

            DateTimeOffset previousAttemptAt = _lastAttemptAt;
            bool previousHasAttempted = _hasAttempted;
            _lastAttemptAt = now;
            _hasAttempted = true;
            OidcProviderMetadata? fetched;
            try
            {
                fetched = await FetchAsync(now, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Only a caller cancellation reaches here: a provider timeout
                // is resolved to a failed fetch inside ReadDocumentAsync. The
                // caller went away before this attempt learned anything about
                // the provider, so the attempt must not spend the refresh
                // budget. Restoring the previous attempt state under the gate
                // stops a client that connects and disconnects in a loop from
                // holding the cache in a permanent suppressed outage and
                // denying every legitimate sign-in.
                _lastAttemptAt = previousAttemptAt;
                _hasAttempted = previousHasAttempted;
                throw;
            }

            if (fetched is not null)
            {
                Volatile.Write(ref _cached, fetched);
                return Result.Success(fetched);
            }

            return Resolve(_cached, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(OidcProviderMetadata? candidate, DateTimeOffset now) =>
        candidate is not null && now - candidate.RetrievedAt < _options.MetadataCacheLifetime;

    private Result<OidcProviderMetadata> Resolve(
        OidcProviderMetadata? candidate,
        DateTimeOffset now)
    {
        if (candidate is null)
        {
            return Result.Failure<OidcProviderMetadata>(OidcErrors.MetadataUnavailable);
        }

        TimeSpan age = now - candidate.RetrievedAt;
        return age < _options.MetadataCacheLifetime + _options.MetadataStaleWhileUnavailable
            ? Result.Success(candidate)
            : Result.Failure<OidcProviderMetadata>(OidcErrors.MetadataUnavailable);
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

    /// <summary>
    /// Reads one provider document under an HTTP budget the library owns.
    ///
    /// The budget lives in its own token source rather than in a linked source
    /// with a deadline, so a cancelled read can be attributed exactly. Asking
    /// whether the caller's token is cancelled is not a sound test: a caller
    /// that gives up moments after a genuine timeout would make a real outage
    /// look like a disconnect, and a timeout that fires while a caller is
    /// walking away would make a disconnect look like an outage. Asking which
    /// source fired answers the question directly. The timeout is checked
    /// first, because a budget that has already elapsed is an outage signal
    /// whatever the caller did afterwards.
    /// </summary>
    private async Task<string?> ReadDocumentAsync(Uri address, CancellationToken cancellationToken)
    {
        using var providerBudget = new CancellationTokenSource(_options.HttpTimeout);
        using CancellationTokenSource attempt = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, providerBudget.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
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
                    .ReadAsync(response.Content, MaximumDocumentBytes, attempt.Token)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (providerBudget.IsCancellationRequested)
        {
            // The library's own HTTP budget elapsed. The provider is
            // unresponsive, which is a real outage observation and must spend
            // the refresh backoff like any other failed attempt.
            return null;
        }
        catch (OperationCanceledException)
        {
            // The caller went away. Nothing was learned about the provider, so
            // this must not count as an attempt.
            throw;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            // Transport, TLS, and protocol failures are all reported as one
            // redacted unavailability so a provider error body can never reach
            // a Vistara response or log.
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
