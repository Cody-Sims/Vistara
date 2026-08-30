using Vistara.Domain.Jobs;

namespace Vistara.Worker.Runtime.Jobs;

public interface IJobRuntimeObserver
{
    void Claimed(int count);
    void Started(JobId jobId, JobType jobType);
    void Heartbeat(JobId jobId);
    void Completed(JobId jobId);
    void Failed(JobId jobId, string failureCode, bool deadLettered);
    void LeaseLost(JobId jobId, string errorCode);
}

public sealed class NullJobRuntimeObserver : IJobRuntimeObserver
{
    private NullJobRuntimeObserver()
    {
    }

    public static NullJobRuntimeObserver Instance { get; } = new();

    public void Claimed(int count)
    {
    }

    public void Started(JobId jobId, JobType jobType)
    {
    }

    public void Heartbeat(JobId jobId)
    {
    }

    public void Completed(JobId jobId)
    {
    }

    public void Failed(JobId jobId, string failureCode, bool deadLettered)
    {
    }

    public void LeaseLost(JobId jobId, string errorCode)
    {
    }
}
