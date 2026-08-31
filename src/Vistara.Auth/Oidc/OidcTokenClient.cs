using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Auth.Oidc;

/// <summary>
/// One authorization-code redemption. Both members are single-use secrets, so
/// the record validates their shape and never renders them.
/// </summary>
public sealed record OidcAuthorizationCodeRedemption
{
    public const int MaximumCodeLength = 4096;

    public OidcAuthorizationCodeRedemption(string code, string codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.Length > MaximumCodeLength ||
            code.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "An authorization code must be a bounded run of printable ASCII.",
                nameof(code));
        }

        if (!OidcPkce.IsWellFormedVerifier(codeVerifier))
        {
            throw new ArgumentException(
                "A PKCE code verifier must be 43-128 unreserved characters.",
                nameof(codeVerifier));
        }

        Code = code;
        CodeVerifier = codeVerifier;
    }

    public string Code { get; }

    public string CodeVerifier { get; }

    public override string ToString() => "[OidcAuthorizationCodeRedemption REDACTED]";
}

/// <summary>
/// The tokens Vistara keeps from a redemption. There is deliberately no
/// refresh token: the flow never requests offline access, and a long-lived
/// provider credential would become one more secret to protect.
/// </summary>
public sealed record OidcTokenSet(
    string IdToken,
    string? AccessToken,
    DateTimeOffset? AccessTokenExpiresAt)
{
    public override string ToString() => "[OidcTokenSet REDACTED]";
}

/// <summary>
/// Redeems an authorization code at the provider token endpoint. The endpoint
/// is re-checked against the configured authority, the request is bounded by
/// the configured timeout, and every provider failure is mapped to a redacted
/// error so an Entra correlation identifier, trace, or description can never
/// reach a browser or a log.
/// </summary>
public sealed class OidcTokenClient
{
    public const int MaximumResponseBytes = 128 * 1024;
    private const string JsonMediaType = "application/json";
    private const string FormMediaType = "application/x-www-form-urlencoded";

    private readonly HttpClient _httpClient;
    private readonly OidcProviderOptions _options;
    private readonly IOidcClientCredentialProvider _credentialProvider;
    private readonly IClock _clock;

    public OidcTokenClient(
        HttpClient httpClient,
        OidcProviderOptions options,
        IOidcClientCredentialProvider credentialProvider,
        IClock clock)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialProvider = credentialProvider ??
            throw new ArgumentNullException(nameof(credentialProvider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<Result<OidcTokenSet>> RedeemAuthorizationCodeAsync(
        OidcAuthorizationCodeRedemption redemption,
        OidcProviderMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redemption);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.IsAllowedEndpoint(metadata.TokenEndpoint) ||
            !string.IsNullOrEmpty(metadata.TokenEndpoint.Query))
        {
            return Result.Failure<OidcTokenSet>(OidcErrors.TokenExchangeFailed);
        }

        Result<OidcClientCredential> credential = await _credentialProvider
            .GetAsync(metadata.TokenEndpoint, cancellationToken)
            .ConfigureAwait(false);
        if (!credential.TryGetValue(out OidcClientCredential? clientCredential))
        {
            return Result.Failure<OidcTokenSet>(
                credential.Error ?? OidcErrors.ClientCredentialUnavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HttpTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(BuildForm(redemption, clientCredential)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(FormMediaType);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (IsTransientFailure(response.StatusCode))
            {
                return Result.Failure<OidcTokenSet>(OidcErrors.TokenEndpointUnavailable);
            }

            if (response.StatusCode != HttpStatusCode.OK ||
                !IsJson(response.Content.Headers.ContentType) ||
                response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return Result.Failure<OidcTokenSet>(OidcErrors.TokenExchangeFailed);
            }

            string? body = await OidcBoundedContent
                .ReadAsync(response.Content, MaximumResponseBytes, timeout.Token)
                .ConfigureAwait(false);
            return body is null
                ? Result.Failure<OidcTokenSet>(OidcErrors.TokenExchangeFailed)
                : ReadTokenSet(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return Result.Failure<OidcTokenSet>(OidcErrors.TokenEndpointUnavailable);
        }
#pragma warning restore CA1031
    }

    private IEnumerable<KeyValuePair<string, string>> BuildForm(
        OidcAuthorizationCodeRedemption redemption,
        OidcClientCredential credential)
    {
        yield return new KeyValuePair<string, string>("grant_type", "authorization_code");
        yield return new KeyValuePair<string, string>("client_id", _options.ClientId);
        yield return new KeyValuePair<string, string>("code", redemption.Code);
        yield return new KeyValuePair<string, string>(
            "redirect_uri",
            _options.RedirectUri.AbsoluteUri);
        yield return new KeyValuePair<string, string>("code_verifier", redemption.CodeVerifier);
        yield return new KeyValuePair<string, string>("scope", _options.ScopeParameter);
        if (credential.Kind == OidcClientCredentialKind.ClientAssertion)
        {
            yield return new KeyValuePair<string, string>(
                "client_assertion_type",
                OidcClientAssertion.AssertionType);
            yield return new KeyValuePair<string, string>("client_assertion", credential.Value);
        }
        else
        {
            yield return new KeyValuePair<string, string>("client_secret", credential.Value);
        }
    }

    private Result<OidcTokenSet> ReadTokenSet(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadString(document.RootElement, "id_token", out string? idToken) ||
                !TryReadString(document.RootElement, "token_type", out string? tokenType) ||
                !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<OidcTokenSet>(OidcErrors.TokenExchangeFailed);
            }

            _ = TryReadString(document.RootElement, "access_token", out string? accessToken);
            DateTimeOffset? expiresAt = accessToken is null
                ? null
                : ReadExpiry(document.RootElement);
            return Result.Success(new OidcTokenSet(idToken!, accessToken, expiresAt));
        }
        catch (JsonException)
        {
            return Result.Failure<OidcTokenSet>(OidcErrors.TokenExchangeFailed);
        }
    }

    private DateTimeOffset? ReadExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out JsonElement element))
        {
            return null;
        }

        long seconds = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out long value) => value,
            JsonValueKind.String when long.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long value) => value,
            _ => 0,
        };

        return seconds is > 0 and <= 86_400
            ? _clock.UtcNow.ToUniversalTime().AddSeconds(seconds)
            : null;
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = element.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool IsTransientFailure(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static bool IsJson(MediaTypeHeaderValue? contentType) =>
        contentType is not null &&
        (string.Equals(contentType.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase) ||
            contentType.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true);
}
