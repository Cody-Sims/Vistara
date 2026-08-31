using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// OpenID Connect Core 3.1.3.6 derives at_hash from the token's signing
/// algorithm, so an RS384 token hashes with SHA-384 and an RS512 token with
/// SHA-512. A validator fixed to SHA-256 either rejects every valid larger
/// token or, worse, drops the binding when it cannot compute the hash.
/// </summary>
public sealed class OidcAccessTokenHashTests : IDisposable
{
    private const string Nonce = "sTZ5wUdpQZ2ULrDkYWK-Y0M4nJ3o1cAqQ8W6VpQoLmE";
    private const string ObjectId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string AccessToken = "access-token-value";

    private readonly RSA _rsa = RSA.Create(3072);
    private readonly Dictionary<string, ECDsa> _curves = new(StringComparer.Ordinal)
    {
        ["ES256"] = ECDsa.Create(ECCurve.NamedCurves.nistP256),
        ["ES384"] = ECDsa.Create(ECCurve.NamedCurves.nistP384),
        ["ES512"] = ECDsa.Create(ECCurve.NamedCurves.nistP521),
    };

    private readonly FixedOidcClock _clock = new();

    public void Dispose()
    {
        _rsa.Dispose();
        foreach (ECDsa curve in _curves.Values)
        {
            curve.Dispose();
        }
    }

    [Theory]
    [InlineData("RS256", "SHA256")]
    [InlineData("RS384", "SHA384")]
    [InlineData("RS512", "SHA512")]
    [InlineData("PS256", "SHA256")]
    [InlineData("PS384", "SHA384")]
    [InlineData("PS512", "SHA512")]
    [InlineData("ES256", "SHA256")]
    [InlineData("ES384", "SHA384")]
    [InlineData("ES512", "SHA512")]
    public async Task Access_token_hash_uses_the_digest_the_signing_algorithm_defines(
        string algorithm,
        string expectedDigest)
    {
        SecurityKey key = KeyFor(algorithm);
        string token = Mint(algorithm, key, AtHash(AccessToken, expectedDigest));

        Result<OidcIdentity> result = await Validate(algorithm, key, token, AccessToken);

        Assert.True(result.IsSuccess, $"{algorithm} should hash with {expectedDigest}");
    }

    [Theory]
    [InlineData("RS384", "SHA256")]
    [InlineData("RS512", "SHA256")]
    [InlineData("RS256", "SHA512")]
    [InlineData("ES384", "SHA256")]
    [InlineData("ES512", "SHA384")]
    [InlineData("PS512", "SHA256")]
    public async Task Access_token_hash_from_the_wrong_digest_is_rejected(
        string algorithm,
        string wrongDigest)
    {
        SecurityKey key = KeyFor(algorithm);
        string token = Mint(algorithm, key, AtHash(AccessToken, wrongDigest));

        Result<OidcIdentity> result = await Validate(algorithm, key, token, AccessToken);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("RS256", "SHA256")]
    [InlineData("RS384", "SHA384")]
    [InlineData("ES512", "SHA512")]
    public async Task Access_token_hash_over_a_different_access_token_is_rejected(
        string algorithm,
        string digest)
    {
        SecurityKey key = KeyFor(algorithm);
        string token = Mint(algorithm, key, AtHash("a-different-access-token", digest));

        Result<OidcIdentity> result = await Validate(algorithm, key, token, AccessToken);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    /// <summary>
    /// The full-length digest is a common implementation error: the claim
    /// carries only the left-most half.
    /// </summary>
    [Theory]
    [InlineData("RS256", "SHA256")]
    [InlineData("RS512", "SHA512")]
    public async Task Access_token_hash_over_the_full_digest_is_rejected(
        string algorithm,
        string digest)
    {
        SecurityKey key = KeyFor(algorithm);
        byte[] full = CryptographicOperations.HashData(
            new HashAlgorithmName(digest),
            Encoding.ASCII.GetBytes(AccessToken));
        string token = Mint(algorithm, key, Base64UrlEncoder.Encode(full));

        Result<OidcIdentity> result = await Validate(algorithm, key, token, AccessToken);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("RS256")]
    [InlineData("ES384")]
    public async Task An_absent_access_token_hash_leaves_the_token_valid(string algorithm)
    {
        SecurityKey key = KeyFor(algorithm);
        string token = Mint(algorithm, key, atHash: null);

        Result<OidcIdentity> result = await Validate(algorithm, key, token, AccessToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Without an access token to hash there is nothing to bind, and the claim
    /// alone must not become a reason to fail a sound token.
    /// </summary>
    [Fact]
    public async Task An_access_token_hash_without_an_access_token_is_not_checked()
    {
        SecurityKey key = KeyFor("RS256");
        string token = Mint("RS256", key, AtHash("some-other-token", "SHA256"));

        Result<OidcIdentity> result = await Validate("RS256", key, token, accessToken: null);

        Assert.True(result.IsSuccess);
    }

    private static string AtHash(string accessToken, string digest)
    {
        byte[] hash = CryptographicOperations.HashData(
            new HashAlgorithmName(digest),
            Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncoder.Encode(hash.AsSpan(0, hash.Length / 2).ToArray());
    }

    private SecurityKey KeyFor(string algorithm) =>
        algorithm.StartsWith("ES", StringComparison.Ordinal)
            ? new ECDsaSecurityKey(_curves[algorithm]) { KeyId = $"key-{algorithm}" }
            : new RsaSecurityKey(_rsa) { KeyId = $"key-{algorithm}" };

    private static OidcProviderOptions Options(string algorithm) =>
        OidcTestProvider.CreateOptions(allowedSigningAlgorithms: [algorithm]);

    private async Task<Result<OidcIdentity>> Validate(
        string algorithm,
        SecurityKey key,
        string token,
        string? accessToken)
    {
        OidcProviderOptions options = Options(algorithm);
        var validator = new OidcIdTokenValidator(options, _clock);
        var metadata = new OidcProviderMetadata(
            options.ExpectedIssuer,
            new Uri($"{options.ExpectedIssuer}/authorize"),
            new Uri($"{options.ExpectedIssuer}/token"),
            new Uri($"{options.ExpectedIssuer}/keys"),
            null,
            [key],
            _clock.UtcNow);
        return await validator.ValidateAsync(
            token,
            new OidcIdTokenValidationContext(metadata, Nonce, accessToken),
            CancellationToken.None);
    }

    private string Mint(string algorithm, SecurityKey key, string? atHash)
    {
        OidcProviderOptions options = Options(algorithm);
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = "AAAAAAAAAAAAAAAAAAAAAJ0Zx1a2bqQ",
            ["ver"] = "2.0",
            ["aud"] = OidcTestProvider.ClientId,
            ["tid"] = OidcTestProvider.TenantId.ToString("D"),
            ["oid"] = ObjectId,
            ["nonce"] = Nonce,
        };

        if (atHash is not null)
        {
            claims["at_hash"] = atHash;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.ExpectedIssuer,
            Claims = claims,
            NotBefore = _clock.UtcNow.UtcDateTime,
            IssuedAt = _clock.UtcNow.UtcDateTime,
            Expires = _clock.UtcNow.AddHours(1).UtcDateTime,
            TokenType = "JWT",
            SigningCredentials = new SigningCredentials(key, algorithm),
        });
    }
}
