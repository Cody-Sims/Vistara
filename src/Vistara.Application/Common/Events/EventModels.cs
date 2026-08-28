using Vistara.Domain.Common;

namespace Vistara.Application.Common.Events;

public readonly record struct EventId
{
    public EventId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Event IDs must be non-empty UUIDv7 values.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct EventTenantId
{
    public EventTenantId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Event tenant IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct EventSequence : IComparable<EventSequence>
{
    public EventSequence(long value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Event sequence must be positive.");
        }

        Value = value;
    }

    public static EventSequence First { get; } = new(1);

    public long Value { get; }

    public EventSequence Next() => new(checked(Value + 1));

    public int CompareTo(EventSequence other) => Value.CompareTo(other.Value);

    public static bool operator <(EventSequence left, EventSequence right) =>
        left.CompareTo(right) < 0;

    public static bool operator >(EventSequence left, EventSequence right) =>
        left.CompareTo(right) > 0;

    public static bool operator <=(EventSequence left, EventSequence right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(EventSequence left, EventSequence right) =>
        left.CompareTo(right) >= 0;
}

public readonly record struct EventCursor
{
    public EventCursor(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Event cursor cannot be negative.");
        }

        Value = value;
    }

    public long Value { get; }
}

public sealed record EventMetadata
{
    public EventMetadata(
        EventId eventId,
        EventTenantId tenantId,
        EventSequence sequence,
        string eventType,
        int eventVersion,
        DateTimeOffset occurredAtUtc,
        Guid correlationId,
        Guid? causationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventVersion),
                "Event version must be positive.");
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Event timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("Correlation ID cannot be empty.", nameof(correlationId));
        }

        if (causationId == Guid.Empty)
        {
            throw new ArgumentException("Causation ID cannot be empty.", nameof(causationId));
        }

        EventId = eventId;
        TenantId = tenantId;
        Sequence = sequence;
        EventType = eventType;
        EventVersion = eventVersion;
        OccurredAtUtc = occurredAtUtc;
        CorrelationId = correlationId;
        CausationId = causationId;
    }

    public EventId EventId { get; }

    public EventTenantId TenantId { get; }

    public EventSequence Sequence { get; }

    public string EventType { get; }

    public int EventVersion { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public Guid CorrelationId { get; }

    public Guid? CausationId { get; }
}

public sealed record EventEnvelope
{
    public EventEnvelope(EventMetadata metadata, string clientPayload)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPayload);
        Metadata = metadata;
        ClientPayload = clientPayload;
    }

    public EventMetadata Metadata { get; }

    public string ClientPayload { get; }
}

public sealed class EventPage
{
    private EventPage(IReadOnlyList<EventEnvelope> events)
    {
        Events = events;
    }

    public IReadOnlyList<EventEnvelope> Events { get; }

    public static Result<EventPage> Create(IEnumerable<EventEnvelope> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        EventEnvelope[] orderedEvents = [.. events];
        for (int index = 1; index < orderedEvents.Length; index++)
        {
            if (orderedEvents[index].Metadata.Sequence <=
                orderedEvents[index - 1].Metadata.Sequence)
            {
                return Result.Failure<EventPage>(
                    ResultError.Validation(
                        "events.sequence_not_monotonic",
                        "Replay event sequences must be strictly increasing."));
            }
        }

        return Result.Success(new EventPage(orderedEvents));
    }
}
