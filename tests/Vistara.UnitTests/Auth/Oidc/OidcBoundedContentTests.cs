using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// The bounded reader is the only thing standing between an unbounded provider
/// body and process memory, so these tests assert on how it drives the source
/// stream rather than only on the returned failure.
/// </summary>
public sealed class OidcBoundedContentTests
{
    [Fact]
    public async Task Bounded_reader_never_requests_more_than_the_ceiling_from_the_metadata_body()
    {
        using var provider = new OidcProviderFixture();
        OidcHttpTestTransport.RepeatingStream? recorder = null;
        provider.Transport.MetadataResponse = () =>
        {
            (HttpResponseMessage response, OidcHttpTestTransport.RepeatingStream stream) =
                OidcHttpTestTransport.RecordedEndlessStream();
            recorder = stream;
            return response;
        };
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(OidcErrors.MetadataUnavailable.Code, result.Error?.Code);
        Assert.NotNull(recorder);
        Assert.True(
            recorder.LargestRequestedRead <= OidcMetadataCache.MaximumDocumentBytes + 1,
            $"a single read asked for {recorder.LargestRequestedRead} bytes, which is past the ceiling");
        Assert.True(
            recorder.TotalBytesRead <= OidcMetadataCache.MaximumDocumentBytes + 1,
            $"the reader consumed {recorder.TotalBytesRead} bytes, which is past the ceiling");
    }

    [Fact]
    public async Task Bounded_reader_never_requests_more_than_the_ceiling_from_the_token_body()
    {
        using var provider = new OidcProviderFixture();
        OidcHttpTestTransport.RepeatingStream? recorder = null;
        provider.Transport.TokenResponse = () =>
        {
            (HttpResponseMessage response, OidcHttpTestTransport.RepeatingStream stream) =
                OidcHttpTestTransport.RecordedEndlessStream();
            recorder = stream;
            return response;
        };
        OidcTokenClient client = OidcCredentialStubs.CreateTokenClient(provider);

        Result<OidcTokenSet> result = await client.RedeemAuthorizationCodeAsync(
            OidcCredentialStubs.Redemption(),
            provider.CreateMetadata(),
            CancellationToken.None);

        Assert.Equal(OidcErrors.TokenExchangeFailed.Code, result.Error?.Code);
        Assert.NotNull(recorder);
        Assert.True(
            recorder.LargestRequestedRead <= OidcTokenClient.MaximumResponseBytes + 1,
            $"a single read asked for {recorder.LargestRequestedRead} bytes, which is past the ceiling");
        Assert.True(
            recorder.TotalBytesRead <= OidcTokenClient.MaximumResponseBytes + 1,
            $"the reader consumed {recorder.TotalBytesRead} bytes, which is past the ceiling");
    }

    /// <summary>
    /// ArrayPool rounds a rent up to its bucket size, so the rented array is
    /// larger than the ceiling by construction. This asserts the reader clamps
    /// to the ceiling and not to the oversized buffer it was handed.
    /// </summary>
    [Fact]
    public async Task Bounded_reader_clamps_to_the_ceiling_not_the_oversized_pool_bucket()
    {
        using var provider = new OidcProviderFixture();
        OidcHttpTestTransport.RepeatingStream? recorder = null;
        provider.Transport.MetadataResponse = () =>
        {
            (HttpResponseMessage response, OidcHttpTestTransport.RepeatingStream stream) =
                OidcHttpTestTransport.RecordedEndlessStream();
            recorder = stream;
            return response;
        };
        using OidcMetadataCache cache = provider.CreateCache();

        _ = await cache.GetAsync(CancellationToken.None);

        Assert.NotNull(recorder);
        int pooledBucketSize = System.Buffers.ArrayPool<byte>.Shared
            .Rent(OidcMetadataCache.MaximumDocumentBytes + 1)
            .Length;
        Assert.True(
            pooledBucketSize > OidcMetadataCache.MaximumDocumentBytes + 1,
            "the pool must hand back an oversized bucket for this test to mean anything");
        Assert.True(recorder.LargestRequestedRead < pooledBucketSize);
    }

    [Fact]
    public async Task Bounded_reader_still_accepts_a_body_that_exactly_fills_the_ceiling()
    {
        using var provider = new OidcProviderFixture();
        string padding = new('p', OidcMetadataCache.MaximumDocumentBytes - 600);
        string document = provider.BuildMetadataJson(padding: padding);
        Assert.True(document.Length <= OidcMetadataCache.MaximumDocumentBytes);
        provider.Transport.MetadataResponse = () => OidcHttpTestTransport.Json(document);
        using OidcMetadataCache cache = provider.CreateCache();

        Result<OidcProviderMetadata> result = await cache.GetAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
    }
}
