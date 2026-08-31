using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Common;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Observability.Telemetry;
using Vistara.Persistence.Derivatives.Worker;
using Vistara.Worker.Runtime.Jobs;
using Vistara.Worker.Runtime.Reconciliation;

namespace Vistara.Worker.Features.Reconciliation.Derivatives;

public sealed record DerivativeRecoveryOptions
{
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// How long a derivative may sit behind a dead-lettered job before
    /// recovery revives it. The delay keeps recovery clear of a failure the
    /// job runtime is still reporting.
    /// </summary>
    public TimeSpan StalledAfter { get; init; } = TimeSpan.FromMinutes(15);

    public int AdditionalAttempts { get; init; } = 3;

    /// <summary>
    /// The ceiling on total generation attempts across every recovery round.
    /// Once reached, the derivative request is closed as failed instead of
    /// being revived forever.
    /// </summary>
    public int MaximumAttempts { get; init; } = 11;

    internal DerivativeRecoveryBudget Budget() =>
        new(AdditionalAttempts, MaximumAttempts);

    internal void Validate()
    {
        if (BatchSize is < 1 or > 500 ||
            StalledAfter <= TimeSpan.Zero ||
            AdditionalAttempts is < 1 or > 20 ||
            MaximumAttempts < AdditionalAttempts ||
            MaximumAttempts > 100)
        {
            throw new InvalidOperationException(
                "Derivative recovery reconciliation options are invalid.");
        }
    }
}

public sealed record DerivativeRecoveryReport(
    int Detected,
    int Requeued,
    int Exhausted);

public sealed class DerivativeRecoveryService(
    IDerivativeRecoveryPort port,
    IClock clock,
    DerivativeRecoveryOptions options)
{
    private readonly IDerivativeRecoveryPort _port =
        port ?? throw new ArgumentNullException(nameof(port));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly DerivativeRecoveryOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<DerivativeRecoveryReport> RunAsync(
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
            IReadOnlyList<StalledDerivativeRequest> stalled =
                await _port.ListStalledAsync(
                    tenantId,
                    now - _options.StalledAfter,
                    _options.BatchSize,
                    cancellationToken);
            int requeued = 0;
            int exhausted = 0;
            foreach (StalledDerivativeRequest candidate in stalled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VistaraTelemetry.RecordCheckpoint(
                    TelemetryCheckpointKind.ReconciliationCandidateRevalidated);
                if (dryRun)
                {
                    continue;
                }

                DerivativeRecoveryOutcome outcome = await _port.RecoverAsync(
                    tenantId,
                    candidate,
                    _options.Budget(),
                    now,
                    cancellationToken);
                switch (outcome)
                {
                    case DerivativeRecoveryOutcome.Requeued:
                        requeued++;
                        VistaraTelemetry.RecordCheckpoint(
                            TelemetryCheckpointKind
                                .ReconciliationSessionTransitioned);
                        break;
                    case DerivativeRecoveryOutcome.Exhausted:
                        exhausted++;
                        VistaraTelemetry.RecordCheckpoint(
                            TelemetryCheckpointKind.ReconciliationQuarantined);
                        break;
                    default:
                        break;
                }
            }

            return new DerivativeRecoveryReport(
                stalled.Count,
                requeued,
                exhausted);
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
}

public sealed class DerivativeRecoveryJobHandler(
    DerivativeRecoveryService service) : IJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly DerivativeRecoveryService _service =
        service ?? throw new ArgumentNullException(nameof(service));

    public static JobType SupportedJobType { get; } =
        new("derivative.reconcile");

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

public static class DerivativeRecoveryServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraDerivativeRecoveryReconciliation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new DerivativeRecoveryOptions());
        services.TryAddScoped<
            IDerivativeRecoveryPort,
            RelationalDerivativeRecoveryPort>();
        services.TryAddScoped<DerivativeRecoveryService>();
        services.TryAddScoped<DerivativeRecoveryJobHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, DerivativeRecoveryJobHandler>());
        return services;
    }
}
