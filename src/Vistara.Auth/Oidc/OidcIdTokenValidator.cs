using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Auth.Oidc;

/// <summary>
/// The directory identity a validated token proves. The stable external key is
/// the (directory tenant, object identifier) pair. Email and display name are
/// profile attributes only: a mailbox can be renamed or reassigned, so it can
/// never authorize anything.
/// </summary>
public sealed record OidcIdentity(
    Guid DirectoryTenantId,
    string ObjectId,
    string Subject,
    string? Email,
    string? DisplayName,
    string Issuer)
{
    public override string ToString() =>
        $"{nameof(OidcIdentity)} {{ DirectoryTenantId = {DirectoryTenantId}, ObjectId = {ObjectId}, Subject = [REDACTED], Email = [REDACTED], DisplayName = [REDACTED], Issuer = {Issuer} }}";
}

/// <summary>
/// The per-callback facts a token must be bound to: the provider material this
/// sign-in resolved, and the nonce the login store issued for this browser.
/// </summary>
public sealed record OidcIdTokenValidationContext
{
    public OidcIdTokenValidationContext(
        OidcProviderMetadata metadata,
        string expectedNonce,
        string? accessToken = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNonce);
        Metadata = metadata;
        ExpectedNonce = expectedNonce;
        AccessToken = accessToken;
    }

    public OidcProviderMetadata Metadata { get; }

    public string ExpectedNonce { get; }

    public string? AccessToken { get; }

    public override string ToString() => "[OidcIdTokenValidationContext REDACTED]";
}

/// <summary>
/// Validates a Microsoft Entra v2.0 identity token against the configured
/// provider. The token arrives from a browser redirect, so every step assumes
/// the value is hostile: the declared algorithm must be on the allowlist
/// before any key is touched, the key identifier must resolve to exactly one
/// published asymmetric key, and the signature, issuer, audience, lifetime,
/// nonce, directory tenant, and object identifier must all hold before an
/// identity is returned.
/// </summary>
public sealed class OidcIdTokenValidator
{
    public const int MaximumTokenLength = 16 * 1024;
    public const int MaximumEncodedHeaderLength = 4 * 1024;
    public const string EntraVersionClaim = "ver";
    public const string EntraTenantClaim = "tid";
    public const string EntraObjectIdClaim = "oid";
    public const string SupportedTokenVersion = "2.0";

    private static readonly Guid PersonalAccountTenantId =
        Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");

    private static readonly HashSet<string> SecurityCriticalHeaderNames =
        new(StringComparer.Ordinal) { "alg", "kid", "typ" };

    private static readonly HashSet<string> SecurityCriticalPayloadNames =
        new(StringComparer.Ordinal)
        {
            JwtRegisteredClaimNames.Iss,
            JwtRegisteredClaimNames.Aud,
            JwtRegisteredClaimNames.Sub,
            JwtRegisteredClaimNames.Exp,
            JwtRegisteredClaimNames.Nbf,
            JwtRegisteredClaimNames.Iat,
            JwtRegisteredClaimNames.Nonce,
            JwtRegisteredClaimNames.AtHash,
            EntraTenantClaim,
            EntraObjectIdClaim,
            EntraVersionClaim,
        };

    private readonly OidcProviderOptions _options;
    private readonly IClock _clock;
    private readonly JsonWebTokenHandler _handler = new()
    {
        MaximumTokenSizeInBytes = MaximumTokenLength,
    };

    public OidcIdTokenValidator(OidcProviderOptions options, IClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<Result<OidcIdentity>> ValidateAsync(
        string? idToken,
        OidcIdTokenValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasBoundedCompactJwsFormat(idToken))
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }

        JsonWebToken unvalidated;
        try
        {
            unvalidated = _handler.ReadJsonWebToken(idToken);
            if (!HasUniqueSecurityCriticalMembers(unvalidated) ||
                !IsAllowedHeader(unvalidated))
            {
                return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or JsonException or SecurityTokenException)
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }

        SecurityKey[] matchingKeys = context.Metadata.SigningKeys
            .Where(key => string.Equals(key.KeyId, unvalidated.Kid, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matchingKeys.Length != 1)
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }

        TokenValidationResult validationResult;
        try
        {
            validationResult = await _handler
                .ValidateTokenAsync(idToken, CreateValidationParameters(matchingKeys))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }
#pragma warning restore CA1031

