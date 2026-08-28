namespace Vistara.Domain.Jobs;

public readonly record struct JobId
{
    public JobId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Job IDs must be non-empty UUIDv7 values.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct JobTenantId
{
    public JobTenantId(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Tenant IDs must be non-empty UUIDv7 values.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct JobVersion
{
    public JobVersion(long value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Job version must be positive.");
        }

        Value = value;
    }

    public long Value { get; }

    public JobVersion Next() => new(checked(Value + 1));
}

public readonly record struct JobType
{
    public const int MaximumLength = 128;

    public JobType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"Job type cannot exceed {MaximumLength} characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct JobDedupeKey
{
    public const int MaximumLength = 256;

    public JobDedupeKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"Job dedupe key cannot exceed {MaximumLength} characters.",
                nameof(value));
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Job dedupe key cannot have leading or trailing whitespace.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct JobDedupeIdentity(
    JobTenantId TenantId,
    JobDedupeKey Key);

public readonly record struct JobLeaseOwner
{
    public const int MaximumLength = 128;

    public JobLeaseOwner(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"Lease owner cannot exceed {MaximumLength} characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
