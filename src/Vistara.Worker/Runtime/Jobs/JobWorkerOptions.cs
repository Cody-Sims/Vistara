namespace Vistara.Worker.Runtime.Jobs;

public sealed class JobWorkerOptions
{
    public int MaximumConcurrency { get; set; } = 1;
    public int ClaimBatchSize { get; set; } = 1;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    internal void Validate()
    {
        if (MaximumConcurrency < 1 || ClaimBatchSize < 1)
        {
            throw new InvalidOperationException(
                "Job concurrency and claim batch size must be positive.");
        }

        EnsurePositive(LeaseDuration, nameof(LeaseDuration));
        EnsurePositive(HeartbeatInterval, nameof(HeartbeatInterval));
        EnsurePositive(PollInterval, nameof(PollInterval));
        EnsurePositive(DrainTimeout, nameof(DrainTimeout));
        EnsurePositive(JobTimeout, nameof(JobTimeout));
        _ = new Vistara.Domain.Jobs.JobRetryPolicy(InitialRetryDelay, MaximumRetryDelay);
        if (HeartbeatInterval >= LeaseDuration)
        {
            throw new InvalidOperationException(
                "The heartbeat interval must be shorter than the lease duration.");
        }
    }

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{name} must be positive.");
        }
    }
}
