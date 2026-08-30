using System.Text.Json;
using System.Text.Json.Serialization;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Lifecycle;

public sealed class LifecycleRestoreJobPayload
{
    [JsonConstructor]
    public LifecycleRestoreJobPayload(
        Guid tenantId,
        Guid actorId,
        IReadOnlyList<LifecycleAssetTarget> targets)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(targets));
        }

        TenantId = tenantId;
        ActorId = actorId;
        Targets = targets.ToArray();
    }

    public Guid TenantId { get; }

    public Guid ActorId { get; }

    public IReadOnlyList<LifecycleAssetTarget> Targets { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Lifecycle job identifiers must be UUIDv7 values.",
                parameterName);
        }
    }
}

public sealed class LifecyclePurgeJobPayload
{
    [JsonConstructor]
    public LifecyclePurgeJobPayload(Guid tenantId, Guid batchId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(batchId, nameof(batchId));
        TenantId = tenantId;
        BatchId = batchId;
    }

    public Guid TenantId { get; }

    public Guid BatchId { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Lifecycle job identifiers must be UUIDv7 values.",
                parameterName);
        }
    }
}

public static class LifecycleJobContracts
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public const int PayloadVersion = 1;

    public static JobType RestoreType { get; } = new("lifecycle.restore");

    public static JobType PurgeType { get; } = new("lifecycle.purge");

    public static string SerializeRestore(LifecycleRestoreJobPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static bool TryParseRestore(
        JobType type,
        int payloadVersion,
        string json,
        out LifecycleRestoreJobPayload? payload)
    {
        payload = null;
        if (type != RestoreType || payloadVersion != PayloadVersion)
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<LifecycleRestoreJobPayload>(
                json,
                JsonOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string SerializePurge(LifecyclePurgeJobPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static bool TryParsePurge(
        JobType type,
        int payloadVersion,
        string json,
        out LifecyclePurgeJobPayload? payload)
    {
        payload = null;
        if (type != PurgeType || payloadVersion != PayloadVersion)
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<LifecyclePurgeJobPayload>(
                json,
                JsonOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