        cancellationToken.ThrowIfCancellationRequested();
        if (!validationResult.IsValid ||
            validationResult.SecurityToken is not JsonWebToken validated)
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }

        return ReadIdentity(validated, context);
    }

    private Result<OidcIdentity> ReadIdentity(
        JsonWebToken validated,
        OidcIdTokenValidationContext context)
    {
        Claim[] claims = validated.Claims.ToArray();
        if (!TryGetSingleStringClaim(claims, EntraVersionClaim, out string version) ||
            !string.Equals(version, SupportedTokenVersion, StringComparison.Ordinal) ||
            !TryGetSingleStringClaim(claims, JwtRegisteredClaimNames.Sub, out string subject) ||
            !TryGetSingleStringClaim(claims, JwtRegisteredClaimNames.Nonce, out string nonce) ||
            !TryGetSingleStringClaim(claims, EntraObjectIdClaim, out string objectId) ||
            !Guid.TryParseExact(objectId, "D", out Guid objectGuid) ||
            objectGuid == Guid.Empty)
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }

        // A stolen or replayed token from a different sign-in fails here, and
        // the comparison is fixed time so it cannot be probed character by
        // character.
        if (!FixedTimeEquals(nonce, context.ExpectedNonce) ||
            !IsFreshIssueTime(validated) ||
            !HasMatchingAccessTokenHash(claims, context.AccessToken))
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }

        if (!TryGetSingleStringClaim(claims, EntraTenantClaim, out string tenantIdValue) ||
            !Guid.TryParseExact(tenantIdValue, "D", out Guid tenantId) ||
            tenantId == Guid.Empty)
        {
            return Result.Failure<OidcIdentity>(OidcErrors.InvalidIdToken);
        }

        if (tenantId != _options.DirectoryTenantId || tenantId == PersonalAccountTenantId)
        {
            return Result.Failure<OidcIdentity>(OidcErrors.TenantNotAllowed);
        }

        return Result.Success(
            new OidcIdentity(
                tenantId,
                objectGuid.ToString("D"),
                subject,
                ReadProfileAttribute(claims, "preferred_username") ??
                    ReadProfileAttribute(claims, JwtRegisteredClaimNames.Email),
                ReadProfileAttribute(claims, "name"),
                _options.ExpectedIssuer));
    }

    private TokenValidationParameters CreateValidationParameters(SecurityKey[] signingKeys) =>
        new()
        {
            AudienceValidator = (audiences, _, _) =>
                HasSingleExactAudience(audiences, _options.ClientId),
            ClockSkew = _options.ClockSkew,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            IssuerSigningKeys = signingKeys,
            LifetimeValidator = ValidateLifetime,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            TryAllIssuerSigningKeys = false,
            ValidAlgorithms = _options.AllowedSigningAlgorithms,
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidAudience = _options.ClientId,
            ValidIssuer = _options.ExpectedIssuer,
        };

    private bool IsAllowedHeader(JsonWebToken token) =>
        !string.IsNullOrWhiteSpace(token.Kid) &&
        _options.AllowedSigningAlgorithms.Contains(token.Alg, StringComparer.Ordinal) &&
        (string.IsNullOrEmpty(token.Typ) ||
            string.Equals(token.Typ, "JWT", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Entra stamps <c>iat</c> at issue time. A token claiming to be issued far
    /// in the future is either clock-skewed beyond the configured tolerance or
    /// forged, and neither should open a session.
    /// </summary>
    private bool IsFreshIssueTime(JsonWebToken token)
    {
        if (!token.TryGetPayloadValue(JwtRegisteredClaimNames.Iat, out long issuedAt))
        {
            return true;
        }

        return DateTimeOffset.FromUnixTimeSeconds(issuedAt) <=
            _clock.UtcNow.ToUniversalTime() + _options.ClockSkew;
    }

    private bool ValidateLifetime(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        _ = securityToken;
        _ = validationParameters;
        if (!expires.HasValue ||
            (notBefore.HasValue && notBefore.Value > expires.Value))
        {
            return false;
        }

        DateTime now = _clock.UtcNow.UtcDateTime;
        return (!notBefore.HasValue || notBefore.Value <= now + _options.ClockSkew) &&
            expires.Value >= now - _options.ClockSkew;
    }

    /// <summary>
    /// Binds the identity token to the access token that arrived with it, so a
    /// token pair cannot be assembled from two different responses.
    /// </summary>
    private static bool HasMatchingAccessTokenHash(Claim[] claims, string? accessToken)
    {
        if (accessToken is null ||
            !TryGetSingleStringClaim(claims, JwtRegisteredClaimNames.AtHash, out string atHash))
        {
            return true;
        }

        byte[] material = Encoding.ASCII.GetBytes(accessToken);
        try
        {
            byte[] digest = SHA256.HashData(material);
            string expected = Base64UrlEncoder.Encode(digest.AsSpan(0, digest.Length / 2).ToArray());
            return FixedTimeEquals(atHash, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static string? ReadProfileAttribute(Claim[] claims, string claimType) =>
        TryGetSingleStringClaim(claims, claimType, out string value) ? value : null;

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool HasBoundedCompactJwsFormat(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.Length > MaximumTokenLength)
        {
            return false;
        }

        int firstSeparator = token.IndexOf('.');
        if (firstSeparator is <= 0 or > MaximumEncodedHeaderLength)
        {
            return false;
        }

        int secondSeparator = token.IndexOf('.', firstSeparator + 1);
        return secondSeparator > firstSeparator + 1 &&
            secondSeparator < token.Length - 1 &&
            token.IndexOf('.', secondSeparator + 1) < 0;
    }

    private static bool HasUniqueSecurityCriticalMembers(JsonWebToken token) =>
        HasUniqueJsonMembers(token.EncodedHeader, SecurityCriticalHeaderNames) &&
        HasUniqueJsonMembers(token.EncodedPayload, SecurityCriticalPayloadNames);

    private static bool HasUniqueJsonMembers(
        string encodedJson,
        HashSet<string> securityCriticalNames)
    {
        byte[] json = Base64UrlEncoder.DecodeBytes(encodedJson);
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (securityCriticalNames.Contains(property.Name) &&
                    !observed.Add(property.Name))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static bool TryGetSingleStringClaim(
        Claim[] claims,
        string claimType,
        out string value)
    {
        Claim[] matches = claims
            .Where(claim => string.Equals(claim.Type, claimType, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].ValueType != ClaimValueTypes.String ||
            string.IsNullOrWhiteSpace(matches[0].Value))
        {
            value = string.Empty;
            return false;
        }

        value = matches[0].Value;
        return true;
    }

    private static bool HasSingleExactAudience(
        IEnumerable<string>? audiences,
        string configuredAudience)
    {
        if (audiences is null)
        {
            return false;
        }

        using IEnumerator<string> enumerator = audiences.GetEnumerator();
        return enumerator.MoveNext() &&
            string.Equals(enumerator.Current, configuredAudience, StringComparison.Ordinal) &&
            !enumerator.MoveNext();
    }
}
