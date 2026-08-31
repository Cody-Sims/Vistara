using System.Buffers;
using System.Text;

namespace Vistara.Auth.Oidc;

/// <summary>
/// Reads a provider response body under a hard byte ceiling. A discovery
/// document, key set, or token response is attacker-reachable, and a provider
/// that omits or lies about Content-Length must not be able to exhaust memory.
/// </summary>
internal static class OidcBoundedContent
{
    internal static async Task<string?> ReadAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        // ArrayPool rounds a rent up to its bucket size, so the rented array is
        // routinely far larger than the ceiling. Every read must be clamped to
        // the ceiling rather than to buffer.Length, or a hostile provider could
        // stream a whole bucket past the limit in one read before the loop
        // condition is ever re-evaluated.
        int ceiling = maximumBytes + 1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ceiling);
        try
        {
            int total = 0;
            while (total < ceiling)
            {
                int read = await stream
                    .ReadAsync(buffer.AsMemory(total, ceiling - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return Encoding.UTF8.GetString(buffer, 0, total);
                }

                total += read;
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
