using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Vistara.Persistence.Model;

public readonly record struct TenantKey
{
    public TenantKey(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Persisted tenant IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static implicit operator TenantKey(Guid value) => new(value);

    public static implicit operator Guid(TenantKey value) => value.Value;
}

public sealed class TenantKeyValueConverter()
    : ValueConverter<TenantKey, Guid>(
        tenantId => tenantId.Value,
        value => new TenantKey(value));
