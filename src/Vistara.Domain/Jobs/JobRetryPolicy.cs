namespace Vistara.Domain.Jobs;

public sealed class JobRetryPolicy
{
    public JobRetryPolicy(TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialDelay),
                "Initial retry delay must be positive.");
        }

        if (maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                "Maximum retry delay must not be less than the initial delay.");
        }

        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
    }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public TimeSpan GetDelay(int failedAttempt)
    {
        if (failedAttempt < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failedAttempt),
                "Failed attempt must be positive.");
        }

        long delayTicks = InitialDelay.Ticks;
        for (int attempt = 1; attempt < failedAttempt; attempt++)
        {
            if (delayTicks >= MaximumDelay.Ticks)
            {
                return MaximumDelay;
            }

            delayTicks = Math.Min(
                checked(delayTicks > long.MaxValue / 2 ? long.MaxValue : delayTicks * 2),
                MaximumDelay.Ticks);
        }

        return TimeSpan.FromTicks(delayTicks);
    }
}
