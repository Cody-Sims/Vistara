namespace Vistara.Persistence.Jobs;

public sealed class JobQueueOptions
{
    public int ConfiguredWorkerCount { get; set; } = 1;
}
