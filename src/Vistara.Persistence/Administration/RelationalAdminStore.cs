using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Administration;

public sealed record PersistedStorageUsage(
    long OriginalBytes,
    long OriginalObjects,
    long DerivativeBytes,
    long DerivativeObjects,
    long StagingBytes,
    long StagingObjects);

public sealed record PersistedTenantPolicy(
    int TrashRetentionDays,
    int PurgeGraceDays,
    bool PublicLinksEnabled,
    int MaxLinkLifetimeDays,
    bool RequirePasswordForPublicLinks,
    long? StorageBytes,
    long? DailyTransformPixels,
    long? ConcurrentUploads,
    long Version);

public sealed record PersistedAuditEvent(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string ActorKind,
    string ActorIdentifier,
    string Action,
    string Outcome,
    string ResourceType,
    string ResourceIdentifier);

public sealed record PersistedAuditPage(
    IReadOnlyList<PersistedAuditEvent> Items,
    DateTimeOffset? NextOccurredAtUtc,
    Guid? NextId);

public enum TenantPolicyWriteStatus
{
    Applied,
    VersionConflict,
    NotFound,
}

/// <summary>
/// Tenant-scoped administrative reads and policy writes. Everything is bound
/// to the active tenant scope, and no provider topology leaves this store.
/// </summary>
public sealed class RelationalAdminStore(VistaraDbContext context)
{
    /// <summary>Default trash retention when a tenant has stored no policy.</summary>
    public const int DefaultTrashRetentionDays = 30;

    public const int DefaultPurgeGraceDays = 7;

    public const int DefaultMaxLinkLifetimeDays = 30;

    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<PersistedStorageUsage> ReadStorageUsageAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        RequireScope(tenantId);
        TenantKey key = tenantId;

        // Classification runs as a correlated EXISTS in the database. Nothing
        // proportional to the tenant's object count is ever materialized.
        IQueryable<BlobRow> blobs = _context.Blobs
            .AsNoTracking()
            .Where(row => row.TenantId == key);
        var originals = await blobs
            .Where(row => _context.AssetRevisions
                .Any(revision =>
                    revision.TenantId == key && revision.BlobId == row.Id))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Bytes = group.Sum(row => row.SizeBytes),
                Count = group.LongCount(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var others = await blobs
            .Where(row => !_context.AssetRevisions
                .Any(revision =>
                    revision.TenantId == key && revision.BlobId == row.Id))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Bytes = group.Sum(row => row.SizeBytes),
                Count = group.LongCount(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        QuotaUsageRow? usage = await _context.QuotaUsage
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == key, cancellationToken);

        return new PersistedStorageUsage(
            originals?.Bytes ?? 0,
            originals?.Count ?? 0,
            others?.Bytes ?? 0,
            others?.Count ?? 0,
            usage?.ReservedBytes ?? 0,
            usage?.ReservedObjects ?? 0);
    }

