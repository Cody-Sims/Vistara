namespace Vistara.Application.Uploads.Quotas;

public readonly record struct QuotaLimit
{
    private QuotaLimit(long? value)
    {
        Value = value;
    }

    public static QuotaLimit Unlimited { get; } = new(null);

    public long? Value { get; }

    public bool IsUnlimited => !Value.HasValue;

    public static QuotaLimit Limited(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return new QuotaLimit(value);
    }

    internal bool Allows(long current, long requested)
    {
        if (IsUnlimited)
        {
            return true;
        }

        try
        {
            return checked(current + requested) <= Value;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

public sealed record QuotaLimits(
    QuotaLimit Uploads,
    QuotaLimit Bytes,
    QuotaLimit Objects,
    QuotaLimit Transformations,
    QuotaLimit Jobs,
    QuotaLimit BudgetUnits)
{
    public bool Allows(QuotaAmounts current, QuotaAmounts requested) =>
        Uploads.Allows(current.Uploads, requested.Uploads) &&
        Bytes.Allows(current.Bytes, requested.Bytes) &&
        Objects.Allows(current.Objects, requested.Objects) &&
        Transformations.Allows(current.Transformations, requested.Transformations) &&
        Jobs.Allows(current.Jobs, requested.Jobs) &&
        BudgetUnits.Allows(current.BudgetUnits, requested.BudgetUnits);
}
