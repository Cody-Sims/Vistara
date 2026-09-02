using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

public sealed class OidcLoginRequestFactoryTests
{
    [Fact]
    public void Login_handle_draws_state_nonce_and_verifier_from_disjoint_random_material()
    {
        var random = new SequentialOidcRandomSource();
        OidcLoginRequestFactory factory = CreateFactory(random);

        Result<OidcLoginHandle> result = factory.Create("/gallery");

        Assert.True(result.TryGetValue(out OidcLoginHandle? handle));
        Assert.Equal(96, random.BytesProduced);
        Assert.Equal(43, handle.State.Length);
        Assert.Equal(43, handle.Nonce.Length);
        Assert.Equal(43, handle.CodeVerifier.Length);
        Assert.Equal(3, new HashSet<string>(StringComparer.Ordinal)
        {
            handle.State,
            handle.Nonce,
            handle.CodeVerifier,
        }.Count);
        Assert.All(
            new[] { handle.State, handle.Nonce, handle.CodeVerifier },
            value => Assert.True(value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')));
    }

    [Fact]
    public void Login_handle_binds_a_sha256_pkce_challenge_and_records_its_method()
    {
        OidcLoginRequestFactory factory = CreateFactory(new SequentialOidcRandomSource());

        Result<OidcLoginHandle> result = factory.Create("/gallery");

        Assert.True(result.TryGetValue(out OidcLoginHandle? handle));
        Assert.Equal("S256", handle.CodeChallengeMethod);
        Assert.Equal(OidcPkce.CreateChallenge(handle.CodeVerifier), handle.CodeChallenge);
        Assert.NotEqual(handle.CodeVerifier, handle.CodeChallenge);
    }

    [Fact]
    public void Pkce_challenge_matches_the_rfc7636_reference_vector()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            OidcPkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("has spaces in the verifier value padded to a legal length aaaa")]
    public void Pkce_challenge_rejects_verifiers_outside_the_rfc7636_alphabet_and_length(
        string verifier)
    {
        Assert.Throws<ArgumentException>(() => OidcPkce.CreateChallenge(verifier));
    }

    [Fact]
    public void Login_handle_publishes_lookup_digests_that_never_reveal_the_secret()
    {
        OidcLoginRequestFactory factory = CreateFactory(new SequentialOidcRandomSource());

        Result<OidcLoginHandle> result = factory.Create("/gallery");

        Assert.True(result.TryGetValue(out OidcLoginHandle? handle));
        foreach ((string secret, string digest) in new[]
        {
            (handle.State, handle.StateDigest),
            (handle.Nonce, handle.NonceDigest),
        })
        {
            Assert.Equal(64, digest.Length);
            Assert.True(digest.All(character =>
                char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
            Assert.NotEqual(secret, digest);
            Assert.True(OidcHandleCryptography.FixedTimeMatches(secret, digest));
            Assert.False(OidcHandleCryptography.FixedTimeMatches(secret + "a", digest));
        }
    }

    [Fact]
    public void Login_handle_digest_lookup_rejects_values_that_are_not_handle_shaped()
    {
        OidcLoginRequestFactory factory = CreateFactory(new SequentialOidcRandomSource());
        Result<OidcLoginHandle> result = factory.Create("/gallery");
        Assert.True(result.TryGetValue(out OidcLoginHandle? handle));

        Assert.True(OidcHandleCryptography.TryComputeDigest(handle.State, out string digest));
        Assert.Equal(handle.StateDigest, digest);

        foreach (string? hostile in new[]
        {
            null,
            string.Empty,
            "  ",
            new string('a', 42),
            new string('a', 44),
            string.Concat(new string('a', 42), "+"),
            string.Concat(new string('a', 42), "="),
        })
        {
            Assert.False(OidcHandleCryptography.TryComputeDigest(hostile, out string rejected));
            Assert.Equal(string.Empty, rejected);
        }
    }

    [Fact]
    public void Login_handle_expires_on_the_clock_it_was_issued_from()
    {
        var clock = new FixedOidcClock(new DateTimeOffset(2032, 3, 4, 5, 6, 7, TimeSpan.Zero));
        OidcLoginRequestFactory factory = CreateFactory(
            new SequentialOidcRandomSource(),
            clock: clock);

        Result<OidcLoginHandle> result = factory.Create("/gallery");

        Assert.True(result.TryGetValue(out OidcLoginHandle? handle));
        Assert.Equal(clock.UtcNow, handle.CreatedAt);
        Assert.Equal(clock.UtcNow.Add(TimeSpan.FromMinutes(10)), handle.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, handle.CreatedAt.Offset);
    }

    [Fact]
    public void Login_handle_normalises_the_return_target_and_rejects_hostile_ones()
    {
        OidcLoginRequestFactory factory = CreateFactory(new SequentialOidcRandomSource());

        Result<OidcLoginHandle> accepted = factory.Create("https://vistara.example/gallery");
        Result<OidcLoginHandle> defaulted = factory.Create(null);
        Result<OidcLoginHandle> rejected = factory.Create("//attacker.example/");

        Assert.True(accepted.TryGetValue(out OidcLoginHandle? acceptedHandle));
        Assert.Equal("/gallery", acceptedHandle.ReturnTo);
        Assert.True(defaulted.TryGetValue(out OidcLoginHandle? defaultedHandle));
        Assert.Equal("/", defaultedHandle.ReturnTo);
        Assert.Equal(OidcErrors.InvalidReturnTarget.Code, rejected.Error?.Code);
    }

    [Fact]
    public void Login_handle_never_renders_its_secrets_in_string_form()
    {
        OidcLoginRequestFactory factory = CreateFactory(new SequentialOidcRandomSource());
        Result<OidcLoginHandle> result = factory.Create("/gallery");
        Assert.True(result.TryGetValue(out OidcLoginHandle? handle));

        string rendered = handle.ToString();

        Assert.Equal("[OidcLoginHandle REDACTED]", rendered);
        Assert.DoesNotContain(handle.State, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(handle.Nonce, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(handle.CodeVerifier, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Login_handle_factory_requires_its_collaborators()
    {
        var options = OidcTestProvider.CreateOptions();

        Assert.Throws<ArgumentNullException>(() =>
            new OidcLoginRequestFactory(null!, new SequentialOidcRandomSource(), new FixedOidcClock()));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcLoginRequestFactory(options, null!, new FixedOidcClock()));
        Assert.Throws<ArgumentNullException>(() =>
            new OidcLoginRequestFactory(options, new SequentialOidcRandomSource(), null!));
    }

    private static OidcLoginRequestFactory CreateFactory(
        SequentialOidcRandomSource random,
        FixedOidcClock? clock = null) =>
        new(OidcTestProvider.CreateOptions(), random, clock ?? new FixedOidcClock());
}
