using Vistara.Application.Common.Events;

namespace Vistara.UnitTests.Jobs;

public sealed class EventsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Event_sequence_next_is_monotonic()
    {
        EventSequence first = EventSequence.First;
        EventSequence second = first.Next();

        Assert.True(second > first);
        Assert.Equal(2, second.Value);
    }

    [Fact]
    public void Replay_page_requires_strictly_increasing_sequences()
    {
        EventEnvelope second = CreateEnvelope(2);
        EventEnvelope first = CreateEnvelope(1);

        var page = EventPage.Create([second, first]);

        Assert.True(page.IsFailure);
        Assert.Equal("events.sequence_not_monotonic", page.Error?.Code);
    }

    [Fact]
    public void Event_metadata_carries_ordering_and_replay_identity()
    {
        EventEnvelope envelope = CreateEnvelope(7);

        Assert.Equal(7, envelope.Metadata.Sequence.Value);
        Assert.Equal("job.completed", envelope.Metadata.EventType);
        Assert.Equal(1, envelope.Metadata.EventVersion);
        Assert.Equal(TimeSpan.Zero, envelope.Metadata.OccurredAtUtc.Offset);
        Assert.Equal(Guid.Parse("01990a2a-bc00-7000-8000-000000000034"), envelope.Metadata.CorrelationId);
    }

    [Fact]
    public void Outbox_message_moves_from_pending_to_published_idempotently()
    {
        OutboxMessage message = OutboxMessage.Create(
            new OutboxMessageId(Guid.Parse("01990a2a-bc00-7000-8000-000000000031")),
            CreateEnvelope(1),
            Now);

        Assert.Equal(OutboxPublicationState.Pending, message.PublicationState);
        Assert.True(message.MarkPublished(Now.AddMinutes(1)).IsSuccess);
        OutboxVersion publishedVersion = message.Version;

        Assert.True(message.MarkPublished(Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(OutboxPublicationState.Published, message.PublicationState);
        Assert.Equal(Now.AddMinutes(1), message.PublishedAtUtc);
        Assert.Equal(publishedVersion, message.Version);
    }

    [Fact]
    public void Event_and_outbox_ids_require_uuid_version_seven()
    {
        Guid versionFour = Guid.Parse("11111111-1111-4111-8111-111111111111");

        Assert.Throws<ArgumentException>(() => new EventId(versionFour));
        Assert.Throws<ArgumentException>(() => new EventTenantId(versionFour));
        Assert.Throws<ArgumentException>(() => new OutboxMessageId(versionFour));
    }

    private static EventEnvelope CreateEnvelope(long sequence) =>
        new(
            new EventMetadata(
                new EventId(Guid.Parse($"01990a2a-bc00-7000-8000-{sequence:D12}")),
                new EventTenantId(Guid.Parse("01990a2a-bc00-7000-8000-000000000032")),
                new EventSequence(sequence),
                "job.completed",
                eventVersion: 1,
                Now,
                correlationId: Guid.Parse("01990a2a-bc00-7000-8000-000000000034"),
                causationId: Guid.Parse("01990a2a-bc00-7000-8000-000000000035")),
            """{"jobId":"job-1","state":"completed"}""");
}
