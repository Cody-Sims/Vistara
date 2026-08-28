namespace Vistara.Domain.Jobs;

public enum JobState
{
    Pending,
    Leased,
    RetryScheduled,
    Completed,
    DeadLettered,
}
