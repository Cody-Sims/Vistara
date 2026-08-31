using Vistara.Domain.Common;

namespace Vistara.Api.Features.Admin;

public sealed record StorageBucketView(
    string Id,
    string Kind,
    string Status,
    long UsedBytes,
    long QuotaBytes,
    long ObjectCount,
    DateTimeOffset LastCheckedAt,
    string? Message);

public sealed record StorageSummaryView(
    IReadOnlyList<StorageBucketView> Buckets,
    long OriginalBytes,
    long DerivativeBytes,
    long StagingBytes,
    long QuotaBytes,
    long PendingUploadBytes);

public sealed record TenantPolicyView(
    int TrashRetentionDays,
    int PurgeGraceDays,
    bool PublicLinksEnabled,
    int MaxLinkLifetimeDays,
    bool RequirePasswordForPublicLinks,
    long StorageBytes,
    long DailyTransformPixels,
    long ConcurrentUploads,
    long Version);

public sealed record TenantPolicyPatch(
    int? TrashRetentionDays,
    int? PurgeGraceDays,
    bool? PublicLinksEnabled,
    int? MaxLinkLifetimeDays,
    bool? RequirePasswordForPublicLinks,
    long? StorageBytes,
    long? DailyTransformPixels,
    long? ConcurrentUploads);

public sealed record AuditEventView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string ActorKind,
    string ActorId,
    string Action,
    string Outcome,
    string ResourceType,
    string ResourceId);

public sealed record AuditQuery(
    Guid TenantId,
    string? Action,
    string? Outcome,
    int Limit,
    DateTimeOffset? AfterOccurredAtUtc,
    Guid? AfterId);

public sealed record AuditPage(
    IReadOnlyList<AuditEventView> Items,
    DateTimeOffset? NextOccurredAtUtc,
    Guid? NextId);

/// <summary>
/// Tenant administration reads and policy writes for the operator screens.
/// Storage answers describe consumption and health only; provider topology is
/// never exposed.
/// </summary>
public interface IAdminPort
{
    ValueTask<StorageSummaryView> GetStorageAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    ValueTask<Result<TenantPolicyView>> GetPolicyAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    ValueTask<Result<TenantPolicyView>> UpdatePolicyAsync(
        Guid tenantId,
        Guid actorUserId,
        TenantPolicyPatch patch,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<AuditPage> ReadAuditAsync(
        AuditQuery query,
        CancellationToken cancellationToken);
}
