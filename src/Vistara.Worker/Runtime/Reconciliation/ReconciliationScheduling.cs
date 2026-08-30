using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Observability.Telemetry;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;

namespace Vistara.Worker.Runtime.Reconciliation;

/// <summary>
/// Declares a repeating repair sweep. Each schedule is enqueued as a durable
/// job per routed tenant so reconciliation inherits job leasing, retry, and
/// dead-lettering instead of running unmanaged background work.
/// </summary>
public sealed record ReconciliationSchedule
{
    public required string JobType { get; init; }

    public required string DedupePrefix { get; init; }

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    public int PayloadVersion { get; init; } = 1;

    public bool DryRun { get; init; } = true;

    public int MaxAttempts { get; init; } = 5;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(JobType) ||
            string.IsNullOrWhiteSpace(DedupePrefix) ||
            DedupePrefix.Contains(':', StringComparison.Ordinal) ||
            InitialDelay < TimeSpan.Zero ||
            Interval <= TimeSpan.Zero ||
            PayloadVersion < 1 ||
            MaxAttempts is < 1 or > 20)
        {
            throw new InvalidOperationException(
                "A reconciliation schedule is invalid.");
        }
    }
}

public sealed record ReconciliationSchedulePayload(string? Cursor, bool DryRun);

/// <summary>
/// The repair sweeps the worker runs by default. Destructive sweeps start in
/// dry-run so operators see a report before anything is deleted.
/// </summary>
public static class ReconciliationSchedules
{
    public static ReconciliationSchedule BlobIntegrity { get; } = new()
    {
        JobType = "storage.reconcile",
        DedupePrefix = "storage-reconcile",
        InitialDelay = TimeSpan.FromMinutes(5),
        Interval = TimeSpan.FromHours(6),
        DryRun = true,
    };

    public static ReconciliationSchedule PurgeRecovery { get; } = new()
    {
        JobType = "lifecycle.purge.reconcile",
        DedupePrefix = "lifecycle-purge-reconcile",
        InitialDelay = TimeSpan.FromMinutes(10),
        Interval = TimeSpan.FromMinutes(30),
        DryRun = false,
    };
}

/// <summary>
/// Fans reconciliation schedules out across routed tenants. Enqueueing is
/// window-deduplicated so repeated sweeps, restarts, and overlapping workers
/// converge on a single job per tenant and window.
/// </summary>
public sealed class ReconciliationScheduler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly ReconciliationSchedule[] _schedules;

    public ReconciliationScheduler(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        IUuid7Generator idGenerator,
        IEnumerable<ReconciliationSchedule> schedules)
    {
        _scopeFactory = scopeFactory ??
            throw new ArgumentNullException(nameof(scopeFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ??
            throw new ArgumentNullException(nameof(idGenerator));
        ArgumentNullException.ThrowIfNull(schedules);
        _schedules = [.. schedules];
        foreach (ReconciliationSchedule schedule in _schedules)
        {
            schedule.Validate();
        }

        if (_schedules
            .Select(schedule => schedule.DedupePrefix)
            .Distinct(StringComparer.Ordinal)
            .Count() != _schedules.Length)
        {
            throw new InvalidOperationException(
                "Reconciliation schedules must use distinct dedupe prefixes.");
        }
    }

    public IReadOnlyList<ReconciliationSchedule> Schedules => _schedules;

    public Task RunAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_schedules.Select(
            schedule => RunScheduleAsync(schedule, stoppingToken)));

    public async ValueTask<int> EnqueueWindowAsync(
        ReconciliationSchedule schedule,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Validate();
        DateTimeOffset now = _clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The reconciliation clock must return UTC.");
        }

        long window = now.UtcDateTime.Ticks / schedule.Interval.Ticks;
        string payload = JsonSerializer.Serialize(
            new ReconciliationSchedulePayload(Cursor: null, schedule.DryRun),
            JsonOptions);
        var jobType = new JobType(schedule.JobType);

        IReadOnlyList<Guid> tenantIds;
        await using (AsyncServiceScope catalogScope =
                     _scopeFactory.CreateAsyncScope())
        {
            tenantIds = await catalogScope.ServiceProvider
                .GetRequiredService<IWorkerTenantCatalog>()
                .ListTenantIdsAsync(cancellationToken);
        }

        int created = 0;
        foreach (Guid tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using AsyncServiceScope scope =
                _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider
                .GetRequiredService<IMutableTenantScope>()
                .Establish(tenantId);
            DurableJob job = DurableJob.Create(
                new JobId(_idGenerator.NewId()),
                new JobTenantId(tenantId),
                jobType,
                payload,
                schedule.PayloadVersion,
                new JobDedupeKey(
                    $"{schedule.DedupePrefix}:{schedule.PayloadVersion}:{window}"),
                priority: 0,
                maxAttempts: schedule.MaxAttempts,
                availableAtUtc: now,
                createdAtUtc: now);
            Result<JobEnqueueResult> result = await scope.ServiceProvider
                .GetRequiredService<IJobQueue>()
                .EnqueueAsync(job, cancellationToken);
            if (!result.TryGetValue(out JobEnqueueResult? enqueued))
            {
                throw new InvalidOperationException(
                    "A scheduled reconciliation job could not be enqueued.");
            }

            if (enqueued.WasCreated)
            {
                created++;
            }
        }

        return created;
    }

    private async Task RunScheduleAsync(
        ReconciliationSchedule schedule,
        CancellationToken stoppingToken)
    {
        try
        {
            if (schedule.InitialDelay > TimeSpan.Zero)
            {
                await Task.Delay(schedule.InitialDelay, stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                using (TelemetryOperation operation =
                       VistaraTelemetry.Start(TelemetryOperationKind.Reconciliation))
                {
                    try
                    {
                        _ = await EnqueueWindowAsync(schedule, stoppingToken);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        operation.Cancel();
                        return;
                    }
#pragma warning disable CA1031
                    catch (Exception)
                    {
                        operation.Fail("reconciliation_failure");
                    }
#pragma warning restore CA1031
                }

                await Task.Delay(schedule.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

public static class ReconciliationSchedulerServiceCollectionExtensions
{
    /// <summary>
    /// Adds a repair sweep to the orchestrator. Registration is keyed on the
    /// dedupe prefix so repeated composition cannot double-schedule a sweep.
    /// </summary>
    public static IServiceCollection AddVistaraReconciliationSchedule(
        this IServiceCollection services,
        ReconciliationSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Validate();
        bool exists = services.Any(descriptor =>
            descriptor.ServiceType == typeof(ReconciliationSchedule) &&
            descriptor.ImplementationInstance is ReconciliationSchedule existing &&
            string.Equals(
                existing.DedupePrefix,
                schedule.DedupePrefix,
                StringComparison.Ordinal));
        if (!exists)
        {
            services.AddSingleton(schedule);
        }

        return services;
    }

    public static IServiceCollection AddVistaraReconciliationScheduler(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ReconciliationScheduler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                ReconciliationSchedulerHostedService>());
        return services;
    }
}

internal sealed class ReconciliationSchedulerHostedService(
    ReconciliationScheduler scheduler) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        scheduler.RunAsync(stoppingToken);
}
