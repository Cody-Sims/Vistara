using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

public sealed class OidcIdTokenValidatorTests
{
    private const string Nonce = "sTZ5wUdpQZ2ULrDkYWK-Y0M4nJ3o1cAqQ8W6VpQoLmE";
    private const string ObjectId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Subject = "AAAAAAAAAAAAAAAAAAAAAJ0Zx1a2bqQ";

    [Fact]
    public async Task Id_token_validation_returns_the_directory_identity_for_a_sound_token()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);
        OidcIdTokenValidator validator = CreateValidator(provider);

        Result<OidcIdentity> result = await validator.ValidateAsync(
            mint.Create(),
            Context(provider),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out OidcIdentity? identity));
        Assert.Equal(OidcTestProvider.TenantId, identity.DirectoryTenantId);
        Assert.Equal(ObjectId, identity.ObjectId);
        Assert.Equal(Subject, identity.Subject);
        Assert.Equal("member@vistara.example", identity.Email);
        Assert.Equal("Vistara Member", identity.DisplayName);
        Assert.Equal(provider.Options.ExpectedIssuer, identity.Issuer);
    }

    [Fact]
    public async Task Id_token_validation_never_treats_an_email_as_the_identity_key()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);
        OidcIdTokenValidator validator = CreateValidator(provider);

        Result<OidcIdentity> withoutObjectId = await validator.ValidateAsync(
            mint.Create(objectId: null),
            Context(provider),
            CancellationToken.None);
        Result<OidcIdentity> withoutEmail = await validator.ValidateAsync(
            mint.Create(email: null, name: null),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, withoutObjectId.Error?.Code);
        Assert.True(withoutEmail.TryGetValue(out OidcIdentity? identity));
        Assert.Null(identity.Email);
        Assert.Null(identity.DisplayName);
        Assert.Equal(ObjectId, identity.ObjectId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Id_token_validation_requires_a_real_directory_object_identifier(
        string objectId)
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(objectId: objectId),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_token_from_another_directory_tenant()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(tenantId: "99999999-9999-9999-9999-999999999999"),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TenantNotAllowed.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_personal_microsoft_account_tenant()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(tenantId: "9188040d-6c67-4c5b-b112-36a304b66dad"),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TenantNotAllowed.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Id_token_validation_requires_a_tenant_claim(string? tenantId)
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(tenantId: tenantId),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_an_unsigned_token()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.CreateUnsigned(),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_symmetric_signature_over_the_public_key()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.CreateHmacSigned(),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_token_signed_by_an_unpublished_key()
    {
        using var provider = new OidcProviderFixture();
        using RSA attacker = RSA.Create(2048);
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(signingKey: new RsaSecurityKey(attacker)
            {
                KeyId = OidcProviderFixture.SigningKeyId,
            }),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_token_whose_key_identifier_is_unknown()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(keyId: "rotated-away"),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_tampered_payload()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);
        string token = mint.Create();
        string[] parts = token.Split('.');
        string tamperedPayload = Base64UrlEncoder.Encode(
            Base64UrlEncoder.Decode(parts[1]).Replace(ObjectId, "00000000-0000-0000-0000-00000000dead", StringComparison.Ordinal));

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            string.Join('.', parts[0], tamperedPayload, parts[2]),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_an_issuer_that_is_not_the_configured_authority()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        foreach (string issuer in new[]
        {
            "https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0",
            "https://attacker.example/v2.0",
            $"{provider.Options.ExpectedIssuer}/",
            provider.Options.ExpectedIssuer.ToUpperInvariant(),
        })
        {
            Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
                mint.Create(issuer: issuer),
                Context(provider),
                CancellationToken.None);

            Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
        }
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("")]
    public async Task Id_token_validation_rejects_an_audience_that_is_not_the_client(
        string audience)
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(audience: audience),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_token_that_lists_extra_audiences()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.CreateMultiAudience(OidcTestProvider.ClientId, "another-client"),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    /// <summary>
    /// Guards the hostile corpus itself: the mint re-encodes and re-signs a
    /// payload for several negative cases, so a benign re-signed token must
    /// still validate. Without this, every re-signed rejection could be passing
    /// because the mint produced garbage rather than because a rule fired.
    /// </summary>
    [Fact]
    public async Task Id_token_hostile_corpus_resigning_still_produces_a_valid_token()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.CreateMultiAudience(OidcTestProvider.ClientId),
            Context(provider),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Id_token_validation_rejects_an_expired_token_outside_the_configured_skew()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);
        OidcIdTokenValidator validator = CreateValidator(provider);

        string token = mint.Create();
        provider.Clock.Advance(TimeSpan.FromMinutes(70));
        Result<OidcIdentity> result = await validator.ValidateAsync(
            token,
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_token_that_is_not_yet_valid()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(notBeforeOffset: TimeSpan.FromMinutes(10)),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_token_without_an_expiry()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.CreateWithoutExpiry(),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_an_issue_time_far_in_the_future()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(issuedAtOffset: TimeSpan.FromMinutes(20)),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-nonce-value-that-is-long-enough-to-pass")]
    public async Task Id_token_validation_rejects_a_replayed_or_missing_nonce(string? nonce)
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(nonce: nonce),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_requires_an_expected_nonce_from_the_login_store()
    {
        using var provider = new OidcProviderFixture();

        Assert.Throws<ArgumentException>(() =>
            new OidcIdTokenValidationContext(provider.CreateMetadata(), "  "));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcIdTokenValidationContext(null!, Nonce));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_version_one_token()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(version: "1.0"),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not.a.jwt.at.all")]
    [InlineData("only-one-part")]
    [InlineData("two.parts")]
    [InlineData("...")]
    [InlineData("a..c")]
    [InlineData("!!!.###.$$$")]
    public async Task Id_token_validation_rejects_values_that_are_not_compact_jws(string? token)
    {
        using var provider = new OidcProviderFixture();

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            token,
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_token_beyond_the_length_bound()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(padding: new string('p', OidcIdTokenValidator.MaximumTokenLength)),
            Context(provider),
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_rejects_duplicate_security_critical_claims()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        foreach (string token in new[]
        {
            mint.CreateWithDuplicatePayloadMember("tid", OidcTestProvider.TenantId.ToString("D")),
            mint.CreateWithDuplicatePayloadMember("oid", ObjectId),
            mint.CreateWithDuplicatePayloadMember("nonce", Nonce),
            mint.CreateWithDuplicatePayloadMember("aud", OidcTestProvider.ClientId),
        })
        {
            Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
                token,
                Context(provider),
                CancellationToken.None);

            Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
        }
    }

    [Fact]
    public async Task Id_token_validation_rejects_a_declared_algorithm_outside_the_allowlist()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        foreach (string token in new[]
        {
            mint.CreateWithHeaderAlgorithm("none"),
            mint.CreateWithHeaderAlgorithm("HS256"),
            mint.CreateWithHeaderAlgorithm("RS512"),
            mint.CreateWithHeaderAlgorithm("rs256"),
        })
        {
            Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
                token,
                Context(provider),
                CancellationToken.None);

            Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
        }
    }

    [Fact]
    public async Task Id_token_validation_rejects_an_encrypted_or_mistyped_token()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        foreach (string token in new[]
        {
            mint.Create(tokenType: "at+jwt"),
            mint.Create(tokenType: "JWE"),
        })
        {
            Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
                token,
                Context(provider),
                CancellationToken.None);

            Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
        }
    }

    [Fact]
    public async Task Id_token_validation_binds_the_access_token_hash_when_one_is_present()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);
        const string accessToken = "access-token-value";

        Result<OidcIdentity> matching = await CreateValidator(provider).ValidateAsync(
            mint.Create(accessTokenHashFor: accessToken),
            Context(provider, accessToken),
            CancellationToken.None);
        Result<OidcIdentity> mismatched = await CreateValidator(provider).ValidateAsync(
            mint.Create(accessTokenHashFor: "different-access-token"),
            Context(provider, accessToken),
            CancellationToken.None);

        Assert.True(matching.IsSuccess);
        Assert.Equal(OidcErrors.InvalidIdToken.Code, mismatched.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_failures_never_carry_token_material()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(nonce: "attacker-controlled-nonce-value-long-enough"),
            Context(provider),
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(OidcErrors.InvalidIdToken.Message, result.Error.Message);
        Assert.DoesNotContain("attacker", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Id_token_validation_reports_unavailable_when_the_key_set_is_empty()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);
        var context = new OidcIdTokenValidationContext(
            provider.CreateMetadata(signingKeys: []),
            Nonce);

        Result<OidcIdentity> result = await CreateValidator(provider).ValidateAsync(
            mint.Create(),
            context,
            CancellationToken.None);

        Assert.Equal(OidcErrors.InvalidIdToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Id_token_validation_propagates_caller_cancellation()
    {
        using var provider = new OidcProviderFixture();
        var mint = new OidcIdTokenMint(provider);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CreateValidator(provider).ValidateAsync(
                mint.Create(),
                Context(provider),
                cancellation.Token));
    }

    [Fact]
    public void Id_token_identity_never_renders_profile_attributes_in_string_form()
    {
        var identity = new OidcIdentity(
            OidcTestProvider.TenantId,
            ObjectId,
            Subject,
            "member@vistara.example",
            "Vistara Member",
            "https://login.microsoftonline.com/tenant/v2.0");

        string rendered = identity.ToString();

        Assert.DoesNotContain("member@vistara.example", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Vistara Member", rendered, StringComparison.Ordinal);
        Assert.Contains(ObjectId, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Id_token_validator_requires_its_collaborators()
    {
        using var provider = new OidcProviderFixture();

        Assert.Throws<ArgumentNullException>(() =>
            new OidcIdTokenValidator(null!, provider.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcIdTokenValidator(provider.Options, null!));
    }

    private static OidcIdTokenValidationContext Context(
        OidcProviderFixture provider,
        string? accessToken = null) =>
        new(provider.CreateMetadata(), Nonce, accessToken);

    private static OidcIdTokenValidator CreateValidator(OidcProviderFixture provider) =>
        new(provider.Options, provider.Clock);

    /// <summary>
    /// Mints identity tokens, including the malformed and hostile shapes a
    /// compliant provider would never emit.
    /// </summary>
    private sealed class OidcIdTokenMint
    {
        private const string DefaultTenantSentinel = "<configured-tenant>";
        private readonly OidcProviderFixture _provider;
        private readonly JsonWebTokenHandler _handler = new();

        internal OidcIdTokenMint(OidcProviderFixture provider) => _provider = provider;

        internal string Create(
            string? issuer = null,
            string? audience = OidcTestProvider.ClientId,
            string? tenantId = DefaultTenantSentinel,
            string? objectId = ObjectId,
            string? nonce = Nonce,
            string? email = "member@vistara.example",
            string? name = "Vistara Member",
            string version = "2.0",
            string tokenType = "JWT",
            string? keyId = null,
            SecurityKey? signingKey = null,
            TimeSpan? notBeforeOffset = null,
            TimeSpan? issuedAtOffset = null,
            string? padding = null,
            string? accessTokenHashFor = null)
        {
            DateTimeOffset now = _provider.Clock.UtcNow;
            var claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = Subject,
                ["ver"] = version,
            };

            if (audience is not null)
            {
                claims["aud"] = audience;
            }

            string resolvedTenant = tenantId == DefaultTenantSentinel
                ? OidcTestProvider.TenantId.ToString("D")
                : tenantId!;
            if (tenantId is not null)
            {
                claims["tid"] = resolvedTenant;
            }

            if (objectId is not null)
            {
                claims["oid"] = objectId;
            }

            if (nonce is not null)
            {
                claims["nonce"] = nonce;
            }

            if (email is not null)
            {
                claims["preferred_username"] = email;
            }

            if (name is not null)
            {
                claims["name"] = name;
            }

            if (padding is not null)
            {
                claims["padding"] = padding;
            }

            if (accessTokenHashFor is not null)
            {
                claims["at_hash"] = ComputeAccessTokenHash(accessTokenHashFor);
            }

            SecurityKey key = signingKey ?? _provider.SigningKey;
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer ?? _provider.Options.ExpectedIssuer,
                Claims = claims,
                NotBefore = (now + (notBeforeOffset ?? TimeSpan.Zero)).UtcDateTime,
                IssuedAt = (now + (issuedAtOffset ?? TimeSpan.Zero)).UtcDateTime,
                Expires = now.AddHours(1).UtcDateTime,
                TokenType = tokenType,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            };
            string token = _handler.CreateToken(descriptor);
            return keyId is null ? token : ReplaceHeaderMember(token, "kid", keyId);
        }

        internal string CreateMultiAudience(params string[] audiences)
        {
            string token = Create();
            string[] parts = token.Split('.');
            string payload = Base64UrlEncoder.Decode(parts[1]).Replace(
                $"\"aud\":\"{OidcTestProvider.ClientId}\"",
                $"\"aud\":[{string.Join(',', audiences.Select(audience => $"\"{audience}\""))}]",
                StringComparison.Ordinal);
            return Resign(parts[0], payload);
        }

        internal string CreateWithoutExpiry()
        {
            string token = Create();
            string[] parts = token.Split('.');
            string payload = RemovePayloadMember(Base64UrlEncoder.Decode(parts[1]), "exp");
            return Resign(parts[0], payload);
        }

        internal string CreateWithDuplicatePayloadMember(string name, string value)
        {
            string token = Create();
            string[] parts = token.Split('.');
            string payload = Base64UrlEncoder.Decode(parts[1]);
            payload = string.Concat("{", $"\"{name}\":\"{value}\",", payload.AsSpan(1));
            return Resign(parts[0], payload);
        }

        internal string CreateWithHeaderAlgorithm(string algorithm)
        {
            string token = Create();
            string[] parts = token.Split('.');
            string header = Base64UrlEncoder.Decode(parts[0]);
            header = header.Replace("\"RS256\"", $"\"{algorithm}\"", StringComparison.Ordinal);
            return string.Join(
                '.',
                Base64UrlEncoder.Encode(header),
                parts[1],
                parts[2]);
        }

        internal string CreateUnsigned()
        {
            string token = Create();
            string[] parts = token.Split('.');
            string header = Base64UrlEncoder.Decode(parts[0])
                .Replace("\"RS256\"", "\"none\"", StringComparison.Ordinal);
            return string.Concat(Base64UrlEncoder.Encode(header), ".", parts[1], ".");
        }

        internal string CreateHmacSigned()
        {
            string token = Create();
            string[] parts = token.Split('.');
            string header = Base64UrlEncoder.Decode(parts[0])
                .Replace("\"RS256\"", "\"HS256\"", StringComparison.Ordinal);
            string encodedHeader = Base64UrlEncoder.Encode(header);
            byte[] secret = _provider.SigningKey.Rsa is null
                ? Encoding.UTF8.GetBytes(OidcProviderFixture.SigningKeyId)
                : _provider.SigningKey.Rsa
                    .ExportParameters(includePrivateParameters: false).Modulus!;
            byte[] signature = HMACSHA256.HashData(
                secret,
                Encoding.ASCII.GetBytes($"{encodedHeader}.{parts[1]}"));
            return string.Join(
                '.',
                encodedHeader,
                parts[1],
                Base64UrlEncoder.Encode(signature));
        }

        private static string ComputeAccessTokenHash(string accessToken)
        {
            byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
            return Base64UrlEncoder.Encode(digest.AsSpan(0, digest.Length / 2).ToArray());
        }

        private static string ReplaceHeaderMember(string token, string name, string value)
        {
            string[] parts = token.Split('.');
            string header = Base64UrlEncoder.Decode(parts[0]);
            int start = header.IndexOf($"\"{name}\":\"", StringComparison.Ordinal);
            int valueStart = start + name.Length + 4;
            int valueEnd = header.IndexOf('"', valueStart);
            header = string.Concat(header.AsSpan(0, valueStart), value, header.AsSpan(valueEnd));
            return string.Join('.', Base64UrlEncoder.Encode(header), parts[1], parts[2]);
        }

        private static string RemovePayloadMember(string payload, string name)
        {
            int start = payload.IndexOf($"\"{name}\":", StringComparison.Ordinal);
            int end = payload.IndexOf(',', start);
            return end < 0
                ? string.Concat(payload.AsSpan(0, start - 1), "}")
                : string.Concat(payload.AsSpan(0, start), payload.AsSpan(end + 1));
        }

        private string Resign(string encodedHeader, string payload)
        {
            string encodedPayload = Base64UrlEncoder.Encode(payload);
            byte[] signature = _provider.SigningKey.Rsa!.SignData(
                Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}"),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return string.Join(
                '.',
                encodedHeader,
                encodedPayload,
                Base64UrlEncoder.Encode(signature));
        }
    }
}
