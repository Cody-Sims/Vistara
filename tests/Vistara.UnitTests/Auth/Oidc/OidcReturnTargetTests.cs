using Vistara.Auth.Oidc;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

public sealed class OidcReturnTargetTests
{
    private static readonly Uri ApplicationBaseUri = new("https://vistara.example/");

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("/gallery", "/gallery")]
    [InlineData("/gallery/", "/gallery/")]
    [InlineData("/gallery?album=summer", "/gallery?album=summer")]
    [InlineData("/gallery?album=a%20b", "/gallery?album=a%20b")]
    [InlineData("https://vistara.example/gallery?album=summer", "/gallery?album=summer")]
    [InlineData("https://vistara.example", "/")]
    public void Return_target_accepts_same_origin_application_paths(
        string? candidate,
        string expected)
    {
        Assert.True(OidcReturnTarget.TryCreate(candidate, ApplicationBaseUri, out string returnTo));
        Assert.Equal(expected, returnTo);
    }

    [Theory]
    [InlineData("//attacker.example/steal")]
    [InlineData("///attacker.example")]
    [InlineData("/\\attacker.example")]
    [InlineData("/%2fattacker.example")]
    [InlineData("/%5cattacker.example")]
    [InlineData("/%2F%2Fattacker.example")]
    [InlineData("https://attacker.example/gallery")]
    [InlineData("http://vistara.example/gallery")]
    [InlineData("https://vistara.example:8443/gallery")]
    [InlineData("https://user:pass@vistara.example/gallery")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>")]
    [InlineData("gallery")]
    [InlineData("\\\\attacker.example\\share")]
    [InlineData("/gallery#fragment")]
    [InlineData("/gal\nlery")]
    [InlineData("/gal\tlery")]
    [InlineData("/gal lery")]
    [InlineData("/gallery\u0000")]
    [InlineData("/gallery\u2028")]
    [InlineData("/../etc/passwd")]
    [InlineData("/gallery/../../etc")]
    [InlineData("/%252f%252fattacker.example")]
    public void Return_target_rejects_cross_origin_and_malformed_candidates(string candidate)
    {
        Assert.False(OidcReturnTarget.TryCreate(candidate, ApplicationBaseUri, out string returnTo));
        Assert.Equal("/", returnTo);
    }

    [Fact]
    public void Return_target_rejects_candidates_longer_than_the_bound()
    {
        string candidate = string.Concat("/", new string('a', OidcReturnTarget.MaximumLength));

        Assert.False(OidcReturnTarget.TryCreate(candidate, ApplicationBaseUri, out string returnTo));
        Assert.Equal("/", returnTo);
    }

    [Fact]
    public void Return_target_requires_an_absolute_application_base_uri()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OidcReturnTarget.TryCreate("/gallery", null!, out _));
        Assert.Throws<ArgumentException>(() =>
            OidcReturnTarget.TryCreate("/gallery", new Uri("/base", UriKind.Relative), out _));
    }

    [Fact]
    public void Return_target_honours_a_non_root_application_base_path()
    {
        var baseUri = new Uri("https://vistara.example/app/");

        Assert.True(OidcReturnTarget.TryCreate(
            "https://vistara.example/app/gallery",
            baseUri,
            out string returnTo));
        Assert.Equal("/app/gallery", returnTo);
        Assert.False(OidcReturnTarget.TryCreate(
            "https://vistara.example/other/gallery",
            baseUri,
            out _));
    }
}
