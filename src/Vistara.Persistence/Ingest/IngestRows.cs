using Vistara.Persistence.Model;

namespace Vistara.Persistence.Ingest;

public sealed class IngestOperationRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid UploadSessionId { get; set; }
    public long FencedUploadVersion { get; set; }
    public string State { get; set; } = string.Empty;
    public string? PromotionMode { get; set; }
    public string? CanonicalKey { get; set; }
    public string? StorageProvider { get; set; }
    public string? VerifiedSha256 { get; set; }
    public long? VerifiedSizeBytes { get; set; }
    public string? DetectedFormat { get; set; }
    public string? DetectedContentType { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? RevisionId { get; set; }
    public Guid? BlobId { get; set; }
    public bool? BlobReused { get; set; }
    public string? RejectionCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public DateTimeOffset? CleanupCompletedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class AuditEventRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public string ActorKind { get; set; } = string.Empty;
    public string ActorIdentifier { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceIdentifier { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    public string Outcome { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}
