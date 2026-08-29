using Microsoft.AspNetCore.Http;
using Vistara.Application.Common.Events;

namespace Vistara.Api.Features.Events;

public enum EventStreamAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
}

public sealed record EventStreamAccess
{
    private EventStreamAccess(EventStreamAccessStatus status, Guid? tenantId)
    {
        Status = status;
        TenantId = tenantId;
    }

    public EventStreamAccessStatus Status { get; }
    public Guid? TenantId { get; }

    public static EventStreamAccess Authorized(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException(
                "An authorized event stream requires a UUIDv7 tenant ID.",
                nameof(tenantId));
        }

        return new EventStreamAccess(EventStreamAccessStatus.Authorized, tenantId);
    }

    public static EventStreamAccess Unauthenticated() =>
        new(EventStreamAccessStatus.Unauthenticated, null);

    public static EventStreamAccess Forbidden() =>
        new(EventStreamAccessStatus.Forbidden, null);
}

public interface IEventStreamAuthorizationPort
{
    ValueTask<EventStreamAccess> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken);
}

public sealed record EventStreamBounds
{
    public EventStreamBounds(long oldestAvailable, long latest)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(oldestAvailable);

        if (latest < 0 || (oldestAvailable > 0 && latest < oldestAvailable))
        {
            throw new ArgumentOutOfRangeException(nameof(latest));
        }

        OldestAvailable = oldestAvailable;
        Latest = latest;
    }

    public long OldestAvailable { get; }
    public long Latest { get; }
}

public interface IEventStreamSource
{
    /// <summary>
    /// Returns detached bounds without retaining a database transaction.
    /// </summary>
    ValueTask<EventStreamBounds> GetBoundsAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a bounded, detached replay page before network streaming starts.
    /// </summary>
    ValueTask<IReadOnlyList<EventEnvelope>> ReadReplayAsync(
        Guid tenantId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lazily observes live events without an unbounded buffer or database transaction.
    /// </summary>
    IAsyncEnumerable<EventEnvelope> ReadLiveAsync(
        Guid tenantId,
        long afterSequence,
        CancellationToken cancellationToken);
}

public interface IEventStreamHeartbeatDelay
{
    ValueTask DelayAsync(TimeSpan interval, CancellationToken cancellationToken);
}

public sealed class EventStreamOptions
{
    public int MaximumReplayEvents { get; init; } = 200;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    public int RetryMilliseconds { get; init; } = 3_000;

    internal void Validate()
    {
        if (MaximumReplayEvents is < 1 or > 200)
        {
            throw new InvalidOperationException("Maximum replay events must be between 1 and 200.");
        }

        if (HeartbeatInterval <= TimeSpan.Zero ||
            HeartbeatInterval > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                "Heartbeat interval must be positive and at most five minutes.");
        }

        if (RetryMilliseconds is < 1_000 or > 60_000)
        {
            throw new InvalidOperationException(
                "The SSE retry hint must be between one and sixty seconds.");
        }
    }
}
