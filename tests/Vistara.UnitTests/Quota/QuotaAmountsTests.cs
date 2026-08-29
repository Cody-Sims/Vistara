using Vistara.Application.Uploads.Quotas;

namespace Vistara.UnitTests.Quota;

public sealed class QuotaAmountsTests
{
    [Fact]
    public void Addition_is_checked_and_preserves_every_dimension()
    {
        QuotaAmounts left = new(1, 2, 3, 4, 5, 6);
        QuotaAmounts right = new(6, 5, 4, 3, 2, 1);

        QuotaAmounts total = left.AddChecked(right);

        Assert.Equal(new QuotaAmounts(7, 7, 7, 7, 7, 7), total);
        Assert.Throws<OverflowException>(
            () => new QuotaAmounts(long.MaxValue, 0, 0, 0, 0, 0)
                .AddChecked(new QuotaAmounts(1, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Amounts_reject_negative_values_but_accept_zero()
    {
        Assert.Equal(QuotaAmounts.Zero, new QuotaAmounts(0, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new QuotaAmounts(0, -1, 0, 0, 0, 0));
    }

    [Fact]
    public void Limits_support_zero_and_unlimited_dimensions()
    {
        QuotaLimits limits = new(
            Uploads: QuotaLimit.Limited(0),
            Bytes: QuotaLimit.Unlimited,
            Objects: QuotaLimit.Limited(1),
            Transformations: QuotaLimit.Unlimited,
            Jobs: QuotaLimit.Limited(0),
            BudgetUnits: QuotaLimit.Unlimited);

        Assert.False(limits.Allows(
            QuotaAmounts.Zero,
            new QuotaAmounts(1, 0, 0, 0, 0, 0)));
        Assert.True(limits.Allows(
            QuotaAmounts.Zero,
            new QuotaAmounts(0, long.MaxValue, 1, long.MaxValue, 0, long.MaxValue)));
    }
}
