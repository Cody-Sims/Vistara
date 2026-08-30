namespace Vistara.PerformanceTests;

internal static class TestIds
{
    internal static readonly Guid Tenant =
        Guid.Parse("01991f9e-522b-7c80-a109-7f764ae57985");

    internal static readonly Guid Actor =
        Guid.Parse("01991f9e-522b-7c80-a109-7f764ae57986");

    internal static Guid Create(long value)
    {
        if (value is < 0 or > 0xFFFFFFFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return Guid.ParseExact(
            $"01991f9e522b7c80a109{value:x12}",
            "N");
    }
}
