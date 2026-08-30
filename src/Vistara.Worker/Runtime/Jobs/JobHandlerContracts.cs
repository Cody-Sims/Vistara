using Vistara.Domain.Jobs;

namespace Vistara.Worker.Runtime.Jobs;

public interface IJobHandler
{
    JobType JobType { get; }

    ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken);
}

public sealed class JobHandlerResult
{
    private JobHandlerResult(JobFailure? failure)
    {
        Failure = failure;
    }

    public JobFailure? Failure { get; }

    public bool IsSuccess => Failure is null;

    public static JobHandlerResult Success() => new((JobFailure?)null);

    public static JobHandlerResult Failed(JobFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(failure);
    }
}

public interface IJobFailureClassifier
{
    JobFailure Classify(Exception exception);
}

public sealed class SafeJobFailureClassifier : IJobFailureClassifier
{
    public JobFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new JobFailure(JobFailureReason.ProcessingFailed);
    }
}
