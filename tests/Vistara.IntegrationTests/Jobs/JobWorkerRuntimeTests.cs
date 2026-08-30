using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

public sealed class JobWorkerRuntimeTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task JobQueue_worker_bounds_concurrency_and_completes_jobs()
    {
        var queue = new FakeQueue(CreateAssignments(6));
        var handler = new ConcurrencyHandler();
        await using ServiceProvider services = Services(queue, handler);
        var runtime = Runtime(
            services.GetRequiredService<IServiceScopeFactory>(),
            maximumConcurrency: 2);

        await runtime.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, handler.MaximumObserved);
        Assert.Equal(6, queue.Completed.Count);
    }

    [Fact]
    public async Task JobQueue_worker_stops_claiming_then_gracefully_drains()
    {
        var queue = new FakeQueue(CreateAssignments(1));
        var handler = new BlockingHandler();
        await using ServiceProvider services = Services(queue, handler);
        var runtime = Runtime(
            services.GetRequiredService<IServiceScopeFactory>(),
            maximumConcurrency: 1);
        using var stopping = new CancellationTokenSource();

        Task run = runtime.RunAsync(stopping.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        handler.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(queue.Completed);
        Assert.Equal(1, queue.LeaseCalls);
    }

    [Fact]
    public async Task JobQueue_worker_timeout_is_safely_classified_for_retry()
    {
        var queue = new FakeQueue(CreateAssignments(1));
        await using ServiceProvider services = Services(queue, new TimeoutHandler());
        var runtime = Runtime(
            services.GetRequiredService<IServiceScopeFactory>(),
            maximumConcurrency: 1,
            jobTimeout: TimeSpan.FromMilliseconds(30));

        await runtime.RunOnceAsync(CancellationToken.None);

        JobFailureRequest failure = Assert.Single(queue.Failed);
        Assert.Equal(JobFailureReason.ProcessingFailed, failure.Failure.Reason);
    }

    [Fact]
    public async Task JobQueue_worker_heartbeat_advances_completion_fence()
    {
        var queue = new FakeQueue(CreateAssignments(1))
        {
            HeartbeatsSucceed = true,
        };
        var handler = new HeartbeatWaitingHandler(queue.HeartbeatObserved.Task);
        await using ServiceProvider services = Services(queue, handler);
        var runtime = Runtime(
            services.GetRequiredService<IServiceScopeFactory>(),
            maximumConcurrency: 1,
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        await runtime.RunOnceAsync(CancellationToken.None);

        JobCompletionRequest completed = Assert.Single(queue.Completed);
        Assert.True(completed.ExpectedVersion.Value >= 3);
        Assert.Equal(
            queue.LastHeartbeatVersion,
            completed.ExpectedVersion.Value);
    }

    [Fact]
    public async Task JobQueue_worker_forced_shutdown_leaves_lease_for_expiry_recovery()
    {
        var queue = new FakeQueue(CreateAssignments(1));
        var handler = new NeverCompletingHandler();
        await using ServiceProvider services = Services(queue, handler);
        var runtime = Runtime(
            services.GetRequiredService<IServiceScopeFactory>(),
            maximumConcurrency: 1,
            drainTimeout: TimeSpan.FromMilliseconds(20));
        using var stopping = new CancellationTokenSource();

        Task run = runtime.RunAsync(stopping.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(queue.Completed);
        Assert.Empty(queue.Failed);
    }

    [Fact]
    public async Task JobQueue_worker_observes_processing_task_fault_and_continues()
    {
        var queue = new FakeQueue(CreateAssignments(2))
        {
            CompletionExceptionsRemaining = 1,
        };
        var observer = new RecordingObserver();
        await using ServiceProvider services = Services(queue, new SuccessHandler());
        var runtime = Runtime(
            services.GetRequiredService<IServiceScopeFactory>(),
            maximumConcurrency: 1,
            observer: observer);

        await runtime.RunOnceAsync(CancellationToken.None);

        Assert.Single(queue.Completed);
        Assert.Equal(2, queue.CompletionAttempts);
        Assert.Contains(
            observer.LeaseLosses,
            loss => loss.ErrorCode == "jobs.processing_task_faulted");
    }

    [Fact]
    public async Task JobQueue_worker_establishes_reconciliation_tenant_in_fresh_scopes()
    {
        Guid tenantId = Guid.CreateVersion7();
        var queue = new FakeQueue(
        [
            CreateAssignment(tenantId, "upload.reconcile", "reconciliation"),
        ]);
        ServiceCollection services = RuntimeServices(queue);
        services.AddScoped<IJobHandler, TenantAwareReconciliationHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var runtime = Runtime(
            provider.GetRequiredService<IServiceScopeFactory>(),
            maximumConcurrency: 1);

        await runtime.RunOnceAsync(CancellationToken.None);

        RuntimeScopeTracker tracker =
            provider.GetRequiredService<RuntimeScopeTracker>();
        Assert.Single(queue.Completed);
        Assert.Equal([tenantId], tracker.HandledTenants);
        Assert.Equal(3, tracker.EstablishedScopeCount);
        Assert.All(
            tracker.EstablishedTenants,
            established => Assert.Equal(tenantId, established));
    }

    [Fact]
    public void JobQueue_retry_jitter_is_deterministic_and_bounded()
    {
        var policy = new JobRetryPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        var random = new SequenceJobRandomSource(0.0, 1.0);

        TimeSpan low = JobRetrySchedule.GetDelay(policy, 2, random);
        TimeSpan high = JobRetrySchedule.GetDelay(policy, 3, random);

        Assert.Equal(TimeSpan.FromSeconds(10), low);
        Assert.Equal(TimeSpan.FromSeconds(30), high);
    }

    private static JobWorkerRuntime Runtime(
        IServiceScopeFactory scopes,
        int maximumConcurrency,
        TimeSpan? jobTimeout = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? drainTimeout = null,
        IJobRuntimeObserver? observer = null) =>
        new(
            scopes,
            new FixedClock(UtcNow),
            new SequenceJobRandomSource(0.5),
            observer ?? NullJobRuntimeObserver.Instance,
            new JobWorkerOptions
            {
                MaximumConcurrency = maximumConcurrency,
                ClaimBatchSize = 16,
                LeaseDuration = TimeSpan.FromMinutes(1),
                HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(10),
                PollInterval = TimeSpan.FromMilliseconds(5),
                DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(2),
                JobTimeout = jobTimeout ?? TimeSpan.FromSeconds(2),
                InitialRetryDelay = TimeSpan.FromSeconds(1),
                MaximumRetryDelay = TimeSpan.FromMinutes(1),
            },
            new JobLeaseOwner("test-worker"));

    private static ServiceProvider Services(FakeQueue queue, IJobHandler handler)
    {
        ServiceCollection services = RuntimeServices(queue);
        services.AddSingleton(handler);
        return services.BuildServiceProvider();
    }

    private static ServiceCollection RuntimeServices(FakeQueue queue)
    {
        ServiceCollection services = [];
        services.AddSingleton<RuntimeScopeTracker>();
        services.AddScoped<RuntimeTenantScope>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<RuntimeTenantScope>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<RuntimeTenantScope>());
        services.AddSingleton<IWorkerTenantCatalog>(
            new FakeTenantCatalog(queue.TenantIds));
        services.AddSingleton<IJobQueue>(queue);
        return services;
    }

    private static JobLeaseAssignment[] CreateAssignments(int count)
    {
        Guid tenantId = Guid.CreateVersion7();
        return Enumerable.Range(0, count)
            .Select(index => CreateAssignment(
                tenantId,
                "test.runtime",
                $"runtime-{index}"))
            .ToArray();
    }

    private static JobLeaseAssignment CreateAssignment(
        Guid tenantId,
        string jobType,
        string dedupeKey)
    {
        DurableJob job = DurableJob.Create(
            new JobId(Guid.CreateVersion7()),
            new JobTenantId(tenantId),
            new JobType(jobType),
            "{}",
            1,
            new JobDedupeKey(dedupeKey),
            0,
            3,
            UtcNow,
            UtcNow);
        JobLease lease = Required(job.TryLease(
            new JobLeaseOwner("test-worker"),
            UtcNow,
            TimeSpan.FromMinutes(1)));
        return new JobLeaseAssignment(job, lease);
    }

    private static T Required<T>(Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Code);
        return value;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeQueue(IReadOnlyList<JobLeaseAssignment> assignments) : IJobQueue
    {
        private readonly Queue<JobLeaseAssignment> _assignments = new(assignments);
        private readonly Guid[] _tenantIds = assignments
            .Select(assignment => assignment.Job.TenantId.Value)
            .Distinct()
            .ToArray();
        private readonly object _gate = new();
        private long _lastHeartbeatVersion;

        internal int LeaseCalls { get; private set; }
        internal List<JobCompletionRequest> Completed { get; } = [];
        internal List<JobFailureRequest> Failed { get; } = [];
        internal int CompletionAttempts { get; private set; }
        internal int CompletionExceptionsRemaining { get; init; }
        internal bool HeartbeatsSucceed { get; init; }
        internal long LastHeartbeatVersion => Volatile.Read(ref _lastHeartbeatVersion);
        internal TaskCompletionSource HeartbeatObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<Guid> TenantIds => _tenantIds;

        public ValueTask<Result<JobEnqueueResult>> EnqueueAsync(
            DurableJob job,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseAsync(
            JobLeaseRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                LeaseCalls++;
                var leased = new List<JobLeaseAssignment>();
                while (leased.Count < request.MaximumCount && _assignments.TryDequeue(out var item))
                {
                    leased.Add(item);
                }

                return ValueTask.FromResult(
                    Result.Success<IReadOnlyList<JobLeaseAssignment>>(leased));
            }
        }

        public ValueTask<Result<JobLease>> HeartbeatAsync(
            JobHeartbeatRequest request,
            CancellationToken cancellationToken)
        {
            if (!HeartbeatsSucceed)
            {
                return ValueTask.FromResult(
                    Result.Failure<JobLease>(JobErrors.LeaseConflict));
            }

            var lease = new JobLease(
                request.JobId,
                request.Owner,
                UtcNow,
                UtcNow.Add(request.LeaseDuration),
                request.ExpectedVersion.Next());
            Interlocked.Exchange(ref _lastHeartbeatVersion, lease.JobVersion.Value);
            HeartbeatObserved.TrySetResult();
            return ValueTask.FromResult(Result.Success(lease));
        }

        public ValueTask<Result> CompleteAsync(
            JobCompletionRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                CompletionAttempts++;
                if (CompletionExceptionsRemaining >= CompletionAttempts)
                {
                    throw new InvalidOperationException("Injected completion failure.");
                }

                Completed.Add(request);
            }

            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> FailAsync(
            JobFailureRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Failed.Add(request);
            }

            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> RecoverExpiredAsync(
            JobExpiredLeaseRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTenantCatalog(IReadOnlyList<Guid> tenantIds) :
        IWorkerTenantCatalog
    {
        public ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(tenantIds);
        }
    }

    private sealed class RuntimeTenantScope(RuntimeScopeTracker tracker) :
        IMutableTenantScope
    {
        private readonly Guid _scopeId = Guid.NewGuid();
        private Guid? _tenantId;

        public Guid TenantId =>
            _tenantId ??
            throw new InvalidOperationException(
                "A tenant scope must be established.");

        public void Establish(Guid tenantId)
        {
            if (_tenantId.HasValue && _tenantId.Value != tenantId)
            {
                throw new InvalidOperationException(
                    "A runtime scope cannot switch tenants.");
            }

            _tenantId = tenantId;
            tracker.RecordEstablishment(_scopeId, tenantId);
        }
    }

    private sealed class RuntimeScopeTracker
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Guid> _established = [];
        private readonly List<Guid> _handled = [];

        internal int EstablishedScopeCount
        {
            get
            {
                lock (_gate)
                {
                    return _established.Count;
                }
            }
        }

        internal Guid[] EstablishedTenants
        {
            get
            {
                lock (_gate)
                {
                    return [.. _established.Values];
                }
            }
        }

        internal Guid[] HandledTenants
        {
            get
            {
                lock (_gate)
                {
                    return [.. _handled];
                }
            }
        }

        internal void RecordEstablishment(Guid scopeId, Guid tenantId)
        {
            lock (_gate)
            {
                _established[scopeId] = tenantId;
            }
        }

        internal void RecordHandled(Guid tenantId)
        {
            lock (_gate)
            {
                _handled.Add(tenantId);
            }
        }
    }

    private sealed class TenantAwareReconciliationHandler(
        ITenantScope tenantScope,
        RuntimeScopeTracker tracker) : IJobHandler
    {
        public JobType JobType => new("upload.reconcile");

        public ValueTask<JobHandlerResult> HandleAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(job.TenantId.Value, tenantScope.TenantId);
            tracker.RecordHandled(tenantScope.TenantId);
            return ValueTask.FromResult(JobHandlerResult.Success());
        }
    }

    private sealed class ConcurrencyHandler : IJobHandler
    {
        private int _active;
        private readonly TaskCompletionSource _twoStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobType JobType => new("test.runtime");
        internal int MaximumObserved { get; private set; }

        public async ValueTask<JobHandlerResult> HandleAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            MaximumObserved = Math.Max(MaximumObserved, active);
            if (active == 2)
            {
                _twoStarted.TrySetResult();
            }

            await _twoStarted.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref _active);
            return JobHandlerResult.Success();
        }
    }

    private sealed class BlockingHandler : IJobHandler
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobType JobType => new("test.runtime");

        public async ValueTask<JobHandlerResult> HandleAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return JobHandlerResult.Success();
        }
    }

    private sealed class TimeoutHandler : IJobHandler
    {
        public JobType JobType => new("test.runtime");

        public async ValueTask<JobHandlerResult> HandleAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JobHandlerResult.Success();
        }
    }

    private sealed class SuccessHandler : IJobHandler
    {
        public JobType JobType => new("test.runtime");

        public ValueTask<JobHandlerResult> HandleAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(JobHandlerResult.Success());
        }
    }

    private sealed class RecordingObserver : IJobRuntimeObserver
    {
        internal List<(JobId JobId, string ErrorCode)> LeaseLosses { get; } = [];

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

        public void LeaseLost(JobId jobId, string errorCode) =>
            LeaseLosses.Add((jobId, errorCode));
    }

    private sealed class HeartbeatWaitingHandler(Task heartbeat) : IJobHandler
    {
        public JobType JobType => new("test.runtime");

        public async ValueTask<JobHandlerResult> HandleAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            await heartbeat.WaitAsync(cancellationToken);
            return JobHandlerResult.Success();
        }
    }

    private sealed class NeverCompletingHandler : IJobHandler
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobType JobType => new("test.runtime");

        public async ValueTask<JobHandlerResult> HandleAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JobHandlerResult.Success();
        }
    }
}