    public async ValueTask<PersistedTenantPolicy?> ReadPolicyAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        RequireScope(tenantId);
        TenantKey key = tenantId;
        TenantRow? tenant = await _context.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == key, cancellationToken);
        return tenant is null ? null : Read(tenant);
    }

    public async ValueTask<TenantPolicyWriteStatus> WritePolicyAsync(
        Guid tenantId,
        PersistedTenantPolicy desired,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        RequireScope(tenantId);
        TenantKey key = tenantId;
        TenantRow? tenant = await _context.Tenants
            .SingleOrDefaultAsync(row => row.Id == key, cancellationToken);
        if (tenant is null)
        {
            return TenantPolicyWriteStatus.NotFound;
        }

        if (tenant.Version != expectedVersion)
        {
            return TenantPolicyWriteStatus.VersionConflict;
        }

        // Unknown members are preserved so an operator-set key this release
        // does not model is never silently dropped.
        JsonObject settings = Parse(tenant.SettingsJson);
        JsonObject retention = Child(settings, "retention");
        retention["trashRetentionDays"] = desired.TrashRetentionDays;
        retention["purgeGraceDays"] = desired.PurgeGraceDays;
        JsonObject sharing = Child(settings, "sharing");
        sharing["publicLinksEnabled"] = desired.PublicLinksEnabled;
        sharing["maxLinkLifetimeDays"] = desired.MaxLinkLifetimeDays;
        sharing["requirePasswordForPublicLinks"] = desired.RequirePasswordForPublicLinks;

        JsonObject quotas = Parse(tenant.QuotasJson);
        // An absent quota means unlimited. Writing zero would tell the
        // reservation path that nothing is allowed, so a cleared quota removes
        // the member instead of synthesizing a limit.
        Assign(quotas, "storedBytes", desired.StorageBytes);
        Assign(quotas, "transformations", desired.DailyTransformPixels);
        Assign(quotas, "concurrentUploads", desired.ConcurrentUploads);

        tenant.SettingsJson = settings.ToJsonString();
        tenant.QuotasJson = quotas.ToJsonString();
        tenant.UpdatedAtUtc = updatedAtUtc;
        tenant.Version = checked(tenant.Version + 1);
        _context.Entry(tenant).Property(entry => entry.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return TenantPolicyWriteStatus.Applied;
        }
        catch (DbUpdateException)
        {
            return TenantPolicyWriteStatus.VersionConflict;
        }
    }

    public async ValueTask<PersistedAuditPage> ReadAuditAsync(
        Guid tenantId,
        string? action,
        string? outcome,
        int limit,
        DateTimeOffset? afterOccurredAtUtc,
        Guid? afterId,
        CancellationToken cancellationToken)
    {
        RequireScope(tenantId);
        TenantKey key = tenantId;
        IQueryable<AuditEventRow> rows = _context.AuditEvents
            .AsNoTracking()
            .Where(row => row.TenantId == key);
        if (!string.IsNullOrWhiteSpace(action))
        {
            rows = rows.Where(row => row.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            rows = rows.Where(row => row.Outcome == outcome);
        }

        if (afterOccurredAtUtc is { } after && afterId is { } cursorId)
        {
            rows = rows.Where(row =>
                row.OccurredAtUtc < after ||
                (row.OccurredAtUtc == after && row.Id.CompareTo(cursorId) < 0));
        }

        AuditEventRow[] page = await rows
            .OrderByDescending(row => row.OccurredAtUtc)
            .ThenByDescending(row => row.Id)
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        bool hasMore = page.Length > limit;
        AuditEventRow[] window = hasMore ? page[..limit] : page;
        AuditEventRow? last = window.Length == 0 ? null : window[^1];
        return new PersistedAuditPage(
            window
                .Select(row => new PersistedAuditEvent(
                    row.Id,
                    row.OccurredAtUtc,
                    row.ActorKind,
                    row.ActorIdentifier,
                    row.Action,
                    row.Outcome,
                    row.ResourceType,
                    row.ResourceIdentifier))
                .ToArray(),
            hasMore ? last?.OccurredAtUtc : null,
            hasMore ? last?.Id : null);
    }

    internal static PersistedTenantPolicy Read(TenantRow tenant)
    {
        JsonObject settings = Parse(tenant.SettingsJson);
        JsonObject retention = Child(settings, "retention");
        JsonObject sharing = Child(settings, "sharing");
        JsonObject quotas = Parse(tenant.QuotasJson);
        return new PersistedTenantPolicy(
            (int)ReadLong(retention, "trashRetentionDays", DefaultTrashRetentionDays),
            (int)ReadLong(retention, "purgeGraceDays", DefaultPurgeGraceDays),
            ReadBool(sharing, "publicLinksEnabled", true),
            (int)ReadLong(sharing, "maxLinkLifetimeDays", DefaultMaxLinkLifetimeDays),
            ReadBool(sharing, "requirePasswordForPublicLinks", false),
            ReadOptionalLong(quotas, "storedBytes"),
            ReadOptionalLong(quotas, "transformations"),
            ReadOptionalLong(quotas, "concurrentUploads"),
            tenant.Version);
    }

    private static JsonObject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static void Assign(JsonObject target, string name, long? value)
    {
        if (value is { } assigned)
        {
            target[name] = assigned;
            return;
        }

        target.Remove(name);
    }

    private static long? ReadOptionalLong(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue(out long parsed) && parsed >= 0
            ? parsed
            : null;

    private static long ReadLong(JsonObject source, string name, long fallback) =>
        source[name] is JsonValue value && value.TryGetValue(out long parsed) && parsed >= 0
            ? parsed
            : fallback;

    private static bool ReadBool(JsonObject source, string name, bool fallback) =>
        source[name] is JsonValue value && value.TryGetValue(out bool parsed)
            ? parsed
            : fallback;

    private void RequireScope(Guid tenantId)
    {
        if (_context.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                "The administrative request does not match the active tenant scope.");
        }
    }
}
