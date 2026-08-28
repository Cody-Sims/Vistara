namespace Vistara.Domain.Tenancy;

public readonly record struct TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Tenant IDs must be non-empty UUIDv7 values.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}
