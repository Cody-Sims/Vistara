namespace Vistara.Persistence.Outbox;

public interface IOutboxTenantContext
{
    Guid TenantId { get; }
}

internal sealed class OutboxMessageRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int EventVersion { get; set; }
    public string ClientPayload { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? ClaimId { get; set; }
    public Guid? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedUntilUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public long Version { get; set; }
}

internal sealed class EventLogRow
{
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int EventVersion { get; set; }
    public string ClientPayload { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public DateTimeOffset RetainedAtUtc { get; set; }
}

internal sealed class OutboxSequenceRow
{
    public Guid TenantId { get; set; }
    public long CurrentSequence { get; set; }
    public long LastPublishedSequence { get; set; }
    public long Version { get; set; }
}
