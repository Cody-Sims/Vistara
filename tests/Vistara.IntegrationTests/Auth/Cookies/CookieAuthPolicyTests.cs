using Vistara.Auth.Cookies;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Cookies;

public sealed class CookieAuthPolicyTests
{
    [Fact]
    public void CookieAuth_cookie_descriptor_is_host_only_secure_and_bounded()
    {
        var options = new CookieAuthOptions(
            idleLifetime: TimeSpan.FromMinutes(30),
            absoluteLifetime: TimeSpan.FromHours(8),
            slidingRefreshInterval: TimeSpan.FromMinutes(10));
        BrowserCookie cookie = BrowserCookie.Session(
            options,
            "opaque-value",
            TimeSpan.FromMinutes(30));

        Assert.Equal("__Host-vistara-session", cookie.Name);
        Assert.Equal("/", cookie.Path);
        Assert.Null(cookie.Domain);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(BrowserSameSite.Lax, cookie.SameSite);
        Assert.Equal(TimeSpan.FromMinutes(30), cookie.MaxAge);
        Assert.Equal(
            "__Host-vistara-session=opaque-value; Path=/; Max-Age=1800; Secure; HttpOnly; SameSite=Lax",
            cookie.ToSetCookieHeader());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public void CookieAuth_antiforgery_allows_safe_cookie_methods(string method)
    {
        var policy = new CookieAntiforgeryPolicy();

        AntiforgeryDecision decision = policy.Validate(
            method,
            BrowserAuthenticationKind.Cookie,
            null,
            null);

        Assert.True(decision.IsAllowed);
    }

    [Theory]
    [InlineData(BrowserAuthenticationKind.Bearer)]
    [InlineData(BrowserAuthenticationKind.ApiKey)]
    [InlineData(BrowserAuthenticationKind.None)]
    public void CookieAuth_antiforgery_does_not_apply_to_non_cookie_authentication(
        BrowserAuthenticationKind authenticationKind)
    {
        var policy = new CookieAntiforgeryPolicy();

        AntiforgeryDecision decision = policy.Validate(
            "POST",
            authenticationKind,
            null,
            null);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void CookieAuth_antiforgery_requires_matching_header_for_unsafe_cookie_request()
    {
        var policy = new CookieAntiforgeryPolicy();
        string digest = CookieTokenCryptography.ComputeDigest("csrf-token");

        AntiforgeryDecision missing = policy.Validate(
            "POST",
            BrowserAuthenticationKind.Cookie,
            null,
            digest);
        AntiforgeryDecision invalid = policy.Validate(
            "DELETE",
            BrowserAuthenticationKind.Cookie,
            "wrong-token",
            digest);
        AntiforgeryDecision valid = policy.Validate(
            "PATCH",
            BrowserAuthenticationKind.Cookie,
            "csrf-token",
            digest);

        Assert.False(missing.IsAllowed);
        Assert.False(invalid.IsAllowed);
        Assert.True(valid.IsAllowed);
        Assert.Equal("cookie_auth.antiforgery_required", missing.Error?.Code);
        Assert.DoesNotContain("csrf-token", invalid.Error?.Message ?? string.Empty);
    }

    [Fact]
    public void CookieAuth_antiforgery_validation_is_origin_independent()
    {
        var policy = new CookieAntiforgeryPolicy();
        string digest = CookieTokenCryptography.ComputeDigest("csrf-token");

        AntiforgeryDecision decision = policy.Validate(
            "POST",
            BrowserAuthenticationKind.Cookie,
            "csrf-token",
            digest);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void CookieAuth_antiforgery_reads_only_the_configured_header()
    {
        var options = new CookieAuthOptions(
            antiforgeryHeaderName: "X-Custom-CSRF");
        var policy = new CookieAntiforgeryPolicy();
        string digest = CookieTokenCryptography.ComputeDigest("csrf-token");

        AntiforgeryDecision wrongHeader = policy.Validate(
            "POST",
            BrowserAuthenticationKind.Cookie,
            new Dictionary<string, string?>
            {
                ["X-Vistara-CSRF"] = "csrf-token",
            },
            digest,
            options);
        AntiforgeryDecision configuredHeader = policy.Validate(
            "POST",
            BrowserAuthenticationKind.Cookie,
            new Dictionary<string, string?>
            {
                ["x-custom-csrf"] = "csrf-token",
            },
            digest,
            options);

        Assert.False(wrongHeader.IsAllowed);
        Assert.True(configuredHeader.IsAllowed);
    }

    [Fact]
    public void CookieAuth_options_reject_unbounded_or_non_sliding_lifetimes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CookieAuthOptions(
                TimeSpan.FromHours(2),
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CookieAuthOptions(
                TimeSpan.FromMinutes(30),
                CookieAuthOptions.MaximumAbsoluteLifetime + TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CookieAuthOptions(
                TimeSpan.FromMinutes(30),
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(31)));
    }
}
