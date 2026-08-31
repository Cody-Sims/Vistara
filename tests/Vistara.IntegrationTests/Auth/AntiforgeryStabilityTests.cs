using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Reading the session must never invalidate an antiforgery token another tab
/// is already using. Every reader of one browser session receives the same
/// usable token.
/// </summary>
public sealed class AntiforgeryStabilityTests
{
    private const string Password = "correct-horse-battery";

    private static readonly CookieAntiforgeryPolicy Policy = new();

    [Fact]
    public async Task Repeated_session_reads_return_the_same_usable_token()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();
        (string sessionToken, string loginToken) = await SignInAsync(harness);

        string? first = await IssueAsync(harness, sessionToken);
        string? second = await IssueAsync(harness, sessionToken);

        Assert.Equal(loginToken, first);
        Assert.Equal(first, second);
        Assert.True(await IsUsableAsync(harness, sessionToken, first!));
    }

    [Fact]
    public async Task Concurrent_tabs_all_receive_the_same_usable_token()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();
        (string sessionToken, _) = await SignInAsync(harness);

        string?[] tokens = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ =>
                Task.Run(() => IssueAsync(harness, sessionToken).AsTask())));

        Assert.All(tokens, token => Assert.False(string.IsNullOrWhiteSpace(token)));
        Assert.Single(tokens.Distinct(StringComparer.Ordinal));
        foreach (string? token in tokens)
        {
            Assert.True(await IsUsableAsync(harness, sessionToken, token!));
        }
    }

    [Fact]
    public async Task A_token_handed_to_one_tab_still_works_after_another_tab_reads()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();
        (string sessionToken, _) = await SignInAsync(harness);
        string? firstTab = await IssueAsync(harness, sessionToken);

        _ = await IssueAsync(harness, sessionToken);
        _ = await IssueAsync(harness, sessionToken);

        Assert.True(await IsUsableAsync(harness, sessionToken, firstTab!));
    }

    [Fact]
    public async Task An_unknown_or_absent_session_receives_no_token()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();

        Assert.Null(await IssueAsync(harness, null));
        Assert.Null(await IssueAsync(harness, "   "));
        Assert.Null(await IssueAsync(harness, new string('a', 43)));
    }

    [Fact]
    public async Task A_revoked_session_receives_no_token()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();
        (string sessionToken, _) = await SignInAsync(harness);

        await using (AsyncServiceScope scope = harness.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider
                .GetRequiredService<IBrowserSessionPort>()
                .LogoutAsync(sessionToken, default);
        }

        Assert.Null(await IssueAsync(harness, sessionToken));
    }

    [Fact]
    public async Task The_token_is_never_the_session_token()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();
        (string sessionToken, string loginToken) = await SignInAsync(harness);

        Assert.NotEqual(sessionToken, loginToken);
        Assert.DoesNotContain(sessionToken, loginToken, StringComparison.Ordinal);
    }

    private static async ValueTask<bool> IsUsableAsync(
        AccountSurfaceHarness harness,
        string sessionToken,
        string antiforgeryToken)
    {
        string? digest = await harness.ReadAntiforgeryDigestAsync(sessionToken);
        return digest is not null &&
            Policy.Validate(
                "POST",
                BrowserAuthenticationKind.Cookie,
                antiforgeryToken,
                digest).IsAllowed;
    }

    private static async ValueTask<(string SessionToken, string AntiforgeryToken)>
        SignInAsync(AccountSurfaceHarness harness)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        Result<BrowserSessionResult> session = await scope.ServiceProvider
            .GetRequiredService<IBrowserSessionPort>()
            .LoginAsync(
                new BrowserLoginCommand("owner@example.com", Password, null, null),
                default);
        Assert.True(session.TryGetValue(out BrowserSessionResult? issued));
        string cookie = issued.SetCookieHeader.Split(';')[0];
        return (
            cookie[(cookie.IndexOf('=', StringComparison.Ordinal) + 1)..],
            issued.AntiforgeryToken);
    }

    private static async ValueTask<string?> IssueAsync(
        AccountSurfaceHarness harness,
        string? sessionToken)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IBrowserSessionPort>()
            .IssueAntiforgeryTokenAsync(sessionToken, default);
    }
}
