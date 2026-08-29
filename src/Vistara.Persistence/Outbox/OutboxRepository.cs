using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common.Events;
using Vistara.Domain.Common;

namespace Vistara.Persistence.Outbox;

public enum OutboxPublishOutcome
{
    Published,
    AlreadyPublished,
    Released,
    OutOfOrder,
    Fenced,
    NotFound,
}

public sealed record OutboxPublishResult(OutboxPublishOutcome Outcome);

public sealed record OutboxClaim(
    Guid ClaimId,
    Guid ClaimedBy,
    DateTimeOffset ClaimedUntilUtc,
    OutboxVersion Version,
    OutboxMessage Message);

public sealed record EventLogBounds(EventCursor OldestAvailable, EventCursor Latest);

public sealed class OutboxRepository(
    DbContext context,
    IOutboxTenantContext tenantContext) :
    IOutboxWriter,
    IOutboxPublisherStore,
    IEventLogReader
{
    private const int MaximumBatchSize = 200;
    private readonly DbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly IOutboxTenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    public async ValueTask<EventSequence> ReserveSequenceAsync(
        CancellationToken cancellationToken)
    {
        OutboxSequenceRow? sequence = _context.Set<OutboxSequenceRow>()
            .Local
            .SingleOrDefault();
        sequence ??= await _context.Set<OutboxSequenceRow>()
            .SingleOrDefaultAsync(cancellationToken);
        if (sequence is null)
        {
            sequence = new OutboxSequenceRow
            {
                TenantId = _tenantContext.TenantId,
                CurrentSequence = 1,
                LastPublishedSequence = 0,
                Version = 1,
            };
            _context.Add(sequence);
            return EventSequence.First;
        }

        sequence.CurrentSequence = checked(sequence.CurrentSequence + 1);
        sequence.Version = checked(sequence.Version + 1);
        return new EventSequence(sequence.CurrentSequence);
    }

    public async ValueTask AppendAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Envelope.Metadata.TenantId.Value != _tenantContext.TenantId)
        {
            throw new InvalidOperationException(
                "Outbox messages can only be appended inside their tenant scope.");
        }

        long messageSequence = message.Envelope.Metadata.Sequence.Value;
        OutboxSequenceRow? sequence = _context.Set<OutboxSequenceRow>()
            .Local
            .SingleOrDefault();
        sequence ??= await _context.Set<OutboxSequenceRow>()
            .SingleOrDefaultAsync(cancellationToken);
        if (sequence is null)
        {
            if (messageSequence != EventSequence.First.Value)
            {
                throw new InvalidOperationException(
                    "The first tenant event must use sequence one.");
            }

            _context.Add(new OutboxSequenceRow
            {
                TenantId = _tenantContext.TenantId,
                CurrentSequence = messageSequence,
                LastPublishedSequence = 0,
                Version = 1,
            });
        }
        else if (messageSequence == sequence.CurrentSequence + 1)
        {
            sequence.CurrentSequence = messageSequence;
            sequence.Version = checked(sequence.Version + 1);
        }
        else if (messageSequence != sequence.CurrentSequence ||
                 _context.Set<OutboxMessageRow>().Local.Any(
                     row => row.Sequence == messageSequence) ||
                 await _context.Set<OutboxMessageRow>()
                     .AsNoTracking()
                     .AnyAsync(row => row.Sequence == messageSequence, cancellationToken))
        {
            throw new InvalidOperationException(
                "Outbox event sequences must be reserved or appended monotonically.");
        }

        _context.Add(ToRow(message));
    }

    public async ValueTask<IReadOnlyList<OutboxMessage>> ReadPendingAsync(
        EventCursor after,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ValidateMaximumCount(maximumCount);
        OutboxMessageRow[] rows = await _context.Set<OutboxMessageRow>()
            .AsNoTracking()
            .Where(row =>
                row.Sequence > after.Value &&
                row.PublishedAtUtc == null &&
                row.ClaimId == null)
            .OrderBy(row => row.Sequence)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToMessage).ToArray();
    }

    public async ValueTask<Result> MarkPublishedAsync(
        OutboxMessageId messageId,
        OutboxVersion expectedVersion,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureUtc(publishedAtUtc, nameof(publishedAtUtc));
        OutboxMessageRow? row = await _context.Set<OutboxMessageRow>()
            .SingleOrDefaultAsync(item => item.Id == messageId.Value, cancellationToken);
        if (row is null)
        {
            return Result.Failure(ResultError.NotFound(
                "outbox.not_found",
                "The outbox message was not found."));
        }

        if (row.PublishedAtUtc.HasValue)
        {
            return Result.Success();
        }

        if (row.Version != expectedVersion.Value || row.ClaimId.HasValue)
        {
            return Result.Failure(ResultError.Conflict(
                "outbox.fenced",
                "The outbox message changed before publication."));
        }

        if (!await TryAdvancePublishedSequenceAsync(row.Sequence, cancellationToken))
        {
            return Result.Failure(ResultError.Conflict(
                "outbox.publication_out_of_order",
                "Outbox messages must be published in sequence order."));
        }

        AddEventLogRow(row, publishedAtUtc);
        row.PublishedAtUtc = publishedAtUtc;
        row.Version = checked(row.Version + 1);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(ResultError.Conflict(
                "outbox.fenced",
                "The outbox message changed before publication."));
        }
    }

    public async ValueTask<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
        Guid claimedBy,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        EnsureUuid7(claimedBy, nameof(claimedBy));
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Claim leases must be positive and at most one hour.");
        }

        ValidateMaximumCount(maximumCount);
        Guid[] candidates = await _context.Set<OutboxMessageRow>()
            .AsNoTracking()
            .Where(row =>
                row.PublishedAtUtc == null &&
                row.AvailableAtUtc <= nowUtc &&
                (row.ClaimedUntilUtc == null || row.ClaimedUntilUtc <= nowUtc))
            .OrderBy(row => row.Sequence)
            .Select(row => row.Id)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);

        var claims = new List<OutboxClaim>(candidates.Length);
        foreach (Guid messageId in candidates)
        {
            Guid claimId = Guid.CreateVersion7();
            DateTimeOffset claimedUntilUtc = nowUtc.Add(leaseDuration);
            int updated = await _context.Set<OutboxMessageRow>()
                .Where(row =>
                    row.Id == messageId &&
                    row.PublishedAtUtc == null &&
                    row.AvailableAtUtc <= nowUtc &&
                    (row.ClaimedUntilUtc == null || row.ClaimedUntilUtc <= nowUtc))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.ClaimId, claimId)
                        .SetProperty(row => row.ClaimedBy, claimedBy)
                        .SetProperty(row => row.ClaimedUntilUtc, claimedUntilUtc)
                        .SetProperty(row => row.Attempts, row => row.Attempts + 1)
                        .SetProperty(row => row.Version, row => row.Version + 1),
                    cancellationToken);
            if (updated == 0)
            {
                continue;
            }

            OutboxMessageRow? tracked = _context.Set<OutboxMessageRow>()
                .Local
                .SingleOrDefault(row => row.Id == messageId);
            if (tracked is not null)
            {
                await _context.Entry(tracked).ReloadAsync(cancellationToken);
            }

            OutboxMessageRow claimed = await _context.Set<OutboxMessageRow>()
                .AsNoTracking()
                .SingleAsync(
                    row => row.Id == messageId && row.ClaimId == claimId,
                    cancellationToken);
            claims.Add(new OutboxClaim(
                claimId,
                claimedBy,
                claimedUntilUtc,
                new OutboxVersion(claimed.Version),
                ToMessage(claimed)));
        }

        return claims;
    }

    public async ValueTask<OutboxPublishResult> PublishClaimAsync(
        OutboxMessageId messageId,
        Guid claimId,
        OutboxVersion expectedVersion,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureUuid7(claimId, nameof(claimId));
        EnsureUtc(publishedAtUtc, nameof(publishedAtUtc));
        OutboxMessageRow? row = await _context.Set<OutboxMessageRow>()
            .SingleOrDefaultAsync(item => item.Id == messageId.Value, cancellationToken);
        if (row is null)
        {
            return new OutboxPublishResult(OutboxPublishOutcome.NotFound);
        }

        if (row.PublishedAtUtc.HasValue)
        {
            return new OutboxPublishResult(OutboxPublishOutcome.AlreadyPublished);
        }

        if (row.ClaimId != claimId || row.Version != expectedVersion.Value)
        {
            return new OutboxPublishResult(OutboxPublishOutcome.Fenced);
        }

        if (!await TryAdvancePublishedSequenceAsync(row.Sequence, cancellationToken))
        {
            return new OutboxPublishResult(OutboxPublishOutcome.OutOfOrder);
        }

        AddEventLogRow(row, publishedAtUtc);
        row.PublishedAtUtc = publishedAtUtc;
        row.ClaimId = null;
        row.ClaimedBy = null;
        row.ClaimedUntilUtc = null;
        row.LastErrorCode = null;
        row.Version = checked(row.Version + 1);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new OutboxPublishResult(OutboxPublishOutcome.Published);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new OutboxPublishResult(OutboxPublishOutcome.Fenced);
        }
    }

    public async ValueTask<OutboxPublishResult> ReleaseClaimAsync(
        OutboxMessageId messageId,
        Guid claimId,
        OutboxVersion expectedVersion,
        DateTimeOffset retryAtUtc,
        string errorCode,
        CancellationToken cancellationToken)
    {
        EnsureUuid7(claimId, nameof(claimId));
        EnsureUtc(retryAtUtc, nameof(retryAtUtc));
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        int updated = await _context.Set<OutboxMessageRow>()
            .Where(row =>
                row.Id == messageId.Value &&
                row.PublishedAtUtc == null &&
                row.ClaimId == claimId &&
                row.Version == expectedVersion.Value)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.ClaimId, (Guid?)null)
                    .SetProperty(row => row.ClaimedBy, (Guid?)null)
                    .SetProperty(row => row.ClaimedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(row => row.AvailableAtUtc, retryAtUtc)
                    .SetProperty(row => row.LastErrorCode, errorCode)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);
        return new OutboxPublishResult(
            updated == 1 ? OutboxPublishOutcome.Released : OutboxPublishOutcome.Fenced);
    }

    public async ValueTask<Result<EventPage>> ReadAfterAsync(
        EventTenantId tenantId,
        EventCursor after,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ValidateMaximumCount(maximumCount);
        if (tenantId.Value != _tenantContext.TenantId)
        {
            return Result.Failure<EventPage>(ResultError.NotFound(
                "events.not_found",
                "The event stream was not found."));
        }

        EventLogBounds bounds = await GetEventLogBoundsAsync(cancellationToken);
        if (after.Value > bounds.Latest.Value)
        {
            return Result.Failure<EventPage>(ResultError.Conflict(
                "events.cursor_in_future",
                "The event cursor is ahead of the retained event log."));
        }

        if (IsStale(after.Value, bounds))
        {
            return Result.Failure<EventPage>(ResultError.Conflict(
                "events.resync_required",
                "The event cursor is older than the retained event log."));
        }

        EventLogRow[] rows = await _context.Set<EventLogRow>()
            .AsNoTracking()
            .Where(row => row.Sequence > after.Value)
            .OrderBy(row => row.Sequence)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
        return EventPage.Create(rows.Select(ToEnvelope));
    }

    public async ValueTask<EventLogBounds> GetEventLogBoundsAsync(
        CancellationToken cancellationToken)
    {
        IQueryable<EventLogRow> rows = _context.Set<EventLogRow>().AsNoTracking();
        long oldest = await rows
            .OrderBy(row => row.Sequence)
            .Select(row => row.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        long latest = await _context.Set<OutboxSequenceRow>()
            .AsNoTracking()
            .Select(row => row.LastPublishedSequence)
            .SingleOrDefaultAsync(cancellationToken);
        return new EventLogBounds(new EventCursor(oldest), new EventCursor(latest));
    }

    public async ValueTask<int> PruneEventLogAsync(
        DateTimeOffset nowUtc,
        int maximumRetainedEvents,
        TimeSpan maximumAge,
        CancellationToken cancellationToken)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedEvents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumAge, TimeSpan.Zero);

        long countBoundary = await _context.Set<EventLogRow>()
            .AsNoTracking()
            .OrderByDescending(row => row.Sequence)
            .Skip(maximumRetainedEvents)
            .Select(row => row.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        DateTimeOffset ageBoundary = nowUtc.Subtract(maximumAge);
        return await _context.Set<EventLogRow>()
            .Where(row =>
                row.RetainedAtUtc < ageBoundary ||
                (countBoundary > 0 && row.Sequence <= countBoundary))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static OutboxMessageRow ToRow(OutboxMessage message)
    {
        EventMetadata metadata = message.Envelope.Metadata;
        if (!IsSafeEventType(metadata.EventType))
        {
            throw new ArgumentException(
                "Event types must contain only safe token characters.",
                nameof(message));
        }

        return new OutboxMessageRow
        {
            Id = message.Id.Value,
            TenantId = metadata.TenantId.Value,
            Sequence = metadata.Sequence.Value,
            EventId = metadata.EventId.Value,
            EventType = metadata.EventType,
            EventVersion = metadata.EventVersion,
            ClientPayload = SafeEventPayload.Sanitize(message.Envelope.ClientPayload),
            OccurredAtUtc = metadata.OccurredAtUtc,
            CorrelationId = metadata.CorrelationId,
            CausationId = metadata.CausationId,
            CreatedAtUtc = message.CreatedAtUtc,
            AvailableAtUtc = message.CreatedAtUtc,
            Version = message.Version.Value,
        };
    }

    private static OutboxMessage ToMessage(OutboxMessageRow row) =>
        OutboxMessage.Create(
            new OutboxMessageId(row.Id),
            ToEnvelope(row),
            row.CreatedAtUtc);

    private static EventEnvelope ToEnvelope(OutboxMessageRow row) =>
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

    private void AddEventLogRow(OutboxMessageRow row, DateTimeOffset retainedAtUtc)
    {
        _context.Add(new EventLogRow
        {
            TenantId = row.TenantId,
            Sequence = row.Sequence,
            EventId = row.EventId,
            EventType = row.EventType,
            EventVersion = row.EventVersion,
            ClientPayload = row.ClientPayload,
            OccurredAtUtc = row.OccurredAtUtc,
            CorrelationId = row.CorrelationId,
            CausationId = row.CausationId,
            RetainedAtUtc = retainedAtUtc,
        });
    }

    private async ValueTask<bool> TryAdvancePublishedSequenceAsync(
        long sequence,
        CancellationToken cancellationToken)
    {
        OutboxSequenceRow? state = _context.Set<OutboxSequenceRow>()
            .Local
            .SingleOrDefault();
        state ??= await _context.Set<OutboxSequenceRow>()
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null || sequence != state.LastPublishedSequence + 1)
        {
            return false;
        }

        state.LastPublishedSequence = sequence;
        state.Version = checked(state.Version + 1);
        return true;
    }

    private static bool IsStale(long afterSequence, EventLogBounds bounds) =>
        afterSequence != 0 &&
        ((bounds.OldestAvailable.Value == 0 && afterSequence < bounds.Latest.Value) ||
         (bounds.OldestAvailable.Value > 0 &&
          afterSequence < bounds.OldestAvailable.Value - 1));

    private static bool IsSafeEventType(string eventType) =>
        eventType.Length is > 0 and <= 200 &&
        eventType.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-' or '_' or ':');

    private static void ValidateMaximumCount(int maximumCount)
    {
        if (maximumCount is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                $"Batch size must be between 1 and {MaximumBatchSize}.");
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("A non-empty UUIDv7 value is required.", parameterName);
        }
    }
}
