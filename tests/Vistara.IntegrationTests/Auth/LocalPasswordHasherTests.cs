using Vistara.Auth.Cookies;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

public sealed class LocalPasswordHasherTests
{
    private static readonly Pbkdf2LocalPasswordHasher Hasher = new(100_000);

    [Fact]
    public void Hashing_is_salted_so_equal_passwords_produce_distinct_verifiers()
    {
        const string password = "correct-horse-battery";

        string first = Hasher.Hash(password);
        string second = Hasher.Hash(password);

        Assert.NotEqual(first, second);
        Assert.True(Hasher.Verify(password, first));
        Assert.True(Hasher.Verify(password, second));
        Assert.DoesNotContain(password, first, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_rejects_a_different_password()
    {
        string stored = Hasher.Hash("correct-horse-battery");

        Assert.False(Hasher.Verify("correct-horse-batterz", stored));
        Assert.False(Hasher.Verify(string.Empty, stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plaintext")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$100000$@@@$aGFzaA==")]
    [InlineData("argon2id$100000$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$100000$c2FsdA==$c2hvcnQ=")]
    public void Verification_fails_closed_for_malformed_verifiers(string stored)
    {
        Assert.False(Hasher.Verify("correct-horse-battery", stored));
    }

    [Fact]
    public void Verification_rejects_a_tampered_verifier()
    {
        const string password = "correct-horse-battery";
        string stored = Hasher.Hash(password);
        string[] parts = stored.Split('$');
        byte[] hash = Convert.FromBase64String(parts[3]);
        hash[0] ^= 0xff;
        string tampered = string.Join(
            '$',
            parts[0],
            parts[1],
            parts[2],
            Convert.ToBase64String(hash));

        Assert.False(Hasher.Verify(password, tampered));
    }

    [Fact]
    public void Hashing_refuses_passwords_below_the_minimum_length()
    {
        Assert.Equal(12, Hasher.MinimumPasswordLength);
        Assert.Throws<ArgumentException>(() => Hasher.Hash("short"));
    }
}
