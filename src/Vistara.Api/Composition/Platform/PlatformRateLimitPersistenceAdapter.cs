using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Common;
using Vistara.Persistence.Auth;

namespace Vistara.Api.Composition.Platform;

internal sealed class PlatformRateLimitPersistenceAdapter(
    RelationalRateLimitStore store,
    IClock clock) : IPlatformRateLimitHook
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public async ValueTask<PlatformRateLimitDecision> CheckAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        string? bucket = Bucket(context.Request.Path);
        if (bucket is null)
        {
            return PlatformRateLimitDecision.Allow();
        }

        string client = context.Connection.RemoteIpAddress is { } address
            ? Normalize(address)
            : "unknown";
        string keyHash = Hash(
            string.Concat("vistara:rate:", bucket, ":", client));
        PersistedRateLimitDecision decision = await store.TryAcquireAsync(
            keyHash,
            clock.UtcNow,
            Window,
            Limit(bucket),
            cancellationToken);
        return decision.IsAllowed
            ? PlatformRateLimitDecision.Allow()
            : PlatformRateLimitDecision.Reject(decision.RetryAfter);
    }

    private static string? Bucket(PathString path)
    {
        if (path.StartsWithSegments("/api/v1/events"))
        {
            return "events";
        }

        if (path.StartsWithSegments("/delivery"))
        {
            return "delivery";
        }

        if (path.StartsWithSegments("/media"))
        {
            return "media";
        }

        return path.StartsWithSegments("/api/v1") ? "api" : null;
    }

    private static int Limit(string bucket) => bucket switch
    {
        "events" => 30,
        "delivery" => 120,
        "media" => 600,
        _ => 300,
    };

    private static string Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
