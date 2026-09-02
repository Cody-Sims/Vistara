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
/// The bucket key is the transport peer address. No forwarded header is read
/// here, because a header a client controls cannot partition a limit, and
/// trusting one behind an untrusted hop would let any caller mint an unlimited
/// number of buckets. The consequence is deliberate and has to be understood
/// when the limits are set: behind a reverse proxy or a managed ingress with
/// no trusted proxy network, every request shares one peer, so each bucket is
/// a ceiling for the whole deployment and not a per-client budget. The limits
/// are therefore configuration rather than constants; see
/// <see cref="PlatformRateLimitOptions"/>.
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
            string.Concat("vistara:rate:", Key(bucket), ":", client));
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

    /// <summary>
    /// The stored key segment. These strings are part of the persisted key, so
    /// changing one silently resets every live window.
    /// </summary>
    private static string Key(PlatformRateLimitBucket bucket) => bucket switch
    {
        PlatformRateLimitBucket.Events => "events",
        PlatformRateLimitBucket.Delivery => "delivery",
        PlatformRateLimitBucket.Media => "media",
        _ => "api",
    };

    private static string Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
