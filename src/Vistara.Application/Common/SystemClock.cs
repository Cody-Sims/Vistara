namespace Vistara.Application.Common;

public sealed class SystemClock : IClock
{
    private SystemClock()
    {
    }

    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
