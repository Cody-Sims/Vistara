namespace Vistara.Application.Common.Storage;

/// <summary>
/// The object key prefixes a single tenant owns.
/// </summary>
/// <remarks>
/// Upload staging, promoted originals, and derivative staging are all written
/// under a tenant-sharded prefix, so a tenant sweep can enumerate exactly its
/// own objects. Published derivative representations are content addressed by
/// generation identity and are deliberately not tenant partitioned, so they are
/// never owned by a single tenant and must never be classified or deleted by a
/// per-tenant reconciliation pass.
/// </remarks>
public static class TenantBlobNamespaces
{
    public const string SharedDerivativePrefix = "derivatives/";

    public static IReadOnlyList<string> For(Guid tenantId)
    {
        EnsureUuid7(tenantId);
        string shard = tenantId.ToString("N")[..2];
        string dashed = tenantId.ToString("D");
        string compact = tenantId.ToString("N");
        return
        [
            $"originals/{shard}/{dashed}/",
            $"staging/{shard}/{dashed}/",
            $"staging/derivatives/{compact}/",
        ];
    }

    public static bool Contains(Guid tenantId, string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        return For(tenantId).Any(prefix =>
            objectKey.StartsWith(prefix, StringComparison.Ordinal) &&
            objectKey.Length > prefix.Length);
    }

    private static void EnsureUuid7(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException(
                "A UUIDv7 tenant ID is required.",
                nameof(tenantId));
        }
    }
}
