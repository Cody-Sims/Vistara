using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Worker.Runtime.Reconciliation;
using Xunit;

namespace Vistara.IntegrationTests.Reconciliation;

public sealed class ReconciliationEnqueueIsolationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_failing_tenant_does_not_stop_the_remaining_tenants()
    {
        Guid first = Guid.CreateVersion7();
        Guid failing = Guid.CreateVersion7();
        Guid last = Guid.CreateVersion7();
        var queue = new ScriptedJobQueue { Throwing = { failing } };
        await using ServiceProvider provider =
            BuildProvider([first, failing, last], queue);

        ReconciliationEnqueueReport report = await provider
            .GetRequiredService<ReconciliationScheduler>()
            .EnqueueWindowAsync(
                ReconciliationSchedules.BlobIntegrity,
                CancellationToken.None);

        Assert.Equal(2, report.Created);
        Assert.Equal(0, report.Existing);
        Assert.Equal(3, report.Attempted);
        ReconciliationEnqueueFailure failure = Assert.Single(report.Failures);
        Assert.Equal(failing, failure.TenantId);
        Assert.Equal("reconciliation_failure", failure.ReasonCode);
        Assert.Equal([first, last], queue.Enqueued);
    }

    [Fact]
    public async Task A_rejected_enqueue_is_reported_without_stopping_the_sweep()
    {
        Guid first = Guid.CreateVersion7();
        Guid rejecting = Guid.CreateVersion7();
        Guid last = Guid.CreateVersion7();
        var queue = new ScriptedJobQueue { Rejecting = { rejecting } };
        await using ServiceProvider provider =
            BuildProvider([first, rejecting, last], queue);

        ReconciliationEnqueueReport report = await provider
            .GetRequiredService<ReconciliationScheduler>()
            .EnqueueWindowAsync(
                ReconciliationSchedules.PurgeRecovery,
                CancellationToken.None);

        Assert.Equal(2, report.Created);
        Assert.Single(report.Failures, failure => failure.TenantId == rejecting);
        Assert.Equal([first, last], queue.Enqueued);
    }

    [Fact]
    public async Task Every_tenant_failure_is_reported_individually()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();
        var queue = new ScriptedJobQueue { Throwing = { first, second } };
        await using ServiceProvider provider =
            BuildProvider([first, second], queue);

        ReconciliationEnqueueReport report = await provider
            .GetRequiredService<ReconciliationScheduler>()
            .EnqueueWindowAsync(
                ReconciliationSchedules.BlobIntegrity,
                CancellationToken.None);

        Assert.Equal(0, report.Created);
        Assert.Equal(
            [first, second],
            report.Failures.Select(failure => failure.TenantId));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Cancellation_still_stops_the_fan_out_immediately()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();
        var queue = new ScriptedJobQueue();
        await using ServiceProvider provider =
            BuildProvider([first, second], queue);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider
                .GetRequiredService<ReconciliationScheduler>()
                .EnqueueWindowAsync(
                    ReconciliationSchedules.BlobIntegrity,
                    cancelled.Token));

        Assert.Empty(queue.Enqueued);
    }

    private static ServiceProvider BuildProvider(
        Guid[] tenantIds,
        ScriptedJobQueue queue)
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddScoped<ScopedTenant>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddSingleton<IWorkerTenantCatalog>(
            new StaticTenantCatalog(tenantIds));
        services.AddSingleton<IJobQueue>(queue);
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IUuid7Generator>(
            new Uuid7Generator(new FixedClock(Now)));
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.BlobIntegrity);
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.PurgeRecovery);
        services.AddSingleton(provider => new ReconciliationScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<IUuid7Generator>(),
            provider.GetServices<ReconciliationSchedule>(),
            NullLogger<ReconciliationScheduler>.Instance));
        return services.BuildServiceProvider();
    }

    private sealed class StaticTenantCatalog(Guid[] tenantIds)
        : IWorkerTenantCatalog
    {
        public ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<Guid>>(tenantIds);
        }
    }

    private sealed class ScriptedJobQueue : IJobQueue
    {
        internal HashSet<Guid> Throwing { get; } = [];

        internal HashSet<Guid> Rejecting { get; } = [];

        internal List<Guid> Enqueued { get; } = [];

        public ValueTask<Result<JobEnqueueResult>> EnqueueAsync(
            DurableJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid tenantId = job.TenantId.Value;
            if (Throwing.Contains(tenantId))
            {
                throw new InvalidOperationException(
                    "The tenant job store is unavailable.");
            }

            if (Rejecting.Contains(tenantId))
            {
                return ValueTask.FromResult(
                    Result.Failure<JobEnqueueResult>(
                        ResultError.Conflict(
                            "jobs.enqueue_conflict",
                            "The job could not be enqueued.")));
            }

            Enqueued.Add(tenantId);
            return ValueTask.FromResult(
                Result.Success(new JobEnqueueResult(job.Id, true)));
        }

        public ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseAsync(
            JobLeaseRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<JobLease>> HeartbeatAsync(
            JobHeartbeatRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> CompleteAsync(
            JobCompletionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> FailAsync(
            JobFailureRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> RecoverExpiredAsync(
            JobExpiredLeaseRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class ScopedTenant : IMutableTenantScope
    {
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
                    "A reconciliation scope cannot switch tenants.");
            }

            _tenantId = tenantId;
        }
    }
}
