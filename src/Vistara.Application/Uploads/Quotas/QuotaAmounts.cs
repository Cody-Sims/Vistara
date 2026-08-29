namespace Vistara.Application.Uploads.Quotas;

public readonly record struct QuotaAmounts
{
    public QuotaAmounts(
        long uploads,
        long bytes,
        long objects,
        long transformations,
        long jobs,
        long budgetUnits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uploads);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(objects);
        ArgumentOutOfRangeException.ThrowIfNegative(transformations);
        ArgumentOutOfRangeException.ThrowIfNegative(jobs);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetUnits);

        Uploads = uploads;
        Bytes = bytes;
        Objects = objects;
        Transformations = transformations;
        Jobs = jobs;
        BudgetUnits = budgetUnits;
    }

    public static QuotaAmounts Zero { get; } = new(0, 0, 0, 0, 0, 0);

    public long Uploads { get; }

    public long Bytes { get; }

    public long Objects { get; }

    public long Transformations { get; }

    public long Jobs { get; }

    public long BudgetUnits { get; }

    public QuotaAmounts AddChecked(QuotaAmounts other) =>
        new(
            checked(Uploads + other.Uploads),
            checked(Bytes + other.Bytes),
            checked(Objects + other.Objects),
            checked(Transformations + other.Transformations),
            checked(Jobs + other.Jobs),
            checked(BudgetUnits + other.BudgetUnits));

    public QuotaAmounts SubtractChecked(QuotaAmounts other) =>
        new(
            checked(Uploads - other.Uploads),
            checked(Bytes - other.Bytes),
            checked(Objects - other.Objects),
            checked(Transformations - other.Transformations),
            checked(Jobs - other.Jobs),
            checked(BudgetUnits - other.BudgetUnits));
}
