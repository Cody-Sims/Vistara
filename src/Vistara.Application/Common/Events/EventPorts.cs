using Vistara.Domain.Common;

namespace Vistara.Application.Common.Events;

public interface IOutboxWriter
{
    /// <summary>
    /// Adds the message to the caller's current transaction.
    /// </summary>
    ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public interface IOutboxPublisherStore
{
    ValueTask<IReadOnlyList<OutboxMessage>> ReadPendingAsync(
        EventCursor after,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<Result> MarkPublishedAsync(
        OutboxMessageId messageId,
        OutboxVersion expectedVersion,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken);
}

public interface IEventLogReader
{
    /// <summary>
    /// Returns tenant-scoped events after the cursor in strictly increasing sequence order.
    /// </summary>
    ValueTask<Result<EventPage>> ReadAfterAsync(
        EventTenantId tenantId,
        EventCursor after,
        int maximumCount,
        CancellationToken cancellationToken);
}
