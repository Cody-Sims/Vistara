using Microsoft.IdentityModel.Tokens;

namespace Vistara.Auth.Jwt;

public sealed class JwtIssuerProfile
{
    private static readonly HashSet<string> SupportedAsymmetricAlgorithms =
    [
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256,
        SecurityAlgorithms.RsaSsaPssSha384,
        SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512,
    ];

    private JwtIssuerProfile(
        string profileId,
        string issuer,
        string audience,
        IReadOnlyCollection<SecurityKey>? signingKeys,
        Uri? metadataAddress,
        IReadOnlyCollection<string> allowedAlgorithms,
        IReadOnlyCollection<string>? allowedTypes)
    {
        ProfileId = ValidateProfileId(profileId);
        Issuer = ValidateIssuer(issuer);
        Audience = ValidateAudience(audience);
        AllowedAlgorithms = Array.AsReadOnly(ValidateAlgorithms(allowedAlgorithms));
        AllowedTypes = Array.AsReadOnly(ValidateTypes(allowedTypes));

        if ((signingKeys is null) == (metadataAddress is null))
        {
            throw new ArgumentException(
                "Exactly one signing-key source must be configured.");
        }

        if (signingKeys is not null)
        {
            SigningKeys = Array.AsReadOnly(
                ValidateSigningKeys(signingKeys, AllowedAlgorithms));
        }
        else
        {
            if (metadataAddress is null ||
                !metadataAddress.IsAbsoluteUri ||
                metadataAddress.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(metadataAddress.Fragment))
            {
                throw new ArgumentException(
                    "JWT metadata addresses must be absolute HTTPS URLs without fragments.",
                    nameof(metadataAddress));
            }

            MetadataAddress = metadataAddress;
        }
    }

    public string ProfileId { get; }

    public string Issuer { get; }

    public string Audience { get; }

    public IReadOnlyCollection<SecurityKey>? SigningKeys { get; }

    public Uri? MetadataAddress { get; }

    public IReadOnlyCollection<string> AllowedAlgorithms { get; }

    public IReadOnlyCollection<string> AllowedTypes { get; }

    public static JwtIssuerProfile ForSigningKeys(
        string profileId,
        string issuer,
        string audience,
        IReadOnlyCollection<SecurityKey> signingKeys,
        IReadOnlyCollection<string> allowedAlgorithms,
        IReadOnlyCollection<string>? allowedTypes = null) =>
        new(
            profileId,
            issuer,
            audience,
            signingKeys,
            null,
            allowedAlgorithms,
            allowedTypes);

    public static JwtIssuerProfile ForMetadata(
        string profileId,
        string issuer,
        string audience,
        Uri metadataAddress,
        IReadOnlyCollection<string> allowedAlgorithms,
        IReadOnlyCollection<string>? allowedTypes = null) =>
        new(
            profileId,
            issuer,
            audience,
            null,
            metadataAddress,
            allowedAlgorithms,
            allowedTypes);

    internal static SecurityKey[] ValidateResolvedSigningKeys(
        IReadOnlyCollection<SecurityKey> signingKeys,
        IReadOnlyCollection<string> allowedAlgorithms) =>
        ValidateSigningKeys(signingKeys, allowedAlgorithms);

    private static string ValidateProfileId(string profileId)
    {
        string candidate = profileId?.Trim() ?? string.Empty;
        if (candidate.Length is < 1 or > 100 ||
            candidate.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The JWT issuer profile identifier is invalid.", nameof(profileId));
        }

        return candidate;
    }

    private static string ValidateIssuer(string issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer) ||
            issuer.Length > 2_048 ||
            !string.Equals(issuer, issuer.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(issuer, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "JWT issuers must be absolute HTTPS URLs without query strings or fragments.",
                nameof(issuer));
        }

        return issuer;
    }

    private static string ValidateAudience(string audience)
    {
        string candidate = audience?.Trim() ?? string.Empty;
        if (candidate.Length is < 1 or > 500)
        {
            throw new ArgumentException("The JWT audience is invalid.", nameof(audience));
        }

        return candidate;
    }

    private static string[] ValidateAlgorithms(
        IReadOnlyCollection<string> allowedAlgorithms)
    {
        ArgumentNullException.ThrowIfNull(allowedAlgorithms);
        string[] algorithms = allowedAlgorithms.Distinct(StringComparer.Ordinal).ToArray();
        if (algorithms.Length == 0 ||
            algorithms.Length != allowedAlgorithms.Count ||
            algorithms.Any(algorithm => !SupportedAsymmetricAlgorithms.Contains(algorithm)))
        {
            throw new ArgumentException(
                "JWT algorithms must be a non-empty unique allowlist of supported asymmetric algorithms.",
                nameof(allowedAlgorithms));
        }

        return algorithms;
    }

    private static string[] ValidateTypes(
        IReadOnlyCollection<string>? allowedTypes)
    {
        IReadOnlyCollection<string> configured = allowedTypes ?? ["at+jwt"];
        string[] types = configured.Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length == 0 ||
            types.Length != configured.Count ||
            types.Any(type => string.IsNullOrWhiteSpace(type) || type.Length > 100))
        {
            throw new ArgumentException(
                "JWT token types must be a non-empty unique allowlist.",
                nameof(allowedTypes));
        }

        return types;
    }

    private static SecurityKey[] ValidateSigningKeys(
        IReadOnlyCollection<SecurityKey> signingKeys,
        IReadOnlyCollection<string> allowedAlgorithms)
    {
        ArgumentNullException.ThrowIfNull(signingKeys);
        SecurityKey[] keys = signingKeys.ToArray();
        if (keys.Length == 0 ||
            keys.Any(key =>
                key is not (RsaSecurityKey or ECDsaSecurityKey) ||
                string.IsNullOrWhiteSpace(key.KeyId) ||
                key is RsaSecurityKey rsaKey && rsaKey.KeySize < 2048) ||
            keys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            throw new ArgumentException(
                "JWT signing keys must be a non-empty set of uniquely identified asymmetric keys.",
                nameof(signingKeys));
        }

        if (allowedAlgorithms.Any(algorithm =>
                !keys.Any(key => IsCompatible(key, algorithm))) ||
            keys.Any(key =>
                !allowedAlgorithms.Any(algorithm => IsCompatible(key, algorithm))))
        {
            throw new ArgumentException(
                "JWT signing key types must match the configured algorithms.",
                nameof(signingKeys));
        }

        return keys;
    }

    private static bool IsCompatible(SecurityKey key, string algorithm) =>
        key switch
        {
            RsaSecurityKey =>
                algorithm.StartsWith("RS", StringComparison.Ordinal) ||
                algorithm.StartsWith("PS", StringComparison.Ordinal),
            ECDsaSecurityKey ecdsaKey => algorithm switch
            {
                SecurityAlgorithms.EcdsaSha256 => ecdsaKey.KeySize == 256,
                SecurityAlgorithms.EcdsaSha384 => ecdsaKey.KeySize == 384,
                SecurityAlgorithms.EcdsaSha512 => ecdsaKey.KeySize == 521,
                _ => false,
            },
            _ => false,
        };
}
