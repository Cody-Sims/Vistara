namespace Vistara.Domain.Identity;

public readonly record struct UserId
{
    public UserId(Guid value)
    {
        IdentityIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct LocalIdentityId
{
    public LocalIdentityId(Guid value)
    {
        IdentityIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct ExternalIdentityId
{
    public ExternalIdentityId(Guid value)
    {
        IdentityIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct AuthSessionId
{
    public AuthSessionId(Guid value)
    {
        IdentityIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct ApiKeyId
{
    public ApiKeyId(Guid value)
    {
        IdentityIdGuard.EnsureUuid7(value, nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

internal static class IdentityIdGuard
{
    public static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Identity IDs must be non-empty UUIDv7 values.",
                parameterName);
        }
    }
}
