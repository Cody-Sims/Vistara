using System.Text.Json.Serialization;

namespace Vistara.Contracts.Admin;

public sealed record StorageBucketResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("usedBytes")] long UsedBytes,
    [property: JsonPropertyName("quotaBytes")] long QuotaBytes,
    [property: JsonPropertyName("objectCount")] long ObjectCount,
    [property: JsonPropertyName("lastCheckedAt")] DateTimeOffset LastCheckedAt,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// Consumption and health for the tenant. Provider topology such as bucket
/// names, containers, endpoints, and filesystem paths is never included.
/// </summary>
public sealed record StorageSummaryResponse(
    [property: JsonPropertyName("buckets")] IReadOnlyList<StorageBucketResponse> Buckets,
    [property: JsonPropertyName("originalBytes")] long OriginalBytes,
    [property: JsonPropertyName("derivativeBytes")] long DerivativeBytes,
    [property: JsonPropertyName("stagingBytes")] long StagingBytes,
    [property: JsonPropertyName("quotaBytes")] long QuotaBytes,
    [property: JsonPropertyName("pendingUploadBytes")] long PendingUploadBytes);

public sealed record RetentionPolicyResponse(
    [property: JsonPropertyName("trashRetentionDays")] int TrashRetentionDays,
    [property: JsonPropertyName("purgeGraceDays")] int PurgeGraceDays);

public sealed record SharingPolicyResponse(
    [property: JsonPropertyName("publicLinksEnabled")] bool PublicLinksEnabled,
    [property: JsonPropertyName("maxLinkLifetimeDays")] int MaxLinkLifetimeDays,
    [property: JsonPropertyName("requirePasswordForPublicLinks")] bool RequirePasswordForPublicLinks);

public sealed record QuotaPolicyResponse(
    [property: JsonPropertyName("storageBytes")] long StorageBytes,
    [property: JsonPropertyName("dailyTransformPixels")] long DailyTransformPixels,
    [property: JsonPropertyName("concurrentUploads")] long ConcurrentUploads);

public sealed record TenantPolicyResponse(
    [property: JsonPropertyName("retention")] RetentionPolicyResponse Retention,
    [property: JsonPropertyName("sharing")] SharingPolicyResponse Sharing,
    [property: JsonPropertyName("quotas")] QuotaPolicyResponse Quotas,
    [property: JsonPropertyName("version")] long Version);

public sealed record AuditActorResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayName);

/// <summary>
/// A redacted audit entry. Field-level before and after summaries stay in the
/// store and are never published, because they can contain user content.
/// </summary>
public sealed record AuditEventResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("actor")] AuditActorResponse Actor,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("resourceType")] string ResourceType,
    [property: JsonPropertyName("resourceId")] string ResourceId);

public sealed record AuditCollectionResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<AuditEventResponse> Items,
    [property: JsonPropertyName("nextCursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextCursor);

public sealed record UpdateTenantPolicyRequest(
    [property: JsonPropertyName("retention")] RetentionPolicyPatch? Retention,
    [property: JsonPropertyName("sharing")] SharingPolicyPatch? Sharing,
    [property: JsonPropertyName("quotas")] QuotaPolicyPatch? Quotas);

public sealed record RetentionPolicyPatch(
    [property: JsonPropertyName("trashRetentionDays")] int? TrashRetentionDays,
    [property: JsonPropertyName("purgeGraceDays")] int? PurgeGraceDays);

public sealed record SharingPolicyPatch(
    [property: JsonPropertyName("publicLinksEnabled")] bool? PublicLinksEnabled,
    [property: JsonPropertyName("maxLinkLifetimeDays")] int? MaxLinkLifetimeDays,
    [property: JsonPropertyName("requirePasswordForPublicLinks")] bool? RequirePasswordForPublicLinks);

public sealed record QuotaPolicyPatch(
    [property: JsonPropertyName("storageBytes")] long? StorageBytes,
    [property: JsonPropertyName("dailyTransformPixels")] long? DailyTransformPixels,
    [property: JsonPropertyName("concurrentUploads")] long? ConcurrentUploads);
