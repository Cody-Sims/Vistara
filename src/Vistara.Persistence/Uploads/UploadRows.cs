using Vistara.Persistence.Model;

namespace Vistara.Persistence.Uploads;

public sealed class QuotaUsageRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public long CommittedUploads { get; set; }
    public long CommittedBytes { get; set; }
    public long CommittedObjects { get; set; }
    public long CommittedComputeUnits { get; set; }
    public long CommittedJobs { get; set; }
    public long CommittedBudgetUnits { get; set; }
    public long ReservedUploads { get; set; }
    public long ReservedBytes { get; set; }
    public long ReservedObjects { get; set; }
    public long ReservedComputeUnits { get; set; }
    public long ReservedJobs { get; set; }
    public long ReservedBudgetUnits { get; set; }
    public long Version { get; set; }
}

public sealed class UploadReconciliationCheckpointRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid RunId { get; set; }
    public string? Cursor { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
