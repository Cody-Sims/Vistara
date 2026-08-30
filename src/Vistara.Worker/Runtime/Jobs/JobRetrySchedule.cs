using Vistara.Domain.Jobs;

namespace Vistara.Worker.Runtime.Jobs;

public interface IJobRandomSource
{
    double NextDouble();
}

public sealed class SystemJobRandomSource : IJobRandomSource
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class SequenceJobRandomSource(params double[] values) : IJobRandomSource
{
    private readonly double[] _values = values.Length == 0 ? [0.5] : values;
    private int _index;

    public double NextDouble()
    {
        int index = (Interlocked.Increment(ref _index) - 1) % _values.Length;
        double value = _values[index];
        return value;
    }
}

public static class JobRetrySchedule
{
    public static TimeSpan GetDelay(
        JobRetryPolicy policy,
        int failedAttempt,
        IJobRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(random);
        double sample = random.NextDouble();
        if (!double.IsFinite(sample) || sample < 0 || sample > 1)
        {
            throw new InvalidOperationException("The job random source returned an invalid sample.");
        }

        TimeSpan exponential = policy.GetDelay(failedAttempt);
        double factor = 0.5 + sample;
        long jitteredTicks = checked((long)Math.Round(
            exponential.Ticks * factor,
            MidpointRounding.AwayFromZero));
        return TimeSpan.FromTicks(Math.Min(jitteredTicks, policy.MaximumDelay.Ticks));
    }

    internal static JobRetryPolicy CreatePolicy(
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        int failedAttempt,
        IJobRandomSource random)
    {
        var basePolicy = new JobRetryPolicy(initialDelay, maximumDelay);
        TimeSpan target = GetDelay(basePolicy, failedAttempt, random);
        long initialTicks = target.Ticks;
        for (int attempt = 1; attempt < failedAttempt; attempt++)
        {
            initialTicks = Math.Max(1, initialTicks / 2);
        }

        return new JobRetryPolicy(
            TimeSpan.FromTicks(initialTicks),
            TimeSpan.FromTicks(Math.Max(initialTicks, target.Ticks)));
    }
}
