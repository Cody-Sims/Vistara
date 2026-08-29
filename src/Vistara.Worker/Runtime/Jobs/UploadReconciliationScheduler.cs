using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Worker.Features.Reconciliation.Uploads;

namespace Vistara.Worker.Runtime.Jobs;

public sealed class UploadReconciliationScheduler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly UploadReconciliationScheduleMetadata _schedule;
    private readonly JobType _jobType;

    public UploadReconciliationScheduler(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        IUuid7Generator idGenerator,
        UploadReconciliationScheduleMetadata schedule)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        if (schedule.InitialDelay < TimeSpan.Zero ||
            schedule.Interval <= TimeSpan.Zero ||
            schedule.PayloadVersion != 1 ||
            !string.Equals(
                schedule.JobType,
                UploadReconciliationJobHandler.SupportedJobType.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Upload reconciliation schedule metadata is invalid.");
        }

        _jobType = new JobType(schedule.JobType);
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_schedule.InitialDelay > TimeSpan.Zero)
        {
            await Task.Delay(_schedule.InitialDelay, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _ = await EnqueueCurrentWindowAsync(stoppingToken);
            await Task.Delay(_schedule.Interval, stoppingToken);
        }
    }

    public async ValueTask<int> EnqueueCurrentWindowAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The upload reconciliation clock must return UTC.");
        }

        long window = now.UtcDateTime.Ticks / _schedule.Interval.Ticks;
        string payload = JsonSerializer.Serialize(
            new SchedulePayload(Cursor: null, _schedule.DryRun),
            JsonOptions);
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        VistaraDbContext database =
            scope.ServiceProvider.GetRequiredService<VistaraDbContext>();
        Guid[] tenantIds = await database.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(tenant => tenant.Id.Value)
            .ToArrayAsync(cancellationToken);
        IJobQueue queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        int created = 0;
        foreach (Guid tenantId in tenantIds)
        {
            DurableJob job = DurableJob.Create(
                new JobId(_idGenerator.NewId()),
                new JobTenantId(tenantId),
                _jobType,
                payload,
                _schedule.PayloadVersion,
                new JobDedupeKey(
                    $"upload-reconcile:{_schedule.PayloadVersion}:{window}"),
                priority: 0,
                maxAttempts: 5,
                availableAtUtc: now,
                createdAtUtc: now);
            Result<JobEnqueueResult> result =
                await queue.EnqueueAsync(job, cancellationToken);
            if (!result.TryGetValue(out JobEnqueueResult? enqueued))
            {
                throw new InvalidOperationException(
                    "A scheduled upload reconciliation job could not be enqueued.");
            }

            if (enqueued.WasCreated)
            {
                created++;
            }
        }

        return created;
    }

    private sealed record SchedulePayload(string? Cursor, bool DryRun);
}

internal sealed class UploadReconciliationSchedulerHostedService(
    UploadReconciliationScheduler scheduler) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        scheduler.RunAsync(stoppingToken);
}
