using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Vistara.Application.Common;
using Vistara.Auth.Jwt;

namespace Vistara.Api.Composition.Platform;

internal sealed class PlatformJwtMetadataSigningKeyResolver(
    IHttpClientFactory httpClientFactory,
    IClock clock) : IJwtMetadataSigningKeyResolver, IDisposable
{
    internal const string HttpClientName = "Vistara.JwtMetadata";
    private const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Uri, CacheEntry> _cache = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async ValueTask<IReadOnlyCollection<SecurityKey>> ResolveAsync(
        Uri metadataAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadataAddress);
        DateTimeOffset now = clock.UtcNow;
        if (_cache.TryGetValue(metadataAddress, out CacheEntry? cached) &&
            cached.ExpiresAtUtc > now)
        {
            return cached.Keys;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = clock.UtcNow;
            if (_cache.TryGetValue(metadataAddress, out cached) &&
                cached.ExpiresAtUtc > now)
            {
                return cached.Keys;
            }

            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            string metadataJson = await ReadDocumentAsync(
                client,
                metadataAddress,
                cancellationToken);
            Uri jwksAddress = ReadJwksAddress(metadataAddress, metadataJson);
            string jwksJson = await ReadDocumentAsync(
                client,
                jwksAddress,
                cancellationToken);
            SecurityKey[] keys = new JsonWebKeySet(jwksJson)
                .GetSigningKeys()
                .ToArray();
            if (keys.Length == 0)
            {
                throw new InvalidOperationException(
                    "JWT metadata did not expose signing keys.");
            }

            _cache[metadataAddress] = new CacheEntry(
                keys,
                now.Add(CacheDuration));
            return keys;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<string> ReadDocumentAsync(
        HttpClient client,
        Uri address,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            address,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                "JWT metadata could not be retrieved.");
        }

        if (response.Content.Headers.ContentLength > MaximumDocumentBytes)
        {
            throw new InvalidOperationException(
                "JWT metadata exceeded the maximum document size.");
        }

        await using Stream source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumDocumentBytes)
            {
                throw new InvalidOperationException(
                    "JWT metadata exceeded the maximum document size.");
            }

            destination.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(destination.ToArray());
    }

    private static Uri ReadJwksAddress(
        Uri metadataAddress,
        string metadataJson)
    {
        using JsonDocument document = JsonDocument.Parse(metadataJson);
        if (!document.RootElement.TryGetProperty(
                "jwks_uri",
                out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(property.GetString(), UriKind.Absolute, out Uri? address) ||
            address.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            !string.Equals(
                address.IdnHost,
                metadataAddress.IdnHost,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "JWT metadata exposed an invalid signing-key address.");
        }

        return address;
    }

    private sealed record CacheEntry(
        IReadOnlyCollection<SecurityKey> Keys,
        DateTimeOffset ExpiresAtUtc);

    public void Dispose() => _refreshLock.Dispose();
}
