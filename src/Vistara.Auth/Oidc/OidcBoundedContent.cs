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
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maximumBytes + 1);
        try
        {
            int total = 0;
            while (total <= maximumBytes)
            {
                int read = await stream
                    .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
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
