using Vistara.Api.Features.Admin;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Common;
using Vistara.Persistence.Administration;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Bridges tenant administration onto the tenant-scoped administrative store,
/// the composed blob store, and the audit writer. Bucket answers describe the
/// provider kind, consumption, and reachability only; bucket names,
/// containers, endpoints, and filesystem paths never leave the deployment.
/// </summary>
internal sealed class PlatformAdminAdapter(
    RelationalAdminStore store,
    IBlobStore blobStore,
    IAuditWriter audit,
    IUuid7Generator ids,
    IClock clock) : IAdminPort
{
    private const string HealthProbeKey = "vistara/health/storage-probe.bin";

    public async ValueTask<StorageSummaryView> GetStorageAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        PersistedStorageUsage usage =
            await store.ReadStorageUsageAsync(tenantId, cancellationToken);
        PersistedTenantPolicy? policy =
            await store.ReadPolicyAsync(tenantId, cancellationToken);
        long quotaBytes = policy?.StorageBytes ?? 0;
        // A bucket quota of zero on the wire means "not limited"; the policy
        // document is the authority on whether a limit exists.
        (string status, string? message) =
            await ProbeAsync(cancellationToken);
        DateTimeOffset checkedAt = clock.UtcNow;
        string kind = DescribeKind(blobStore.Name);

        return new StorageSummaryView(
            [
                new StorageBucketView(
                    "originals",
                    kind,
                    status,
                    usage.OriginalBytes,
                    quotaBytes,
                    usage.OriginalObjects,
                    checkedAt,
                    message),
                new StorageBucketView(
                    "derivatives",
                    kind,
                    status,
                    usage.DerivativeBytes,
                    0,
                    usage.DerivativeObjects,
                    checkedAt,
                    message),
                new StorageBucketView(
                    "staging",
                    kind,
                    status,
                    usage.StagingBytes,
                    0,
                    usage.StagingObjects,
                    checkedAt,
                    message),
            ],
            usage.OriginalBytes,
            usage.DerivativeBytes,
            usage.StagingBytes,
            quotaBytes,
            usage.StagingBytes);
    }

    public async ValueTask<Result<TenantPolicyView>> GetPolicyAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        PersistedTenantPolicy? policy =
            await store.ReadPolicyAsync(tenantId, cancellationToken);
        return policy is null
            ? Result.Failure<TenantPolicyView>(NotFound)
            : Result.Success(Map(policy));
    }

    public async ValueTask<Result<TenantPolicyView>> UpdatePolicyAsync(
        Guid tenantId,
        Guid actorUserId,
        TenantPolicyPatch patch,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        PersistedTenantPolicy? current =
            await store.ReadPolicyAsync(tenantId, cancellationToken);
        if (current is null)
        {
            return Result.Failure<TenantPolicyView>(NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return Result.Failure<TenantPolicyView>(StaleVersion);
        }

        var desired = new PersistedTenantPolicy(
            patch.TrashRetentionDays ?? current.TrashRetentionDays,
            patch.PurgeGraceDays ?? current.PurgeGraceDays,
            patch.PublicLinksEnabled ?? current.PublicLinksEnabled,
            patch.MaxLinkLifetimeDays ?? current.MaxLinkLifetimeDays,
            patch.RequirePasswordForPublicLinks ?? current.RequirePasswordForPublicLinks,
            Merge(patch.StorageBytes, current.StorageBytes),
            Merge(patch.DailyTransformPixels, current.DailyTransformPixels),
            Merge(patch.ConcurrentUploads, current.ConcurrentUploads),
            current.Version);
        if (Validate(desired) is { } invalid)
        {
            return Result.Failure<TenantPolicyView>(invalid);
        }

        DateTimeOffset now = clock.UtcNow;
        TenantPolicyWriteStatus status = await store.WritePolicyAsync(
            tenantId,
            desired,
            expectedVersion,
            now,
            cancellationToken);
        if (status == TenantPolicyWriteStatus.NotFound)
        {
            return Result.Failure<TenantPolicyView>(NotFound);
        }

        if (status == TenantPolicyWriteStatus.VersionConflict)
        {
            return Result.Failure<TenantPolicyView>(StaleVersion);
        }

        Result<AuditChangeSummary> after = AuditChangeSummary.Create(
        [
            AuditField.Plain(
                "trashRetentionDays",
                desired.TrashRetentionDays.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            AuditField.Plain(
                "publicLinksEnabled",
                desired.PublicLinksEnabled ? "true" : "false"),
            AuditField.Plain("storedBytes", Describe(desired.StorageBytes)),
        ]);
        await audit.AppendAsync(
            new AuditRecord(
                new AuditEventId(ids.NewId()),
                new AuditTenantId(tenantId),
                new AuditActor(AuditActorKind.User, actorUserId.ToString("D")),
                "tenant.policy.updated",
                new AuditResource("tenant_policy", tenantId.ToString("D")),
                AuditChangeSummary.Empty,
                after.TryGetValue(out AuditChangeSummary? summary)
                    ? summary
                    : AuditChangeSummary.Empty,
                AuditOutcome.Succeeded,
                now),
            cancellationToken);
        return Result.Success(Map(desired with { Version = expectedVersion + 1 }));
    }

    public async ValueTask<AuditPage> ReadAuditAsync(
        AuditQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        PersistedAuditPage page = await store.ReadAuditAsync(
            query.TenantId,
            query.Action,
            query.Outcome,
            query.Limit,
            query.AfterOccurredAtUtc,
            query.AfterId,
            cancellationToken);
        return new AuditPage(
            page.Items
                .Select(item => new AuditEventView(
                    item.Id,
                    item.OccurredAtUtc,
                    item.ActorKind,
                    item.ActorIdentifier,
                    item.Action,
                    item.Outcome,
                    item.ResourceType,
                    item.ResourceIdentifier))
                .ToArray(),
            page.NextOccurredAtUtc,
            page.NextId);
    }

    internal static ResultError? Validate(PersistedTenantPolicy policy)
    {
        if (policy.TrashRetentionDays is < 1 or > 3_650 ||
            policy.PurgeGraceDays is < 0 or > 3_650 ||
            policy.MaxLinkLifetimeDays is < 1 or > 3_650)
        {
            return ResultError.Validation(
                "policies.invalid_duration",
                "Retention and link lifetimes must be between one and 3650 days.");
        }

        if (policy.StorageBytes is < 0 ||
            policy.DailyTransformPixels is < 0 ||
            policy.ConcurrentUploads is < 0 or > 1_000)
        {
            return ResultError.Validation(
                "policies.invalid_quota",
                "Quota values must be zero or greater, with at most 1000 concurrent uploads.");
        }

        return null;
    }

    private static long? Merge(Api.Features.Account.PatchValue<long?> patch, long? current) =>
        patch.IsPresent ? patch.Value : current;

    private static string Describe(long? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ??
        "unlimited";

    internal static string DescribeKind(string provider) =>
        provider switch
        {
            "aws-s3" => "s3",
            "azure" => "azure",
            "local" => "filesystem",
            _ => "unknown",
        };

    private async ValueTask<(string Status, string? Message)> ProbeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await blobStore.HeadAsync(new BlobKey(HealthProbeKey), cancellationToken);
            return ("healthy", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The provider message can carry endpoints and credentials, so the
            // caller only learns the failure kind.
            return ("unavailable", failure.GetType().Name);
        }
    }

    private static TenantPolicyView Map(PersistedTenantPolicy policy) =>
        new(
            policy.TrashRetentionDays,
            policy.PurgeGraceDays,
            policy.PublicLinksEnabled,
            policy.MaxLinkLifetimeDays,
            policy.RequirePasswordForPublicLinks,
            policy.StorageBytes,
            policy.DailyTransformPixels,
            policy.ConcurrentUploads,
            policy.Version);

    private static ResultError NotFound => ResultError.NotFound(
        "policies.not_found",
        "The requested tenant was not found.");

    private static ResultError StaleVersion => ResultError.Conflict(
        "policies.version_conflict",
        "The policy document changed since it was read.");
}
