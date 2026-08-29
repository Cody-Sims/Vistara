using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common.Events;
using Vistara.Persistence.Outbox;

namespace Vistara.Persistence.Events;

public sealed record PersistedEventStreamBounds(
    long OldestAvailable,
    long Latest);

public sealed class RelationalEventStreamStore(
    VistaraDbContext context,
    ITenantScope tenantScope)
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly ITenantScope _tenantScope =
        tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));

    public async ValueTask<PersistedEventStreamBounds> GetBoundsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        long oldest = await _context.Set<EventLogRow>()
            .AsNoTracking()
            .OrderBy(row => row.Sequence)
            .Select(row => row.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        long latest = await _context.Set<OutboxSequenceRow>()
            .AsNoTracking()
            .Select(row => row.LastPublishedSequence)
            .SingleOrDefaultAsync(cancellationToken);
        return new PersistedEventStreamBounds(oldest, latest);
    }

    public async ValueTask<IReadOnlyList<EventEnvelope>> ReadAsync(
        Guid tenantId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (maximumCount is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        EventLogRow[] rows = await _context.Set<EventLogRow>()
            .AsNoTracking()
            .Where(row => row.Sequence > afterSequence)
            .OrderBy(row => row.Sequence)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToEnvelope).ToArray();
    }

    private void EnsureTenant(Guid tenantId)
    {
        if (TenantScopeGuard.RequireTenantId(_tenantScope) != tenantId)
        {
            throw new InvalidOperationException(
                "Event stream reads cannot cross tenant scope.");
        }
    }

    private static EventEnvelope ToEnvelope(EventLogRow row) =>
        new(
            new EventMetadata(
                new EventId(row.EventId),
                new EventTenantId(row.TenantId),
                new EventSequence(row.Sequence),
                row.EventType,
                row.EventVersion,
                row.OccurredAtUtc,
                row.CorrelationId,
                row.CausationId),
            row.ClientPayload);
}
