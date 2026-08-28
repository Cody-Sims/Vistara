using Vistara.Application.Common;

namespace Vistara.UnitTests.Common;

public sealed class ClockTests
{
    [Fact]
    public void System_clock_returns_a_utc_timestamp()
    {
        DateTimeOffset timestamp = SystemClock.Instance.UtcNow;

        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
    }
}
