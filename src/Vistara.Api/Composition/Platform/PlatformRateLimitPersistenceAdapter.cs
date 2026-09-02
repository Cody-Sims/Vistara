using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Vistara.Application.Common;
using Vistara.Persistence.Auth;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// The database-backed request ceiling. The counter is shared by every replica
/// of a deployment, which is the point: the in-process framework limiter only
/// sees the requests one replica handled.
///
/// The bucket key is the connection peer as it stands after forwarded-header
/// processing. This adapter reads no header itself, because a header a client
/// controls cannot partition a limit, and honouring one from an untrusted hop
/// would let any caller mint an unlimited number of buckets. Whether that peer
/// is a client or a shared ingress is a property of the deployment, declared
/// as <see cref="PlatformRateLimitOptions.PartitionMode"/> and checked against
/// the security composition at startup - behind an ingress with no trusted
/// proxy, each bucket is a ceiling for the whole deployment.
/// </summary>
internal sealed class PlatformRateLimitPersistenceAdapter(
    RelationalRateLimitStore store,
    IClock clock,
    IOptions<PlatformRateLimitOptions> options) : IPlatformRateLimitHook
{
    public async ValueTask<PlatformRateLimitDecision> CheckAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (Bucket(context.Request.Path) is not { } bucket)
        {
            return PlatformRateLimitDecision.Allow();
        }

        string client = context.Connection.RemoteIpAddress is { } address
            ? Normalize(address)
            : "unknown";
        string keyHash = Hash(
            string.Concat(
                "vistara:rate:",
                PlatformRateLimitBuckets.Key(bucket),
                ":",
                client));
        PlatformRateLimitOptions limits = options.Value;
        PersistedRateLimitDecision decision = await store.TryAcquireAsync(
            keyHash,
            clock.UtcNow,
            limits.Window,
            limits.LimitFor(bucket),
            cancellationToken);
        return decision.IsAllowed
            ? PlatformRateLimitDecision.Allow()
            : PlatformRateLimitDecision.Reject(decision.RetryAfter);
    }

    private static PlatformRateLimitBucket? Bucket(PathString path)
    {
        if (path.StartsWithSegments("/api/v1/events"))
        {
            return PlatformRateLimitBucket.Events;
        }

        if (path.StartsWithSegments("/delivery"))
        {
            return PlatformRateLimitBucket.Delivery;
        }

        if (path.StartsWithSegments("/media"))
        {
            return PlatformRateLimitBucket.Media;
        }

        return path.StartsWithSegments("/api/v1")
            ? PlatformRateLimitBucket.Api
            : null;
    }

    private static string Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
