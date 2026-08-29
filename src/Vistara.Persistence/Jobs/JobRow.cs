namespace Vistara.Persistence.Jobs;

public sealed class JobRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int PayloadVersion { get; set; }
    public string DedupeKey { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int MaxAttempts { get; set; }
    public int Attempts { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? TraceParent { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseAcquiredAtUtc { get; set; }
    public DateTimeOffset? LeaseHeartbeatAtUtc { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}
