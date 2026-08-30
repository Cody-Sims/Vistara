using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Common;
using Vistara.Application.Jobs;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Observability.Telemetry;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Worker.Runtime.Jobs;
using Vistara.Worker.Runtime.Reconciliation;

namespace Vistara.Worker.Features.Reconciliation.Lifecycle;

public sealed record PurgeRecoveryOptions
{
    /// <summary>
    /// How long an executing purge batch may stall before its job is
    /// re-enqueued. Purge execution is idempotent, so recovery only needs to
    /// restore progress, never to undo it.
    /// </summary>
    public TimeSpan StalledAfter { get; init; } = TimeSpan.FromMinutes(30);

    public int BatchSize { get; init; } = 50;

    internal void Validate()
    {
        if (StalledAfter <= TimeSpan.Zero || BatchSize is < 1 or > 500)
        {
            throw new InvalidOperationException(
                "Purge recovery reconciliation options are invalid.");
        }
    }
}

public sealed record StalledPurgeBatch(Guid BatchId, DateTimeOffset StartedAtUtc);

public interface IPurgeRecoveryStatePort
{
    ValueTask<IReadOnlyList<StalledPurgeBatch>> ListStalledBatchesAsync(
        Guid tenantId,
        DateTimeOffset stalledBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken);
}

public sealed record PurgeRecoveryReport(int Detected, int Requeued);

public sealed class PurgeRecoveryService(
    IPurgeRecoveryStatePort state,
    IJobQueue queue,
    IClock clock,
    IUuid7Generator idGenerator,
    PurgeRecoveryOptions options)
{
    private readonly IPurgeRecoveryStatePort _state =
        state ?? throw new ArgumentNullException(nameof(state));
    private readonly IJobQueue _queue =
        queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
    private readonly PurgeRecoveryOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<PurgeRecoveryReport> RunAsync(
        Guid tenantId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        _options.Validate();
        using TelemetryOperation operation =
            VistaraTelemetry.Start(TelemetryOperationKind.Reconciliation);
        try
        {
            DateTimeOffset now = _clock.UtcNow;
            IReadOnlyList<StalledPurgeBatch> stalled =
                await _state.ListStalledBatchesAsync(
                    tenantId,
                    now - _options.StalledAfter,
                    _options.BatchSize,
                    cancellationToken);
            int requeued = 0;
            foreach (StalledPurgeBatch batch in stalled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VistaraTelemetry.RecordCheckpoint(
                    TelemetryCheckpointKind.ReconciliationCandidateRevalidated);
                if (dryRun)
                {
                    continue;
                }

                if (await RequeueAsync(tenantId, batch, now, cancellationToken))
                {
                    requeued++;
                    VistaraTelemetry.RecordCheckpoint(
                        TelemetryCheckpointKind.ReconciliationSessionTransitioned);
                }
            }

            return new PurgeRecoveryReport(stalled.Count, requeued);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception)
        {
            operation.Fail("reconciliation_failure");
            throw;
        }
    }

    private async ValueTask<bool> RequeueAsync(
        Guid tenantId,
        StalledPurgeBatch batch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DurableJob job = DurableJob.Create(
            new JobId(_idGenerator.NewId()),
            new JobTenantId(tenantId),
            LifecycleJobContracts.PurgeType,
            LifecycleJobContracts.SerializePurge(
                new LifecyclePurgeJobPayload(tenantId, batch.BatchId)),
            LifecycleJobContracts.PayloadVersion,
            new JobDedupeKey($"lifecycle-purge-recovery:{batch.BatchId:N}"),
            priority: 0,
            maxAttempts: 5,
            availableAtUtc: now,
            createdAtUtc: now);
        Result<JobEnqueueResult> result =
            await _queue.EnqueueAsync(job, cancellationToken);
        return result.TryGetValue(out JobEnqueueResult? enqueued) &&
            enqueued.WasCreated;
    }
}

public sealed class PurgeRecoveryJobHandler(PurgeRecoveryService service)
    : IJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly PurgeRecoveryService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType { get; } =
        new("lifecycle.purge.reconcile");

    public JobType JobType => SupportedJobType;

    public async ValueTask<JobHandlerResult> HandleAsync(
        DurableJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.PayloadVersion != 1 ||
            job.Type.Value != SupportedJobType.Value ||
            !TryReadPayload(job.Payload, out ReconciliationSchedulePayload? payload))
        {
            return JobHandlerResult.Failed(
                new JobFailure(JobFailureReason.ProcessingFailed));
        }

        _ = await _service.RunAsync(
            job.TenantId.Value,
            payload!.DryRun,
            cancellationToken);
        return JobHandlerResult.Success();
    }

    private static bool TryReadPayload(
        string json,
        out ReconciliationSchedulePayload? payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<ReconciliationSchedulePayload>(
                json,
                JsonOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            payload = null;
            return false;
        }
    }
}

public static class PurgeRecoveryServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraPurgeRecoveryReconciliation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new PurgeRecoveryOptions());
        services.TryAddScoped<
            IPurgeRecoveryStatePort,
            RelationalPurgeRecoveryStateAdapter>();
        services.TryAddScoped<PurgeRecoveryService>();
        services.TryAddScoped<PurgeRecoveryJobHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, PurgeRecoveryJobHandler>());
        return services;
    }
}

internal sealed class RelationalPurgeRecoveryStateAdapter(
    VistaraDbContext context,
    IMutableTenantScope tenantScope) : IPurgeRecoveryStatePort
{
    private const string ExecutingState = "Executing";

    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IMutableTenantScope _tenantScope =
        tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));

    public async ValueTask<IReadOnlyList<StalledPurgeBatch>>
        ListStalledBatchesAsync(
            Guid tenantId,
            DateTimeOffset stalledBeforeUtc,
            int batchSize,
            CancellationToken cancellationToken)
    {
        _tenantScope.Establish(tenantId);
        List<PurgeBatchRow> rows = await _context.PurgeBatches
            .AsNoTracking()
            .Where(row =>
                row.State == ExecutingState &&
                row.RequestedAtUtc <= stalledBeforeUtc)
            .OrderBy(row => row.RequestedAtUtc)
            .ThenBy(row => row.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(row =>
                new StalledPurgeBatch(row.Id, row.RequestedAtUtc)),
        ];
    }
}
