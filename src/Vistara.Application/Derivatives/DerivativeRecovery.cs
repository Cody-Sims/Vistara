namespace Vistara.Application.Derivatives;

/// <summary>
/// A derivative request that is still expected to produce output while its
/// durable generation job has been dead-lettered, so no worker will ever pick
/// the work up again without operator or reconciliation intervention.
/// </summary>
public sealed record StalledDerivativeRequest(
    Guid RequestId,
    Guid JobId,
    long JobVersion,
    int JobMaxAttempts,
    string? FailureCode,
    DateTimeOffset UpdatedAtUtc);

public enum DerivativeRecoveryOutcome
{
    /// <summary>The generation job was returned to the retry queue.</summary>
    Requeued,

    /// <summary>
    /// The recovery budget is spent; the request was closed as failed so
    /// callers stop waiting on a derivative that will never arrive.
    /// </summary>
    Exhausted,

    /// <summary>
    /// Another worker already recovered or advanced the request, so this
    /// attempt made no change.
    /// </summary>
    Stale,
}

/// <summary>
/// Tenant-scoped durable state required to recover dead-lettered derivative
/// generation without rebuilding a generation payload the request row cannot
/// reproduce.
/// </summary>
public interface IDerivativeRecoveryPort
{
    ValueTask<IReadOnlyList<StalledDerivativeRequest>> ListStalledAsync(
        Guid tenantId,
        DateTimeOffset stalledBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies exactly one fenced recovery transition for the candidate.
    /// Implementations must no-op and report <see cref="DerivativeRecoveryOutcome.Stale"/>
    /// when the persisted job version no longer matches.
    /// </summary>
    ValueTask<DerivativeRecoveryOutcome> RecoverAsync(
        Guid tenantId,
        StalledDerivativeRequest candidate,
        DerivativeRecoveryBudget budget,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}

public sealed record DerivativeRecoveryBudget
{
    public DerivativeRecoveryBudget(int additionalAttempts, int maximumAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additionalAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumAttempts,
            additionalAttempts);
        AdditionalAttempts = additionalAttempts;
        MaximumAttempts = maximumAttempts;
    }

    public int AdditionalAttempts { get; }

    public int MaximumAttempts { get; }
}
