using System.Net;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// A provider outage is the case where an unrated cache does the most damage:
/// every arriving request would queue behind its own network timeout and the
/// deployment would amplify the outage into a request storm against the
/// provider. These tests pin the backoff and staleness policy on the cold and
/// expired paths, not only on forced refreshes.
/// </summary>
public sealed class OidcMetadataOutagePolicyTests
{
    [Fact]
    public async Task Cold_outage_produces_one_fetch_per_backoff_window_for_concurrent_callers()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata>[] results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(async _ =>
                await cache.GetAsync(CancellationToken.None).ConfigureAwait(false)));

        Assert.All(
            results,
            result => Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code));
        Assert.Single(provider.Transport.RequestedUris);
    }

    [Fact]
    public async Task Cold_outage_suppresses_further_fetches_until_the_backoff_elapses()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using OidcMetadataCache cache = provider.CreateCache();

        _ = await cache.GetAsync(CancellationToken.None);
        Assert.Single(provider.Transport.RequestedUris);

        provider.Clock.Advance(TimeSpan.FromMinutes(4));
        Result<OidcProviderMetadata> suppressed = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(OidcErrors.MetadataUnavailable.Code, suppressed.Error?.Code);
        Assert.Single(provider.Transport.RequestedUris);

        provider.Clock.Advance(TimeSpan.FromMinutes(2));
        _ = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, provider.Transport.RequestedUris.Count);
    }

    [Fact]
    public async Task Cold_outage_recovers_on_the_first_attempt_after_the_backoff()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        Func<HttpResponseMessage> healthy = provider.Transport.MetadataResponse!;
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> failed = await cache.GetAsync(CancellationToken.None);
        provider.Clock.Advance(TimeSpan.FromMinutes(6));
        provider.Transport.MetadataResponse = healthy;
        Result<OidcProviderMetadata> recovered = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, failed.Error?.Code);
        Assert.True(recovered.IsSuccess);
    }

    [Fact]
    public async Task Expired_outage_produces_one_fetch_and_serves_the_stale_document()
    {
        using var provider = new OidcProviderFixture(
            metadataCacheLifetime: TimeSpan.FromMinutes(30),
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        using OidcMetadataCache cache = provider.CreateCache();
        Result<OidcProviderMetadata> seeded = await cache.GetAsync(CancellationToken.None);
        Assert.True(seeded.TryGetValue(out OidcProviderMetadata? original));

        provider.Clock.Advance(TimeSpan.FromMinutes(31));
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        int before = provider.Transport.RequestedUris.Count;

        Result<OidcProviderMetadata>[] results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(async _ =>
                await cache.GetAsync(CancellationToken.None).ConfigureAwait(false)));

        Assert.All(results, result =>
        {
            Assert.True(result.TryGetValue(out OidcProviderMetadata? served));
            Assert.Equal(original.RetrievedAt, served.RetrievedAt);
        });
        Assert.Equal(before + 1, provider.Transport.RequestedUris.Count);
    }

    [Fact]
    public async Task Expired_outage_fails_closed_once_the_stale_window_is_exhausted()
    {
        using var provider = new OidcProviderFixture(
            metadataCacheLifetime: TimeSpan.FromMinutes(30),
            metadataRefreshBackoff: TimeSpan.FromMinutes(5),
            metadataStaleWhileUnavailable: TimeSpan.FromMinutes(20));
        using OidcMetadataCache cache = provider.CreateCache();
        _ = await cache.GetAsync(CancellationToken.None);
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        provider.Clock.Advance(TimeSpan.FromMinutes(45));
        Result<OidcProviderMetadata> stale = await cache.GetAsync(CancellationToken.None);

        provider.Clock.Advance(TimeSpan.FromMinutes(10));
        Result<OidcProviderMetadata> expired = await cache.GetAsync(CancellationToken.None);

        Assert.True(stale.IsSuccess);
        Assert.Equal(OidcErrors.MetadataUnavailable.Code, expired.Error?.Code);
    }

    [Fact]
    public async Task A_zero_stale_window_refuses_to_serve_an_expired_document()
    {
        using var provider = new OidcProviderFixture(
            metadataCacheLifetime: TimeSpan.FromMinutes(30),
            metadataRefreshBackoff: TimeSpan.FromMinutes(5),
            metadataStaleWhileUnavailable: TimeSpan.Zero);
        using OidcMetadataCache cache = provider.CreateCache();
        _ = await cache.GetAsync(CancellationToken.None);
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        provider.Clock.Advance(TimeSpan.FromMinutes(31));
        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
    }

    /// <summary>
    /// The suppressed callers must return without waiting on the network. A
    /// slow provider makes the difference observable: only the one caller that
    /// owns the attempt should pay the timeout.
    /// </summary>
    [Fact]
    public async Task Suppressed_callers_return_without_paying_the_provider_timeout()
    {
        using var provider = new OidcProviderFixture(
            httpTimeout: TimeSpan.FromMilliseconds(300),
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        provider.Transport.MetadataDelay = TimeSpan.FromSeconds(30);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> first = await cache.GetAsync(CancellationToken.None);

        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Result<OidcProviderMetadata>[] suppressed = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(async _ =>
                await cache.GetAsync(CancellationToken.None).ConfigureAwait(false)));
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, first.Error?.Code);
        Assert.All(
            suppressed,
            result => Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code));
        Assert.Single(provider.Transport.RequestedUris);
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(250),
            $"suppressed callers waited {elapsed.TotalMilliseconds:F0}ms, so they queued on the network");
    }

    [Fact]
    public async Task A_forced_refresh_during_an_outage_still_honours_the_backoff()
    {
        using var provider = new OidcProviderFixture(
            metadataRefreshBackoff: TimeSpan.FromMinutes(5));
        using OidcMetadataCache cache = provider.CreateCache();
        _ = await cache.GetAsync(CancellationToken.None);
        int before = provider.Transport.RequestedUris.Count;
        provider.Transport.MetadataResponse = () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        provider.Clock.Advance(TimeSpan.FromMinutes(1));
        Result<OidcProviderMetadata>[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
                await cache.RefreshAsync(CancellationToken.None).ConfigureAwait(false)));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(before, provider.Transport.RequestedUris.Count);
    }

    [Fact]
    public void Options_bound_the_stale_window_and_default_it_explicitly()
    {
        var defaults = new OidcProviderOptions(
            OidcTestProvider.TenantId,
            OidcTestProvider.ClientId,
            OidcTestProvider.RedirectUri);

        Assert.Equal(TimeSpan.FromHours(1), defaults.MetadataStaleWhileUnavailable);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OidcProviderOptions(
            OidcTestProvider.TenantId,
            OidcTestProvider.ClientId,
            OidcTestProvider.RedirectUri,
            metadataStaleWhileUnavailable: TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OidcProviderOptions(
            OidcTestProvider.TenantId,
            OidcTestProvider.ClientId,
            OidcTestProvider.RedirectUri,
            metadataStaleWhileUnavailable: TimeSpan.FromSeconds(-1)));
    }
}
