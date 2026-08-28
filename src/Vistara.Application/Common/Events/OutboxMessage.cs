using Vistara.Domain.Common;

namespace Vistara.Application.Common.Events;

public readonly record struct OutboxMessageId
{
    public OutboxMessageId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Outbox message IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct OutboxVersion
{
    public OutboxVersion(long value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Outbox version must be positive.");
        }

        Value = value;
    }

    public long Value { get; }

    public OutboxVersion Next() => new(checked(Value + 1));
}

public enum OutboxPublicationState
{
    Pending,
    Published,
}

public sealed class OutboxMessage
{
    private OutboxMessage(
        OutboxMessageId id,
        EventEnvelope envelope,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Envelope = envelope;
        CreatedAtUtc = createdAtUtc;
        Version = new OutboxVersion(1);
    }

    public OutboxMessageId Id { get; }

    public EventEnvelope Envelope { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public OutboxPublicationState PublicationState { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public OutboxVersion Version { get; private set; }

    public static OutboxMessage Create(
        OutboxMessageId id,
        EventEnvelope envelope,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Outbox timestamp must be UTC.", nameof(createdAtUtc));
        }

        if (createdAtUtc < envelope.Metadata.OccurredAtUtc)
        {
            throw new ArgumentException(
                "Outbox creation cannot precede event occurrence.",
                nameof(createdAtUtc));
        }

        return new OutboxMessage(id, envelope, createdAtUtc);
    }

    public Result MarkPublished(DateTimeOffset publishedAtUtc)
    {
        if (publishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Publication timestamp must be UTC.", nameof(publishedAtUtc));
        }

        if (PublicationState == OutboxPublicationState.Published)
        {
            return Result.Success();
        }

        if (publishedAtUtc < CreatedAtUtc)
        {
            return Result.Failure(
                ResultError.Validation(
                    "outbox.publication_precedes_creation",
                    "Publication cannot precede outbox creation."));
        }

        PublicationState = OutboxPublicationState.Published;
        PublishedAtUtc = publishedAtUtc;
        Version = Version.Next();
        return Result.Success();
    }
}
