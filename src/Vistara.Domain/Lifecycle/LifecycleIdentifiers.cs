namespace Vistara.Domain.Lifecycle;

public readonly record struct LifecycleTenantId
{
    public LifecycleTenantId(Guid value)
    {
        LifecycleIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct LifecycleUserId
{
    public LifecycleUserId(Guid value)
    {
        LifecycleIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct LifecycleAssetId
{
    public LifecycleAssetId(Guid value)
    {
        LifecycleIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct RetentionHoldId
{
    public RetentionHoldId(Guid value)
    {
        LifecycleIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct PurgeBatchId
{
    public PurgeBatchId(Guid value)
    {
        LifecycleIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

internal static class LifecycleIdGuard
{
    public static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Lifecycle IDs must be non-empty UUIDv7 values.",
                parameterName);
        }
    }
}
