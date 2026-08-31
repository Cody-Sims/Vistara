using Vistara.Application.Common;
using Vistara.Auth.Oidc;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// Deterministic random material. Every 32-byte draw is a distinct, repeatable
/// pattern so a test can prove that state, nonce, and verifier come from
/// disjoint material without depending on a real entropy source.
/// </summary>
internal sealed class SequentialOidcRandomSource : IOidcRandomSource
{
    private byte _draw;

    public int BytesProduced { get; private set; }

    public void Fill(Span<byte> destination)
    {
        _draw++;
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = unchecked((byte)((_draw * 37) + index));
        }

        BytesProduced += destination.Length;
    }
}

internal sealed class FixedOidcClock : IClock
{
    public FixedOidcClock(DateTimeOffset? utcNow = null) =>
        UtcNow = utcNow ?? new DateTimeOffset(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
}

internal static class OidcTestProvider
{
    internal static readonly Guid TenantId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    internal const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    internal static readonly Uri RedirectUri =
        new("https://vistara.example/signin-entra");

    internal static OidcProviderOptions CreateOptions(
        Uri? redirectUri = null,
        IReadOnlyCollection<string>? scopes = null,
        IReadOnlyCollection<string>? allowedSigningAlgorithms = null,
        TimeSpan? clockSkew = null,
        TimeSpan? httpTimeout = null,
        TimeSpan? metadataCacheLifetime = null,
        TimeSpan? metadataRefreshBackoff = null,
        TimeSpan? metadataStaleWhileUnavailable = null) =>
        new(
            TenantId,
            ClientId,
            redirectUri ?? RedirectUri,
            scopes: scopes,
            allowedSigningAlgorithms: allowedSigningAlgorithms,
            clockSkew: clockSkew,
            httpTimeout: httpTimeout,
            metadataCacheLifetime: metadataCacheLifetime,
            metadataRefreshBackoff: metadataRefreshBackoff,
            metadataStaleWhileUnavailable: metadataStaleWhileUnavailable);
}
