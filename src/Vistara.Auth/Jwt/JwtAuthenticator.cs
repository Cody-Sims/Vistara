using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.Jwt;

public sealed class JwtAuthenticator
{
    public const int MaximumTokenLength = 16 * 1024;
    public const int MaximumEncodedHeaderLength = 4 * 1024;
    public const string TenantIdClaim = "tenant_id";
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> SecurityCriticalHeaderNames =
        new(StringComparer.Ordinal) { "alg", "kid", "typ" };
    private static readonly HashSet<string> SecurityCriticalPayloadNames =
        new(StringComparer.Ordinal)
        {
            JwtRegisteredClaimNames.Iss,
            JwtRegisteredClaimNames.Aud,
            JwtRegisteredClaimNames.Sub,
            JwtRegisteredClaimNames.Jti,
            TenantIdClaim,
            JwtRegisteredClaimNames.Nbf,
            JwtRegisteredClaimNames.Exp,
        };

    private readonly Dictionary<string, JwtIssuerProfile> _profiles;
    private readonly IJwtMetadataSigningKeyResolver _metadataKeyResolver;
    private readonly IJwtTenantMembershipProvider _membershipProvider;
    private readonly IJwtRevocationStore _revocationStore;
    private readonly IClock _clock;
    private readonly JsonWebTokenHandler _handler = new()
    {
        MaximumTokenSizeInBytes = MaximumTokenLength,
    };

    public JwtAuthenticator(
        IReadOnlyCollection<JwtIssuerProfile> profiles,
        IJwtMetadataSigningKeyResolver metadataKeyResolver,
        IJwtTenantMembershipProvider membershipProvider,
        IJwtRevocationStore revocationStore,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _metadataKeyResolver = metadataKeyResolver ??
            throw new ArgumentNullException(nameof(metadataKeyResolver));
        _membershipProvider = membershipProvider ??
            throw new ArgumentNullException(nameof(membershipProvider));
        _revocationStore = revocationStore ??
            throw new ArgumentNullException(nameof(revocationStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        if (profiles.Count == 0)
        {
            throw new ArgumentException(
                "At least one JWT issuer profile is required.",
                nameof(profiles));
        }

        var profileMap = new Dictionary<string, JwtIssuerProfile>(StringComparer.Ordinal);
        foreach (JwtIssuerProfile profile in profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (!profileMap.TryAdd(profile.Issuer, profile))
            {
                throw new ArgumentException(
                    "JWT issuer profiles must have unique issuers.",
                    nameof(profiles));
            }
        }

        _profiles = profileMap;
    }

    public async ValueTask<Result<JwtPrincipal>> AuthenticateAsync(
        string? token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasBoundedCompactJwsFormat(token))
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.InvalidToken);
        }

        JsonWebToken unvalidatedToken;
        JwtRequiredClaims unvalidatedClaims;
        try
        {
            unvalidatedToken = _handler.ReadJsonWebToken(token);
            if (!HasUniqueSecurityCriticalMembers(unvalidatedToken) ||
                !TryReadRequiredClaims(unvalidatedToken, out unvalidatedClaims))
            {
                return Result.Failure<JwtPrincipal>(JwtErrors.InvalidToken);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or JsonException or SecurityTokenException)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.InvalidToken);
        }

        if (!_profiles.TryGetValue(unvalidatedClaims.Issuer, out JwtIssuerProfile? profile) ||
            !IsAllowedHeader(unvalidatedToken, profile))
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.InvalidToken);
        }

        IReadOnlyCollection<SecurityKey> signingKeys;
        try
        {
            signingKeys = profile.SigningKeys ??
                await _metadataKeyResolver.ResolveAsync(
                    profile.MetadataAddress!,
                    cancellationToken);
            signingKeys = JwtIssuerProfile.ValidateResolvedSigningKeys(
                signingKeys,
                profile.AllowedAlgorithms);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.ValidationUnavailable);
        }
#pragma warning restore CA1031

        SecurityKey[] matchingKeys = signingKeys
            .Where(key => string.Equals(key.KeyId, unvalidatedToken.Kid, StringComparison.Ordinal))
            .ToArray();
        if (matchingKeys.Length != 1)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.InvalidToken);
        }

        var validationParameters = new TokenValidationParameters
        {
            ClockSkew = MaximumClockSkew,
            IssuerSigningKeys = matchingKeys,
            LifetimeValidator = ValidateLifetime,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            TryAllIssuerSigningKeys = false,
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidAlgorithms = profile.AllowedAlgorithms,
            ValidAudience = profile.Audience,
            ValidIssuer = profile.Issuer,
            ValidTypes = profile.AllowedTypes,
        };

        TokenValidationResult validationResult;
