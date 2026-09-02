using System.Net;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// The refresh backoff exists to stop Vistara amplifying a provider outage.
/// It is spent by an attempt that actually observed the provider. A caller who
/// disconnects mid-flight observes nothing, so charging that attempt to the
/// budget would let anyone who can open and abandon requests in a loop hold
/// the cache in a permanent suppressed outage and deny every legitimate
/// sign-in.
///
/// These tests pin both halves: a caller cancellation must leave the budget
/// untouched, and the library's own elapsed HTTP budget must still spend it.
/// </summary>
public sealed class OidcMetadataCancellationBudgetTests
{
    [Fact]
    public async Task A_cancelled_cold_fetch_leaves_the_refresh_budget_unspent()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        using OidcMetadataCache cache = provider.CreateCache();

        await CancelAnInFlightFetchAsync(provider, cache);
        int afterCancellation = provider.Transport.RequestedUris.Count;

        // No clock movement at all: if the cancelled attempt had been charged
        // to the budget, this caller would be suppressed.
        Result<OidcProviderMetadata> legitimate = await cache.GetAsync(CancellationToken.None);

        Assert.True(legitimate.IsSuccess);
        Assert.True(provider.Transport.RequestedUris.Count > afterCancellation);
    }

    [Fact]
    public async Task A_cancelled_fetch_restores_the_previous_attempt_timestamp()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        Func<HttpResponseMessage> healthy = provider.Transport.MetadataResponse!;
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using OidcMetadataCache cache = provider.CreateCache();

        // A real failure at T0 spends the budget until T0 + 5 minutes.
        Result<OidcProviderMetadata> outage = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(OidcErrors.MetadataUnavailable.Code, outage.Error?.Code);

        // A cancelled caller at T0 + 6 owns an attempt and abandons it. That
        // must not push the window out to T0 + 11.
        provider.Clock.Advance(TimeSpan.FromMinutes(6));
        await CancelAnInFlightFetchAsync(provider, cache);
        int afterCancellation = provider.Transport.RequestedUris.Count;

        provider.Transport.MetadataResponse = healthy;
        Result<OidcProviderMetadata> legitimate = await cache.GetAsync(CancellationToken.None);

        Assert.True(legitimate.IsSuccess);
        Assert.True(provider.Transport.RequestedUris.Count > afterCancellation);
    }

    [Fact]
    public async Task A_cancelled_refresh_does_not_stall_a_signing_key_rollover()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        using OidcMetadataCache cache = provider.CreateCache();
        Result<OidcProviderMetadata> seeded = await cache.GetAsync(CancellationToken.None);
        Assert.True(seeded.IsSuccess);

        provider.Clock.Advance(TimeSpan.FromMinutes(6));
        await CancelAnInFlightRefreshAsync(provider, cache);

        // The provider has rotated. A forced refresh is how an unknown key
        // identifier gets resolved, and the abandoned caller must not have
        // locked that path out.
        provider.Transport.JwksResponse = () =>
            OidcHttpTestTransport.Json(provider.BuildRotatedJwksJson());
        Result<OidcProviderMetadata> rolled = await cache.RefreshAsync(CancellationToken.None);

        Assert.True(rolled.TryGetValue(out OidcProviderMetadata? metadata));
        Assert.Contains(
            metadata.SigningKeys,
            key => key.KeyId == OidcProviderFixture.RotatedSigningKeyId);
    }

    [Fact]
    public async Task Concurrent_cancelled_callers_never_create_more_than_one_in_flight_fetch()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Transport.MetadataRequestStarted = started;
        provider.Transport.MetadataDelay = Timeout.InfiniteTimeSpan;
        using OidcMetadataCache cache = provider.CreateCache();
        using var cancellation = new CancellationTokenSource();

        Task<Result<OidcProviderMetadata>>[] callers = Enumerable.Range(0, 16)
            .Select(_ => cache.GetAsync(cancellation.Token).AsTask())
            .ToArray();
        await AwaitFetchStartAsync(started.Task);
        await cancellation.CancelAsync();

        foreach (Task<Result<OidcProviderMetadata>> caller in callers)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller);
        }

        Assert.Equal(1, provider.Transport.MaxConcurrentRequests);
        Assert.Single(provider.Transport.RequestedUris);
    }

    [Fact]
    public async Task Concurrent_cancelled_callers_leave_the_budget_open_for_a_later_caller()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Transport.MetadataRequestStarted = started;
        provider.Transport.MetadataDelay = Timeout.InfiniteTimeSpan;
        using OidcMetadataCache cache = provider.CreateCache();
        using var cancellation = new CancellationTokenSource();

        Task<Result<OidcProviderMetadata>>[] callers = Enumerable.Range(0, 16)
            .Select(_ => cache.GetAsync(cancellation.Token).AsTask())
            .ToArray();
        await AwaitFetchStartAsync(started.Task);
        await cancellation.CancelAsync();
        foreach (Task<Result<OidcProviderMetadata>> caller in callers)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller);
        }

        provider.Transport.MetadataRequestStarted = null;
        provider.Transport.MetadataDelay = TimeSpan.Zero;
        Result<OidcProviderMetadata> legitimate = await cache.GetAsync(CancellationToken.None);

        Assert.True(legitimate.IsSuccess);
    }

    /// <summary>
    /// The other half of the rule. An elapsed HTTP budget is a real
    /// observation that the provider is unresponsive, so it must suppress the
    /// next call exactly as an error response does.
    /// </summary>
    [Fact]
    public async Task The_libraries_own_http_budget_still_spends_the_refresh_backoff()
    {
        using var provider = new OidcProviderFixture(
            httpTimeout: TimeSpan.FromMilliseconds(50),
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        provider.Transport.MetadataDelay = TimeSpan.FromSeconds(30);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> timedOut = await cache.GetAsync(CancellationToken.None);
        int afterTimeout = provider.Transport.RequestedUris.Count;

        provider.Transport.MetadataDelay = TimeSpan.Zero;
        Result<OidcProviderMetadata> suppressed = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, timedOut.Error?.Code);
        Assert.Equal(OidcErrors.MetadataUnavailable.Code, suppressed.Error?.Code);
        Assert.Equal(afterTimeout, provider.Transport.RequestedUris.Count);
    }

    [Fact]
    public async Task A_timeout_that_races_a_caller_disconnect_is_still_treated_as_an_outage()
    {
        using var provider = new OidcProviderFixture(
            httpTimeout: TimeSpan.FromMilliseconds(50),
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        provider.Transport.MetadataDelay = TimeSpan.FromSeconds(30);
        using OidcMetadataCache cache = provider.CreateCache();
        using var cancellation = new CancellationTokenSource();

        // The budget elapses first; the caller then walks away. Attribution
        // must follow which source fired, not which token happens to be
        // cancelled by the time the failure is inspected.
        Result<OidcProviderMetadata> timedOut = await cache.GetAsync(cancellation.Token);
        await cancellation.CancelAsync();
        int afterTimeout = provider.Transport.RequestedUris.Count;

        provider.Transport.MetadataDelay = TimeSpan.Zero;
        Result<OidcProviderMetadata> suppressed = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, timedOut.Error?.Code);
        Assert.Equal(OidcErrors.MetadataUnavailable.Code, suppressed.Error?.Code);
        Assert.Equal(afterTimeout, provider.Transport.RequestedUris.Count);
    }

    /// <summary>
    /// The denial-of-service case the whole fix exists for.
    /// </summary>
    [Fact]
    public async Task Repeated_caller_disconnects_cannot_hold_the_cache_in_an_outage()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(30));
        using OidcMetadataCache cache = provider.CreateCache();

        for (int attempt = 0; attempt < 12; attempt++)
        {
            await CancelAnInFlightFetchAsync(provider, cache);
        }

        // The clock has not moved, so a budget spent by any one of those
        // abandoned attempts would still be suppressing this caller.
        Result<OidcProviderMetadata> legitimate = await cache.GetAsync(CancellationToken.None);

        Assert.True(legitimate.IsSuccess);
    }

    [Fact]
    public async Task Repeated_disconnects_during_a_real_outage_do_not_extend_the_window()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(10));
        Func<HttpResponseMessage> healthy = provider.Transport.MetadataResponse!;
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using OidcMetadataCache cache = provider.CreateCache();
        _ = await cache.GetAsync(CancellationToken.None);

        // Disconnects arrive throughout the suppression window. None of them
        // owns an attempt, so none of them can push the window out.
        for (int minute = 1; minute <= 9; minute++)
        {
            provider.Clock.Advance(TimeSpan.FromMinutes(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();
                await cache.GetAsync(cancelled.Token);
            });
        }

        provider.Clock.Advance(TimeSpan.FromMinutes(2));
        provider.Transport.MetadataResponse = healthy;
        Result<OidcProviderMetadata> recovered = await cache.GetAsync(CancellationToken.None);

        Assert.True(recovered.IsSuccess);
    }

    [Fact]
    public async Task A_caller_cancelled_before_it_starts_never_reaches_the_provider()
    {
        using var provider = new OidcProviderFixture();
        using OidcMetadataCache cache = provider.CreateCache();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.GetAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.RefreshAsync(cancellation.Token));

        Assert.Empty(provider.Transport.RequestedUris);
        Assert.True((await cache.GetAsync(CancellationToken.None)).IsSuccess);
    }

    /// <summary>
    /// A cancelled token exchange must surface as cancellation rather than as
    /// a fabricated provider verdict, and an elapsed budget must still read as
    /// an outage.
    /// </summary>
    [Fact]
    public async Task Token_exchange_separates_a_cancelled_caller_from_an_elapsed_budget()
    {
        using var timedOutProvider = new OidcProviderFixture(
            httpTimeout: TimeSpan.FromMilliseconds(50));
        timedOutProvider.Transport.TokenDelay = TimeSpan.FromSeconds(30);
        OidcTokenClient timedOutClient =
            OidcCredentialStubs.CreateTokenClient(timedOutProvider);

        Result<OidcTokenSet> timedOut = await timedOutClient.RedeemAuthorizationCodeAsync(
            OidcCredentialStubs.Redemption(),
            timedOutProvider.CreateMetadata(),
            CancellationToken.None);

        using var cancelledProvider = new OidcProviderFixture();
        cancelledProvider.Transport.TokenDelay = Timeout.InfiniteTimeSpan;
        OidcTokenClient cancelledClient =
            OidcCredentialStubs.CreateTokenClient(cancelledProvider);
        using var cancellation = new CancellationTokenSource();
        Task<Result<OidcTokenSet>> pending = cancelledClient.RedeemAuthorizationCodeAsync(
            OidcCredentialStubs.Redemption(),
            cancelledProvider.CreateMetadata(),
            cancellation.Token).AsTask();
        await WaitForRequestAsync(cancelledProvider);
        await cancellation.CancelAsync();

        Assert.Equal(OidcErrors.TokenEndpointUnavailable.Code, timedOut.Error?.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    private static async Task CancelAnInFlightFetchAsync(
        OidcProviderFixture provider,
        OidcMetadataCache cache)
    {
        await CancelAnInFlightAsync(provider, cache, forceRefresh: false);
    }

    private static async Task CancelAnInFlightRefreshAsync(
        OidcProviderFixture provider,
        OidcMetadataCache cache)
    {
        await CancelAnInFlightAsync(provider, cache, forceRefresh: true);
    }

    /// <summary>
    /// Cancels a fetch that has genuinely reached the transport, rather than
    /// racing a timer against it, so the attempt really did own the budget at
    /// the moment the caller went away.
    /// </summary>
    private static async Task CancelAnInFlightAsync(
        OidcProviderFixture provider,
        OidcMetadataCache cache,
        bool forceRefresh)
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Func<HttpResponseMessage>? healthy = provider.Transport.MetadataResponse;
        provider.Transport.MetadataRequestStarted = started;
        provider.Transport.MetadataDelay = Timeout.InfiniteTimeSpan;
        using var cancellation = new CancellationTokenSource();
        Task<Result<OidcProviderMetadata>> pending = forceRefresh
            ? cache.RefreshAsync(cancellation.Token).AsTask()
            : cache.GetAsync(cancellation.Token).AsTask();

        await AwaitFetchStartAsync(started.Task);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        provider.Transport.MetadataRequestStarted = null;
        provider.Transport.MetadataDelay = TimeSpan.Zero;
        provider.Transport.MetadataResponse = healthy;
    }

    /// <summary>
    /// Waits for the fetch to actually reach the transport, under a bound so a
    /// regression that suppresses the attempt fails with a clear message
    /// instead of hanging the suite.
    /// </summary>
    private static async Task AwaitFetchStartAsync(Task started)
    {
        Task first = await Task.WhenAny(started, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            ReferenceEquals(first, started),
            "the fetch never reached the provider, so the refresh budget had already been spent");
    }

    private static async Task WaitForRequestAsync(OidcProviderFixture provider)
    {
        for (int attempt = 0; attempt < 200 && provider.Transport.RequestedUris.Count == 0;
            attempt++)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(provider.Transport.RequestedUris);
    }
}
