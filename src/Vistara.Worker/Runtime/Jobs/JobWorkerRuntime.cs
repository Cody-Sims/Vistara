using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Worker.Runtime.Jobs;

public sealed class JobWorkerRuntime
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly IJobRandomSource _random;
    private readonly IJobRuntimeObserver _observer;
    private readonly IJobFailureClassifier _failureClassifier;
    private readonly JobWorkerOptions _options;
    private readonly JobLeaseOwner _owner;

    public JobWorkerRuntime(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        IJobRandomSource random,
        IJobRuntimeObserver observer,
        JobWorkerOptions options,
        JobLeaseOwner owner,
        IJobFailureClassifier? failureClassifier = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _owner = owner;
        _failureClassifier = failureClassifier ?? new SafeJobFailureClassifier();
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var forceStop = new CancellationTokenSource();
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<JobLeaseAssignment> assignments =
                await ClaimAsync(cancellationToken);
            if (assignments.Count == 0)
            {
                return;
            }

            await Task.WhenAll(assignments.Select(
                assignment => ProcessSafelyAsync(assignment, forceStop.Token)));
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var forceStop = new CancellationTokenSource();
        var active = new HashSet<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ObserveCompletedAsync(active);
                int capacity = _options.MaximumConcurrency - active.Count;
                if (capacity > 0)
                {
                    IReadOnlyList<JobLeaseAssignment> assignments =
                        await ClaimAsync(stoppingToken, capacity);
                    foreach (JobLeaseAssignment assignment in assignments)
                    {
                        active.Add(ProcessSafelyAsync(assignment, forceStop.Token));
                    }

                    if (assignments.Count > 0)
                    {
                        continue;
                    }
                }

                if (active.Count > 0)
                {
                    Task<Task> processing = Task.WhenAny(active);
                    Task delay = Task.Delay(_options.PollInterval, stoppingToken);
                    Task completed = await Task.WhenAny(processing, delay);
                    await ObserveCancellationAsync(completed, stoppingToken);
                    if (completed == processing)
                    {
                        Task processingTask = await processing;
                        await ObserveTaskAsync(processingTask);
                        _ = active.Remove(processingTask);
                    }
                }
                else
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        if (active.Count == 0)
        {
            return;
        }

        Task drain = Task.WhenAll(active);
        Task deadline = Task.Delay(_options.DrainTimeout, CancellationToken.None);
        if (await Task.WhenAny(drain, deadline) != drain)
        {
            forceStop.Cancel();
        }

        await ObserveTaskAsync(drain);
    }

    private async Task<IReadOnlyList<JobLeaseAssignment>> ClaimAsync(
        CancellationToken cancellationToken,
        int? capacity = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IJobQueue queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        int maximum = Math.Min(
            capacity ?? _options.MaximumConcurrency,
            Math.Min(_options.MaximumConcurrency, _options.ClaimBatchSize));
        Result<IReadOnlyList<JobLeaseAssignment>> result = await queue.LeaseAsync(
            new JobLeaseRequest(
                _owner,
                _clock.UtcNow,
                _options.LeaseDuration,
                maximum),
            cancellationToken);
        if (!result.TryGetValue(out IReadOnlyList<JobLeaseAssignment>? assignments))
        {
            return [];
        }

        _observer.Claimed(assignments.Count);
        return assignments;
    }

    private async Task ProcessAsync(
        JobLeaseAssignment assignment,
        CancellationToken forceStop)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IJobQueue queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        IJobHandler? handler = scope.ServiceProvider
            .GetServices<IJobHandler>()
            .SingleOrDefault(candidate =>
                candidate.JobType.Value == assignment.Job.Type.Value);
        var leaseState = new LeaseState(assignment.Lease);
        using var heartbeatStop = new CancellationTokenSource();
        using var handlerStop = CancellationTokenSource.CreateLinkedTokenSource(forceStop);
        handlerStop.CancelAfter(_options.JobTimeout);
        Task heartbeat = HeartbeatAsync(
            assignment.Job.Id,
            queue,
            leaseState,
            handlerStop,
            heartbeatStop.Token);
        _observer.Started(assignment.Job.Id, assignment.Job.Type);

        JobHandlerResult outcome;
        try
        {
            outcome = handler is null
                ? JobHandlerResult.Failed(
                    new JobFailure(JobFailureReason.ProcessingFailed))
                : await handler.HandleAsync(assignment.Job, handlerStop.Token);
        }
        catch (OperationCanceledException) when (forceStop.IsCancellationRequested)
        {
            heartbeatStop.Cancel();
            await IgnoreCancellationAsync(heartbeat);
            return;
        }
        catch (OperationCanceledException)
        {
            outcome = JobHandlerResult.Failed(
                new JobFailure(JobFailureReason.ProcessingFailed));
        }
        catch (Exception exception)
        {
            outcome = JobHandlerResult.Failed(_failureClassifier.Classify(exception));
        }

        heartbeatStop.Cancel();
        await IgnoreCancellationAsync(heartbeat);
        if (forceStop.IsCancellationRequested)
        {
            return;
        }

        JobLease lease = leaseState.Current;
        if (outcome.IsSuccess)
        {
            Result completed = await queue.CompleteAsync(
                new JobCompletionRequest(
                    assignment.Job.Id,
                    lease.Owner,
                    lease.JobVersion,
                    _clock.UtcNow),
                CancellationToken.None);
            if (completed.IsSuccess)
            {
                _observer.Completed(assignment.Job.Id);
            }
            else
            {
                _observer.LeaseLost(
                    assignment.Job.Id,
                    completed.Error?.Code ?? "jobs.completion_failed");
            }

            return;
        }

        JobFailure failure = outcome.Failure!;
        JobRetryPolicy retryPolicy = JobRetrySchedule.CreatePolicy(
            _options.InitialRetryDelay,
            _options.MaximumRetryDelay,
            assignment.Job.Attempts,
            _random);
        Result failed = await queue.FailAsync(
            new JobFailureRequest(
                assignment.Job.Id,
                lease.Owner,
                lease.JobVersion,
                failure,
                _clock.UtcNow,
                retryPolicy),
            CancellationToken.None);
        if (failed.IsSuccess)
        {
            _observer.Failed(
                assignment.Job.Id,
                failure.Code,
                assignment.Job.Attempts >= assignment.Job.MaxAttempts);
        }
        else
        {
            _observer.LeaseLost(
                assignment.Job.Id,
                failed.Error?.Code ?? "jobs.failure_transition_failed");
        }
    }

    private async Task ProcessSafelyAsync(
        JobLeaseAssignment assignment,
        CancellationToken forceStop)
    {
        try
        {
            await ProcessAsync(assignment, forceStop);
        }
        catch (OperationCanceledException) when (forceStop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            try
            {
                _observer.LeaseLost(
                    assignment.Job.Id,
                    "jobs.processing_task_faulted");
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task HeartbeatAsync(
        JobId jobId,
        IJobQueue queue,
        LeaseState leaseState,
        CancellationTokenSource handlerStop,
        CancellationToken stoppingToken)
    {
        while (true)
        {
            await Task.Delay(_options.HeartbeatInterval, stoppingToken);
            JobLease lease = leaseState.Current;
            Result<JobLease> result = await queue.HeartbeatAsync(
                new JobHeartbeatRequest(
                    jobId,
                    lease.Owner,
                    lease.JobVersion,
                    _clock.UtcNow,
                    _options.LeaseDuration),
                stoppingToken);
            if (!result.TryGetValue(out JobLease? updated))
            {
                _observer.LeaseLost(
                    jobId,
                    result.Error?.Code ?? "jobs.heartbeat_failed");
                handlerStop.Cancel();
                return;
            }

            leaseState.Current = updated;
            _observer.Heartbeat(jobId);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ObserveCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveCompletedAsync(HashSet<Task> active)
    {
        Task[] completed = active.Where(task => task.IsCompleted).ToArray();
        foreach (Task task in completed)
        {
            await ObserveTaskAsync(task);
            _ = active.Remove(task);
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    private sealed class LeaseState(JobLease current)
    {
        private readonly object _gate = new();
        private JobLease _current = current;

        internal JobLease Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
            set
            {
                lock (_gate)
                {
                    _current = value;
                }
            }
        }
    }
}