#pragma warning disable CA1031
        try
        {
            validationResult = await _handler.ValidateTokenAsync(token, validationParameters);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.ValidationUnavailable);
        }
#pragma warning restore CA1031
        cancellationToken.ThrowIfCancellationRequested();
        if (!validationResult.IsValid ||
            validationResult.SecurityToken is not JsonWebToken validatedToken ||
            !TryReadRequiredClaims(validatedToken, out JwtRequiredClaims validatedClaims) ||
            validatedClaims != unvalidatedClaims)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.InvalidToken);
        }

        bool isRevoked;
#pragma warning disable CA1031
        try
        {
            isRevoked = await _revocationStore.IsRevokedAsync(
                validatedClaims.Issuer,
                validatedClaims.JwtId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.ValidationUnavailable);
        }
#pragma warning restore CA1031
        if (isRevoked)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.Revoked);
        }

        JwtTenantMembership? membership;
#pragma warning disable CA1031
        try
        {
            membership = await _membershipProvider.FindAsync(
                validatedClaims.Issuer,
                validatedClaims.Subject,
                validatedClaims.TenantId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.ValidationUnavailable);
        }
#pragma warning restore CA1031
        if (membership is null ||
            membership.TenantId != validatedClaims.TenantId ||
            membership.TenantStatus != TenantStatus.Active ||
            membership.MembershipStatus != MembershipStatus.Active ||
            !Enum.IsDefined(membership.Role))
        {
            return Result.Failure<JwtPrincipal>(JwtErrors.TenantAccessDenied);
        }

        return Result.Success(
            new JwtPrincipal(
                membership.UserId,
                membership.TenantId,
                membership.Role,
                validatedClaims.Issuer,
                validatedClaims.Subject,
                validatedClaims.JwtId));
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

    private static bool IsAllowedHeader(
        JsonWebToken token,
        JwtIssuerProfile profile) =>
        !string.IsNullOrWhiteSpace(token.Kid) &&
        profile.AllowedAlgorithms.Contains(token.Alg, StringComparer.Ordinal) &&
        profile.AllowedTypes.Contains(token.Typ, StringComparer.Ordinal);

    private static bool HasUniqueSecurityCriticalMembers(JsonWebToken token) =>
        HasUniqueJsonMembers(
                token.EncodedHeader,
                SecurityCriticalHeaderNames) &&
            HasUniqueJsonMembers(
                token.EncodedPayload,
                SecurityCriticalPayloadNames);

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

    private static bool TryReadRequiredClaims(
        JsonWebToken token,
        out JwtRequiredClaims requiredClaims)
    {
        requiredClaims = default;
        Claim[] claims = token.Claims.ToArray();
        if (!TryGetSingleStringClaim(claims, JwtRegisteredClaimNames.Iss, out string issuer) ||
            !TryGetSingleStringClaim(claims, JwtRegisteredClaimNames.Sub, out string subject) ||
            !TryGetSingleStringClaim(claims, JwtRegisteredClaimNames.Jti, out string jwtId) ||
            !TryGetSingleStringClaim(claims, TenantIdClaim, out string tenantIdText) ||
            !Guid.TryParse(tenantIdText, out Guid tenantGuid) ||
            tenantGuid == Guid.Empty ||
            tenantGuid.Version != 7 ||
            token.Audiences.Take(2).Count() != 1 ||
            CountClaims(claims, JwtRegisteredClaimNames.Exp) != 1 ||
            CountClaims(claims, JwtRegisteredClaimNames.Nbf) > 1)
        {
            return false;
        }

        requiredClaims = new JwtRequiredClaims(
            issuer,
            subject,
            jwtId,
            new TenantId(tenantGuid));
        return true;
    }

    private static bool TryGetSingleStringClaim(
        IEnumerable<Claim> claims,
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

    private static int CountClaims(IEnumerable<Claim> claims, string claimType) =>
        claims.Count(claim =>
            string.Equals(claim.Type, claimType, StringComparison.Ordinal));

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
        return (!notBefore.HasValue || notBefore.Value <= now + MaximumClockSkew) &&
            expires.Value >= now - MaximumClockSkew;
    }

    private readonly record struct JwtRequiredClaims(
        string Issuer,
        string Subject,
        string JwtId,
        TenantId TenantId);
}
