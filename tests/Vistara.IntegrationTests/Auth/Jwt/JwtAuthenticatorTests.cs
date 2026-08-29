using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vistara.Application.Common;
using Vistara.Auth.Jwt;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Jwt;

public sealed class JwtAuthenticatorTests
{
    private const string Issuer = "https://issuer.example";
    private const string TrailingSlashIssuer = "https://issuer.example/";
    private const string Audience = "vistara-api";
    private static readonly DateTimeOffset Now =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly TenantId TenantId =
        new(Guid.CreateVersion7(Now));

    private static readonly UserId UserId =
        new(Guid.CreateVersion7(Now.AddMilliseconds(1)));

    [Fact]
    public async Task Jwt_valid_configured_rsa_issuer_authenticates_active_membership()
    {
        using RSA rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        JwtIssuerProfile profile = CreateProfile(signingKey);
        var membership = new FakeMembershipProvider();
        var revocation = new FakeRevocationStore();
        var metadata = new FakeMetadataKeyResolver();
        JwtAuthenticator authenticator = CreateAuthenticator(
            profile,
            membership,
            revocation,
            metadata);

        Result<JwtPrincipal> result = await authenticator.AuthenticateAsync(
            CreateToken(signingKey),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out JwtPrincipal? principal));
        Assert.Equal(UserId, principal.UserId);
        Assert.Equal(TenantId, principal.TenantId);
        Assert.Equal(TenantRole.Member, principal.Role);
        Assert.Equal(Issuer, principal.Issuer);
        Assert.Equal("subject-1", principal.Subject);
        Assert.Equal("token-1", principal.JwtId);
        Assert.Equal(1, membership.Calls);
        Assert.Equal(1, revocation.Calls);
        Assert.Empty(metadata.Addresses);
    }

    [Fact]
    public async Task Jwt_preserves_and_accepts_the_exact_trailing_slash_issuer_only()
    {
        using RSA rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        JwtIssuerProfile profile = CreateProfile(
            signingKey,
            issuer: TrailingSlashIssuer);
        var membership = new FakeMembershipProvider
        {
            ExpectedIssuer = TrailingSlashIssuer,
        };
        var revocation = new FakeRevocationStore
        {
            ExpectedIssuer = TrailingSlashIssuer,
        };
        JwtAuthenticator authenticator = CreateAuthenticator(
            profile,
            membership,
            revocation);

        Result<JwtPrincipal> exact = await authenticator.AuthenticateAsync(
            CreateToken(signingKey, issuer: TrailingSlashIssuer),
            CancellationToken.None);
        Result<JwtPrincipal> variant = await authenticator.AuthenticateAsync(
            CreateToken(signingKey, issuer: Issuer),
            CancellationToken.None);

        Assert.True(exact.TryGetValue(out JwtPrincipal? principal));
        Assert.Equal(TrailingSlashIssuer, profile.Issuer);
        Assert.Equal(TrailingSlashIssuer, principal.Issuer);
        Assert.Equal(JwtErrors.InvalidToken.Code, variant.Error?.Code);
        Assert.Equal(1, membership.Calls);
        Assert.Equal(1, revocation.Calls);
    }

    [Fact]
    public async Task Jwt_valid_configured_ecdsa_issuer_authenticates()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new ECDsaSecurityKey(ecdsa) { KeyId = "ec-1" };
        JwtIssuerProfile profile = CreateProfile(key, SecurityAlgorithms.EcdsaSha256);

        Result<JwtPrincipal> result = await CreateAuthenticator(profile)
            .AuthenticateAsync(
                CreateToken(key, SecurityAlgorithms.EcdsaSha256),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Jwt_metadata_profile_resolves_only_its_fixed_https_address()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "metadata-key" };
        var metadataAddress = new Uri(
            "https://issuer.example/.well-known/openid-configuration");
        JwtIssuerProfile profile = JwtIssuerProfile.ForMetadata(
            "metadata-profile",
            Issuer,
            Audience,
            metadataAddress,
            [SecurityAlgorithms.RsaSha256]);
        var resolver = new FakeMetadataKeyResolver { Keys = [key] };

        Result<JwtPrincipal> result = await CreateAuthenticator(
                profile,
                metadata: resolver)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([metadataAddress], resolver.Addresses);
    }

    [Fact]
    public async Task Jwt_unknown_issuer_is_rejected_without_metadata_or_domain_calls()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        JwtIssuerProfile profile = JwtIssuerProfile.ForMetadata(
            "trusted",
            Issuer,
            Audience,
            new Uri("https://issuer.example/fixed-metadata"),
            [SecurityAlgorithms.RsaSha256]);
        var resolver = new FakeMetadataKeyResolver { Keys = [key] };
        var membership = new FakeMembershipProvider();
        var revocation = new FakeRevocationStore();

        Result<JwtPrincipal> result = await CreateAuthenticator(
                profile,
                membership,
                revocation,
                resolver)
            .AuthenticateAsync(
                CreateToken(key, issuer: "https://attacker.example/token-selected-url"),
                CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, result.Error?.Code);
        Assert.Empty(resolver.Addresses);
        Assert.Equal(0, membership.Calls);
        Assert.Equal(0, revocation.Calls);
    }

    [Theory]
    [InlineData("none")]
    [InlineData(SecurityAlgorithms.HmacSha256)]
    [InlineData(SecurityAlgorithms.RsaSha384)]
    public async Task Jwt_rejects_none_symmetric_confusion_and_unpinned_algorithms(
        string algorithm)
    {
        using RSA rsa = RSA.Create(2048);
        var validationKey = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        JwtIssuerProfile profile = CreateProfile(validationKey);
        string token;
        if (algorithm == "none")
        {
            token = new JsonWebTokenHandler().CreateToken(
                CreatePayload(),
                new Dictionary<string, object> { ["typ"] = "at+jwt" });
        }
        else if (algorithm == SecurityAlgorithms.HmacSha256)
        {
            var confusionKey = new SymmetricSecurityKey(rsa.ExportSubjectPublicKeyInfo())
            {
                KeyId = validationKey.KeyId,
            };
            token = CreateToken(confusionKey, algorithm);
        }
        else
        {
            token = CreateToken(validationKey, algorithm);
        }

        Result<JwtPrincipal> result = await CreateAuthenticator(profile)
            .AuthenticateAsync(token, CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Jwt_rejects_unknown_key_and_invalid_signature()
    {
        using RSA trustedRsa = RSA.Create(2048);
        using RSA attackerRsa = RSA.Create(2048);
        var trustedKey = new RsaSecurityKey(trustedRsa) { KeyId = "trusted-key" };
        var unknownKey = new RsaSecurityKey(attackerRsa) { KeyId = "unknown-key" };
        var collidingKey = new RsaSecurityKey(attackerRsa) { KeyId = "trusted-key" };
        JwtAuthenticator authenticator = CreateAuthenticator(CreateProfile(trustedKey));

        Result<JwtPrincipal> unknown = await authenticator.AuthenticateAsync(
            CreateToken(unknownKey),
            CancellationToken.None);
        Result<JwtPrincipal> invalidSignature = await authenticator.AuthenticateAsync(
            CreateToken(collidingKey),
            CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, unknown.Error?.Code);
        Assert.Equal(JwtErrors.InvalidToken.Code, invalidSignature.Error?.Code);
    }

    [Theory]
    [InlineData("wrong-audience", "at+jwt")]
    [InlineData(Audience, "JWT")]
    [InlineData(Audience, "application/jwt")]
    public async Task Jwt_rejects_invalid_audience_or_type(
        string audience,
        string tokenType)
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };

        Result<JwtPrincipal> result = await CreateAuthenticator(CreateProfile(key))
            .AuthenticateAsync(
                CreateToken(key, audience: audience, tokenType: tokenType),
                CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("https://api.example/resource", "https://api.example/resource/")]
    [InlineData("https://api.example/resource/", "https://api.example/resource")]
    [InlineData(Audience, "VISTARA-API")]
    public async Task Jwt_rejects_non_ordinal_exact_audience_variants(
        string configuredAudience,
        string presentedAudience)
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        JwtIssuerProfile profile = CreateProfile(
            key,
            audience: configuredAudience);

        Result<JwtPrincipal> result = await CreateAuthenticator(profile)
            .AuthenticateAsync(
                CreateToken(key, audience: presentedAudience),
                CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Jwt_requires_type_but_accepts_an_explicitly_configured_type()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        JwtIssuerProfile profile = JwtIssuerProfile.ForSigningKeys(
            "trusted",
            Issuer,
            Audience,
            [key],
            [SecurityAlgorithms.RsaSha256],
            ["custom-access+jwt"]);

        Result<JwtPrincipal> configured = await CreateAuthenticator(profile)
            .AuthenticateAsync(
                CreateToken(key, tokenType: "custom-access+jwt"),
                CancellationToken.None);
        Result<JwtPrincipal> missing = await CreateAuthenticator(profile)
            .AuthenticateAsync(
                CreateToken(key, tokenType: null),
                CancellationToken.None);

        Assert.True(configured.IsSuccess);
        Assert.Equal(JwtErrors.InvalidToken.Code, missing.Error?.Code);
    }

    [Theory]
    [InlineData(-121, null, false)]
    [InlineData(-120, null, true)]
    [InlineData(null, 120, true)]
    [InlineData(null, 121, false)]
    public async Task Jwt_enforces_expiry_and_not_before_with_bounded_clock_skew(
        int? expiresOffsetSeconds,
        int? notBeforeOffsetSeconds,
        bool expectedSuccess)
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        DateTimeOffset expires = expiresOffsetSeconds.HasValue
            ? Now.AddSeconds(expiresOffsetSeconds.Value)
            : Now.AddMinutes(5);
        DateTimeOffset notBefore = notBeforeOffsetSeconds.HasValue
            ? Now.AddSeconds(notBeforeOffsetSeconds.Value)
            : expiresOffsetSeconds.HasValue
                ? Now.AddMinutes(-10)
                : Now.AddMinutes(-1);

        Result<JwtPrincipal> result = await CreateAuthenticator(CreateProfile(key))
            .AuthenticateAsync(
                CreateToken(key, expires: expires, notBefore: notBefore),
                CancellationToken.None);

        Assert.Equal(expectedSuccess, result.IsSuccess);
    }

    [Theory]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","jti":"token-1","tenant_id":"00000000-0000-4000-8000-000000000001","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","jti":"token-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","jti":"token-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa"}""")]
    public async Task Jwt_rejects_invalid_tenant_or_missing_required_claims(string payload)
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };

        Result<JwtPrincipal> result = await CreateAuthenticator(CreateProfile(key))
            .AuthenticateAsync(
                CreateToken(key, payload: payload),
                CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, result.Error?.Code);
    }

    [Theory]
    [InlineData("""{"iss":"https://issuer.example","iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","jti":"token-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","sub":"subject-2","jti":"token-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","aud":"another","sub":"subject-1","jti":"token-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","jti":"token-1","jti":"token-2","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","jti":"token-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000}""")]
    [InlineData("""{"iss":"https://issuer.example","aud":"vistara-api","sub":"subject-1","jti":"token-1","tenant_id":"0b8e1ca2-306e-7bd3-9479-71439b417baa","exp":1962000000,"exp":1962000001}""")]
    public async Task Jwt_rejects_duplicate_security_critical_claims(string payload)
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };

        Result<JwtPrincipal> result = await CreateAuthenticator(CreateProfile(key))
            .AuthenticateAsync(
                CreateToken(key, payload: payload),
                CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Jwt_rejects_duplicate_header_members_and_ambiguous_audiences()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        string valid = CreateToken(key);
        int separator = valid.IndexOf('.');
        string duplicateHeader = Base64UrlEncoder.Encode(
            """{"alg":"RS256","alg":"RS256","kid":"rsa-1","typ":"at+jwt"}""") +
            valid[separator..];
        string multipleAudiencesPayload =
            $$"""{"iss":"{{Issuer}}","aud":["{{Audience}}","another"],"sub":"subject-1","jti":"token-1","tenant_id":"{{TenantId.Value}}","nbf":{{Now.AddMinutes(-1).ToUnixTimeSeconds()}},"exp":{{Now.AddMinutes(5).ToUnixTimeSeconds()}}}""";
        JwtAuthenticator authenticator = CreateAuthenticator(CreateProfile(key));

        Result<JwtPrincipal> duplicate = await authenticator.AuthenticateAsync(
            duplicateHeader,
            CancellationToken.None);
        Result<JwtPrincipal> ambiguousAudience = await authenticator.AuthenticateAsync(
            CreateToken(key, payload: multipleAudiencesPayload),
            CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, duplicate.Error?.Code);
        Assert.Equal(JwtErrors.InvalidToken.Code, ambiguousAudience.Error?.Code);
    }

    [Theory]
    [InlineData(TenantStatus.Suspended, MembershipStatus.Active)]
    [InlineData(TenantStatus.Deactivated, MembershipStatus.Active)]
    [InlineData(TenantStatus.Active, MembershipStatus.Invited)]
    [InlineData(TenantStatus.Active, MembershipStatus.Suspended)]
    [InlineData(TenantStatus.Active, MembershipStatus.Removed)]
    public async Task Jwt_rejects_inactive_tenant_or_membership(
        TenantStatus tenantStatus,
        MembershipStatus membershipStatus)
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        var membership = new FakeMembershipProvider
        {
            Result = new JwtTenantMembership(
                UserId,
                TenantId,
                tenantStatus,
                membershipStatus,
                TenantRole.Member),
        };

        Result<JwtPrincipal> result = await CreateAuthenticator(
                CreateProfile(key),
                membership)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);

        Assert.Equal(JwtErrors.TenantAccessDenied.Code, result.Error?.Code);
    }

    [Fact]
    public async Task Jwt_rejects_missing_membership_and_revoked_jti()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        var missingMembership = new FakeMembershipProvider { Result = null };
        var revokedStore = new FakeRevocationStore { Revoked = true };

        Result<JwtPrincipal> missing = await CreateAuthenticator(
                CreateProfile(key),
                missingMembership)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);
        Result<JwtPrincipal> revoked = await CreateAuthenticator(
                CreateProfile(key),
                revocation: revokedStore)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);

        Assert.Equal(JwtErrors.TenantAccessDenied.Code, missing.Error?.Code);
        Assert.Equal(JwtErrors.Revoked.Code, revoked.Error?.Code);
    }

    [Fact]
    public async Task Jwt_fails_closed_on_invalid_role_or_domain_port_failure()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        var invalidRole = new FakeMembershipProvider
        {
            Result = new JwtTenantMembership(
                UserId,
                TenantId,
                TenantStatus.Active,
                MembershipStatus.Active,
                (TenantRole)999),
        };
        var failingRevocation = new FakeRevocationStore
        {
            Exception = new InvalidOperationException("sensitive store detail"),
        };
        var failingMembership = new FakeMembershipProvider
        {
            Exception = new InvalidOperationException("sensitive directory detail"),
        };

        Result<JwtPrincipal> role = await CreateAuthenticator(
                CreateProfile(key),
                invalidRole)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);
        Result<JwtPrincipal> revocation = await CreateAuthenticator(
                CreateProfile(key),
                revocation: failingRevocation)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);
        Result<JwtPrincipal> membership = await CreateAuthenticator(
                CreateProfile(key),
                failingMembership)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);

        Assert.Equal(JwtErrors.TenantAccessDenied.Code, role.Error?.Code);
        Assert.Equal(JwtErrors.ValidationUnavailable.Code, revocation.Error?.Code);
        Assert.Equal(JwtErrors.ValidationUnavailable.Code, membership.Error?.Code);
        Assert.DoesNotContain(
            "sensitive",
            revocation.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sensitive",
            membership.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jwt_honors_cancellation_before_and_during_port_calls()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        var membership = new FakeMembershipProvider();
        var revocation = new FakeRevocationStore();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CreateAuthenticator(
                    CreateProfile(key),
                    membership,
                    revocation)
                .AuthenticateAsync(CreateToken(key), canceled.Token));
        Assert.Equal(0, membership.Calls);
        Assert.Equal(0, revocation.Calls);

        revocation.Cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CreateAuthenticator(
                    CreateProfile(key),
                    membership,
                    revocation)
                .AuthenticateAsync(CreateToken(key), CancellationToken.None));

        revocation.Cancel = false;
        membership.Cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CreateAuthenticator(
                    CreateProfile(key),
                    membership,
                    revocation)
                .AuthenticateAsync(CreateToken(key), CancellationToken.None));
    }

    [Fact]
    public async Task Jwt_bounds_token_and_header_before_key_or_domain_work()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        var metadata = new FakeMetadataKeyResolver();
        var membership = new FakeMembershipProvider();
        var revocation = new FakeRevocationStore();
        JwtAuthenticator authenticator = CreateAuthenticator(
            CreateProfile(key),
            membership,
            revocation,
            metadata);
        string oversizedToken = new('a', JwtAuthenticator.MaximumTokenLength + 1);
        string oversizedHeader =
            $"{new string('a', JwtAuthenticator.MaximumEncodedHeaderLength + 1)}.e30.signature";

        Result<JwtPrincipal> tokenResult = await authenticator.AuthenticateAsync(
            oversizedToken,
            CancellationToken.None);
        Result<JwtPrincipal> headerResult = await authenticator.AuthenticateAsync(
            oversizedHeader,
            CancellationToken.None);

        Assert.Equal(JwtErrors.InvalidToken.Code, tokenResult.Error?.Code);
        Assert.Equal(JwtErrors.InvalidToken.Code, headerResult.Error?.Code);
        Assert.Empty(metadata.Addresses);
        Assert.Equal(0, membership.Calls);
        Assert.Equal(0, revocation.Calls);
    }

    [Fact]
    public async Task Jwt_fails_closed_on_metadata_configuration_or_key_resolution_errors()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        JwtIssuerProfile profile = JwtIssuerProfile.ForMetadata(
            "metadata",
            Issuer,
            Audience,
            new Uri("https://issuer.example/fixed-metadata"),
            [SecurityAlgorithms.RsaSha256]);
        var throwing = new FakeMetadataKeyResolver
        {
            Exception = new InvalidOperationException("sensitive provider detail"),
        };
        var empty = new FakeMetadataKeyResolver { Keys = [] };

        Result<JwtPrincipal> unavailable = await CreateAuthenticator(
                profile,
                metadata: throwing)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);
        Result<JwtPrincipal> noKeys = await CreateAuthenticator(
                profile,
                metadata: empty)
            .AuthenticateAsync(CreateToken(key), CancellationToken.None);

        Assert.Equal(JwtErrors.ValidationUnavailable.Code, unavailable.Error?.Code);
        Assert.Equal(JwtErrors.ValidationUnavailable.Code, noKeys.Error?.Code);
        Assert.DoesNotContain(
            "sensitive provider detail",
            unavailable.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jwt_error_never_contains_the_presented_token()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        string token = CreateToken(key, audience: "wrong-audience");

        Result<JwtPrincipal> result = await CreateAuthenticator(CreateProfile(key))
            .AuthenticateAsync(token, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.DoesNotContain(token, result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-1", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantId.Value.ToString(), result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jwt_profiles_reject_unsafe_or_ambiguous_configuration()
    {
        using RSA rsa = RSA.Create(2048);
        using RSA weakRsa = RSA.Create(1024);
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rsaKey = new RsaSecurityKey(rsa) { KeyId = "rsa-1" };
        var weakRsaKey = new RsaSecurityKey(weakRsa) { KeyId = "weak-rsa" };
        var p256Key = new ECDsaSecurityKey(p256) { KeyId = "p256" };
        var symmetricKey = new SymmetricSecurityKey(new byte[32]) { KeyId = "symmetric-1" };

        Assert.Throws<ArgumentException>(() => JwtIssuerProfile.ForSigningKeys(
            "bad-algorithm",
            Issuer,
            Audience,
            [rsaKey],
            [SecurityAlgorithms.HmacSha256]));
        Assert.Throws<ArgumentException>(() => JwtIssuerProfile.ForSigningKeys(
            "bad-key",
            Issuer,
            Audience,
            [symmetricKey],
            [SecurityAlgorithms.RsaSha256]));
        Assert.Throws<ArgumentException>(() => JwtIssuerProfile.ForSigningKeys(
            "weak-rsa",
            Issuer,
            Audience,
            [weakRsaKey],
            [SecurityAlgorithms.RsaSha256]));
        Assert.Throws<ArgumentException>(() => JwtIssuerProfile.ForSigningKeys(
            "curve-mismatch",
            Issuer,
            Audience,
            [p256Key],
            [SecurityAlgorithms.EcdsaSha384]));
        Assert.Throws<ArgumentException>(() => JwtIssuerProfile.ForMetadata(
            "bad-metadata",
            Issuer,
            Audience,
            new Uri("http://issuer.example/metadata"),
            [SecurityAlgorithms.RsaSha256]));
        Assert.Throws<ArgumentException>(() => new JwtAuthenticator(
            [CreateProfile(rsaKey), CreateProfile(rsaKey)],
            new FakeMetadataKeyResolver(),
            new FakeMembershipProvider(),
            new FakeRevocationStore(),
            new FakeClock(Now)));
    }

    private static JwtIssuerProfile CreateProfile(
        SecurityKey signingKey,
        string algorithm = SecurityAlgorithms.RsaSha256,
        string issuer = Issuer,
        string audience = Audience) =>
        JwtIssuerProfile.ForSigningKeys(
            "trusted",
            issuer,
            audience,
            [signingKey],
            [algorithm]);

    private static JwtAuthenticator CreateAuthenticator(
        JwtIssuerProfile profile,
        FakeMembershipProvider? membership = null,
        FakeRevocationStore? revocation = null,
        FakeMetadataKeyResolver? metadata = null) =>
        new(
            [profile],
            metadata ?? new FakeMetadataKeyResolver(),
            membership ?? new FakeMembershipProvider(),
            revocation ?? new FakeRevocationStore(),
            new FakeClock(Now));

    private static string CreateToken(
        SecurityKey signingKey,
        string algorithm = SecurityAlgorithms.RsaSha256,
        string issuer = Issuer,
        string audience = Audience,
        string? tokenType = "at+jwt",
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null,
        string? payload = null)
    {
        var header = new Dictionary<string, object>();
        if (tokenType is not null)
        {
            header["typ"] = tokenType;
        }

        return new JsonWebTokenHandler().CreateToken(
            payload ?? CreatePayload(issuer, audience, expires, notBefore),
            new SigningCredentials(signingKey, algorithm),
            header);
    }

    private static string CreatePayload(
        string issuer = Issuer,
        string audience = Audience,
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null) =>
        $$"""{"iss":"{{issuer}}","aud":"{{audience}}","sub":"subject-1","jti":"token-1","tenant_id":"{{TenantId.Value}}","nbf":{{(notBefore ?? Now.AddMinutes(-1)).ToUnixTimeSeconds()}},"exp":{{(expires ?? Now.AddMinutes(5)).ToUnixTimeSeconds()}}}""";

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeMembershipProvider : IJwtTenantMembershipProvider
    {
        public JwtTenantMembership? Result { get; init; } =
            new(
                UserId,
                TenantId,
                TenantStatus.Active,
                MembershipStatus.Active,
                TenantRole.Member);

        public bool Cancel { get; set; }

        public Exception? Exception { get; init; }

        public int Calls { get; private set; }

        public string ExpectedIssuer { get; init; } = Issuer;

        public ValueTask<JwtTenantMembership?> FindAsync(
            string issuer,
            string subject,
            TenantId tenantId,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Cancel)
            {
                throw new OperationCanceledException();
            }

            if (Exception is not null)
            {
                throw Exception;
            }

            Assert.Equal(ExpectedIssuer, issuer);
            Assert.Equal("subject-1", subject);
            Assert.Equal(TenantId, tenantId);
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakeRevocationStore : IJwtRevocationStore
    {
        public bool Revoked { get; init; }

        public bool Cancel { get; set; }

        public Exception? Exception { get; init; }

        public int Calls { get; private set; }

        public string ExpectedIssuer { get; init; } = Issuer;

        public ValueTask<bool> IsRevokedAsync(
            string issuer,
            string jwtId,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Cancel)
            {
                throw new OperationCanceledException();
            }

            if (Exception is not null)
            {
                throw Exception;
            }

            Assert.Equal(ExpectedIssuer, issuer);
            Assert.Equal("token-1", jwtId);
            return ValueTask.FromResult(Revoked);
        }
    }

    private sealed class FakeMetadataKeyResolver : IJwtMetadataSigningKeyResolver
    {
        public IReadOnlyCollection<SecurityKey> Keys { get; init; } = [];

        public Exception? Exception { get; init; }

        public List<Uri> Addresses { get; } = [];

        public ValueTask<IReadOnlyCollection<SecurityKey>> ResolveAsync(
            Uri metadataAddress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Addresses.Add(metadataAddress);
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult(Keys);
        }
    }
}
