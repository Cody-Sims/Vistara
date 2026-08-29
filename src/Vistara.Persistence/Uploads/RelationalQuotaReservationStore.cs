using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Uploads.Quotas;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Uploads;

public sealed class RelationalQuotaReservationStore(VistaraDbContext context)
    : IQuotaReservationStore
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<QuotaStoreReserveResult> TryReserveAsync(
        AtomicQuotaReservation command,
        CancellationToken cancellationToken)
    {
        Validate(command);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId.Value,
                cancellationToken);
        try
        {
            QuotaStoreReserveResult result =
                await QuotaPersistence.TryReserveTrackedAsync(
                    _context,
                    command,
                    uploadSessionId: null,
                    cancellationToken);
            if (result.Status == QuotaStoreReserveStatus.LimitExceeded)
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return result;
            }

            if (result.Status != QuotaStoreReserveStatus.Reserved)
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return result;
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return QuotaStoreReserveResult.VersionConflict(
                await ReadSnapshotAsync(command.TenantId.Value, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return QuotaStoreReserveResult.VersionConflict(
                await ReadSnapshotAsync(command.TenantId.Value, cancellationToken));
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return QuotaStoreReserveResult.VersionConflict(
                await ReadSnapshotAsync(command.TenantId.Value, cancellationToken));
        }
    }

    public async ValueTask<QuotaStoreTransitionResult> TransitionAsync(
        AtomicQuotaTransition command,
        CancellationToken cancellationToken)
    {
        Validate(command);
        cancellationToken.ThrowIfCancellationRequested();
        Guid tenantId = _context.TenantId;
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        try
        {
            QuotaStoreTransitionResult result =
                await QuotaPersistence.TransitionTrackedAsync(
                    _context,
                    command,
                    consumedByOperationId: null,
                    cancellationToken);
            if (result.Status != QuotaStoreTransitionStatus.Transitioned)
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return result;
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return await ReadTransitionConflictAsync(command, cancellationToken);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return await ReadTransitionConflictAsync(command, cancellationToken);
        }
    }

    private async ValueTask<QuotaUsageSnapshot> ReadSnapshotAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        QuotaUsageSnapshot snapshot =
            await QuotaPersistence.ReadSnapshotAsync(_context, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return snapshot;
    }

    private async ValueTask<QuotaStoreTransitionResult> ReadTransitionConflictAsync(
        AtomicQuotaTransition command,
        CancellationToken cancellationToken)
    {
        Guid tenantId = _context.TenantId;
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        QuotaReservationRow? row = await _context.QuotaReservations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == command.ReservationId,
                cancellationToken);
        QuotaUsageSnapshot snapshot =
            await QuotaPersistence.ReadSnapshotAsync(_context, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return row is null
            ? QuotaStoreTransitionResult.NotFound()
            : QuotaStoreTransitionResult.VersionConflict(
                QuotaPersistence.ToDomain(row),
                snapshot);
    }

    private static void Validate(AtomicQuotaReservation command)
    {
        if (command.ReservationId == Guid.Empty ||
            command.ReservationId.Version != 7)
        {
            throw new ArgumentException(
                "A UUIDv7 reservation ID is required.",
                nameof(command));
        }

        if (string.IsNullOrEmpty(command.IdempotencyKey) ||
            command.IdempotencyKey.Length > 200 ||
            string.IsNullOrEmpty(command.RequestFingerprint) ||
            command.RequestFingerprint.Length > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Quota idempotency values exceed persistence limits.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(
            command.ExpectedUsageVersion);
        if (command.NowUtc.Offset != TimeSpan.Zero ||
            command.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            command.ExpiresAtUtc <= command.NowUtc)
        {
            throw new ArgumentException(
                "Quota reservation timestamps must be ordered UTC values.",
                nameof(command));
        }

        if (command.Amount == QuotaAmounts.Zero)
        {
            throw new ArgumentException(
                "A quota reservation must reserve at least one resource.",
                nameof(command));
        }
    }

    private static void Validate(AtomicQuotaTransition command)
    {
        if (command.ReservationId == Guid.Empty ||
            command.ReservationId.Version != 7 ||
            command.ExpectedVersion < 1 ||
            command.NowUtc.Offset != TimeSpan.Zero ||
            command.TargetState == QuotaReservationState.Reserved ||
            !Enum.IsDefined(command.TargetState))
        {
            throw new ArgumentException(
                "The quota transition is invalid.",
                nameof(command));
        }
    }
}

internal static class QuotaPersistence
{
    internal static async ValueTask<QuotaStoreReserveResult> TryReserveTrackedAsync(
        VistaraDbContext context,
        AtomicQuotaReservation command,
        Guid? uploadSessionId,
        CancellationToken cancellationToken)
    {
        TenantKey tenantId = command.TenantId.Value;
        QuotaReservationRow? existing = await context.QuotaReservations
            .SingleOrDefaultAsync(
                row =>
                    row.TenantId == tenantId &&
                    row.IdempotencyKey == command.IdempotencyKey,
                cancellationToken);
        QuotaUsageRow usage = await GetOrCreateUsageAsync(
            context,
            tenantId,
            cancellationToken);
        if (existing is not null)
        {
            QuotaUsageSnapshot existingSnapshot = ToSnapshot(usage);
            return string.Equals(
                    existing.RequestFingerprint,
                    command.RequestFingerprint,
                    StringComparison.Ordinal)
                ? QuotaStoreReserveResult.Existing(
                    ToDomain(existing),
                    existingSnapshot)
                : QuotaStoreReserveResult.DuplicateKey(existingSnapshot);
        }

        if (usage.Version != command.ExpectedUsageVersion)
        {
            return QuotaStoreReserveResult.VersionConflict(ToSnapshot(usage));
        }

        await ExpireTrackedAsync(context, usage, command.NowUtc, cancellationToken);
        QuotaUsageSnapshot snapshot = ToSnapshot(usage);
        QuotaAmounts total;
        try
        {
            total = snapshot.TotalChecked();
        }
        catch (OverflowException)
        {
            return QuotaStoreReserveResult.LimitExceeded(snapshot);
        }

        if (!command.Limits.Allows(total, command.Amount))
        {
            return QuotaStoreReserveResult.LimitExceeded(snapshot);
        }

        QuotaReservation reservation = QuotaReservation.Create(
            command.ReservationId,
            command.TenantId,
            command.IdempotencyKey,
            command.RequestFingerprint,
            command.Amount,
            command.NowUtc,
            command.ExpiresAtUtc);
        context.QuotaReservations.Add(new QuotaReservationRow
        {
            Id = reservation.Id,
            TenantId = tenantId,
            UploadSessionId = uploadSessionId,
            IdempotencyKey = reservation.IdempotencyKey,
            RequestFingerprint = reservation.RequestFingerprint,
            ReservedUploads = reservation.Amount.Uploads,
            ReservedBytes = reservation.Amount.Bytes,
            ReservedObjects = reservation.Amount.Objects,
            ReservedComputeUnits = reservation.Amount.Transformations,
            ReservedJobs = reservation.Amount.Jobs,
            ReservedBudgetUnits = reservation.Amount.BudgetUnits,
            State = reservation.State.ToString(),
            CreatedAtUtc = reservation.CreatedAtUtc,
            ExpiresAtUtc = reservation.ExpiresAtUtc,
            UpdatedAtUtc = reservation.CreatedAtUtc,
            Version = reservation.Version,
        });
        AddReserved(usage, command.Amount);
        usage.Version = checked(usage.Version + 1);
        return QuotaStoreReserveResult.Reserved(reservation, ToSnapshot(usage));
    }

    internal static async ValueTask<QuotaStoreTransitionResult> TransitionTrackedAsync(
        VistaraDbContext context,
        AtomicQuotaTransition command,
        Guid? consumedByOperationId,
        CancellationToken cancellationToken)
    {
        QuotaReservationRow? row = await context.QuotaReservations
            .SingleOrDefaultAsync(
                candidate => candidate.Id == command.ReservationId,
                cancellationToken);
        if (row is null)
        {
            return QuotaStoreTransitionResult.NotFound();
        }

        QuotaUsageRow usage = await GetOrCreateUsageAsync(
            context,
            row.TenantId,
            cancellationToken);
        QuotaReservation current = ToDomain(row);
        QuotaUsageSnapshot snapshot = ToSnapshot(usage);
        if (current.State == command.TargetState)
        {
            return QuotaStoreTransitionResult.Existing(current, snapshot);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return QuotaStoreTransitionResult.VersionConflict(current, snapshot);
        }

        if (current.State != QuotaReservationState.Reserved ||
            (command.TargetState == QuotaReservationState.Expired &&
             command.NowUtc < current.ExpiresAtUtc) ||
            (command.TargetState == QuotaReservationState.Consumed &&
             command.NowUtc >= current.ExpiresAtUtc))
        {
            return QuotaStoreTransitionResult.InvalidState(current, snapshot);
        }

        QuotaReservation changed;
        try
        {
            changed = current.Transition(command.TargetState);
        }
        catch (InvalidOperationException)
        {
            return QuotaStoreTransitionResult.InvalidState(current, snapshot);
        }

        SubtractReserved(usage, current.Amount);
        if (command.TargetState == QuotaReservationState.Consumed)
        {
            AddCommitted(usage, current.Amount);
            row.ConsumedByOperationId = consumedByOperationId;
        }

        usage.Version = checked(usage.Version + 1);
        row.State = changed.State.ToString();
        row.UpdatedAtUtc = command.NowUtc;
        row.Version = changed.Version;
        context.Entry(row).Property(item => item.Version).OriginalValue =
            command.ExpectedVersion;
        return QuotaStoreTransitionResult.Transitioned(changed, ToSnapshot(usage));
    }

    internal static async ValueTask<QuotaUsageSnapshot> ReadSnapshotAsync(
        VistaraDbContext context,
        CancellationToken cancellationToken)
    {
        TenantKey tenantId = context.TenantId;
        QuotaUsageRow? row = await context.QuotaUsage
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId,
                cancellationToken);
        return row is null
            ? new QuotaUsageSnapshot(QuotaAmounts.Zero, QuotaAmounts.Zero, 0)
            : ToSnapshot(row);
    }

    internal static QuotaReservation ToDomain(QuotaReservationRow row)
    {
        var reservation = QuotaReservation.Create(
            row.Id,
            new Vistara.Domain.Tenancy.TenantId(row.TenantId),
            row.IdempotencyKey,
            row.RequestFingerprint,
            Amount(row),
            row.CreatedAtUtc,
            row.ExpiresAtUtc);
        if (!Enum.TryParse(row.State, out QuotaReservationState state))
        {
            throw new InvalidOperationException("The persisted quota state is invalid.");
        }

        return state == QuotaReservationState.Reserved
            ? reservation
            : reservation.Transition(state);
    }

    internal static QuotaUsageSnapshot ToSnapshot(QuotaUsageRow row) =>
        new(
            new QuotaAmounts(
                row.CommittedUploads,
                row.CommittedBytes,
                row.CommittedObjects,
                row.CommittedComputeUnits,
                row.CommittedJobs,
                row.CommittedBudgetUnits),
            new QuotaAmounts(
                row.ReservedUploads,
                row.ReservedBytes,
                row.ReservedObjects,
                row.ReservedComputeUnits,
                row.ReservedJobs,
                row.ReservedBudgetUnits),
            row.Version);

    private static async ValueTask<QuotaUsageRow> GetOrCreateUsageAsync(
        VistaraDbContext context,
        TenantKey tenantId,
        CancellationToken cancellationToken)
    {
        QuotaUsageRow? usage = await context.QuotaUsage
            .SingleOrDefaultAsync(
                row => row.TenantId == tenantId,
                cancellationToken);
        if (usage is not null)
        {
            return usage;
        }

        QuotaReservationRow[] reservations = await context.QuotaReservations
            .AsNoTracking()
            .Where(row =>
                row.TenantId == tenantId &&
                row.State == nameof(QuotaReservationState.Reserved))
            .ToArrayAsync(cancellationToken);
        QuotaAmounts reserved = Sum(reservations.Select(Amount));
        usage = new QuotaUsageRow
        {
            TenantId = tenantId,
            ReservedUploads = reserved.Uploads,
            ReservedBytes = reserved.Bytes,
            ReservedObjects = reserved.Objects,
            ReservedComputeUnits = reserved.Transformations,
            ReservedJobs = reserved.Jobs,
            ReservedBudgetUnits = reserved.BudgetUnits,
            Version = 0,
        };
        context.QuotaUsage.Add(usage);
        return usage;
    }

    private static async ValueTask ExpireTrackedAsync(
        VistaraDbContext context,
        QuotaUsageRow usage,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        QuotaReservationRow[] expired = await context.QuotaReservations
            .Where(row =>
                row.State == nameof(QuotaReservationState.Reserved) &&
                row.ExpiresAtUtc <= nowUtc)
            .ToArrayAsync(cancellationToken);
        foreach (QuotaReservationRow row in expired)
        {
            SubtractReserved(usage, Amount(row));
            row.State = nameof(QuotaReservationState.Expired);
            row.UpdatedAtUtc = nowUtc;
            row.Version = checked(row.Version + 1);
            usage.Version = checked(usage.Version + 1);
        }
    }

    private static QuotaAmounts Amount(QuotaReservationRow row) =>
        new(
            row.ReservedUploads,
            row.ReservedBytes,
            row.ReservedObjects,
            row.ReservedComputeUnits,
            row.ReservedJobs,
            row.ReservedBudgetUnits);

    private static QuotaAmounts Sum(IEnumerable<QuotaAmounts> values)
    {
        QuotaAmounts total = QuotaAmounts.Zero;
        foreach (QuotaAmounts value in values)
        {
            total = total.AddChecked(value);
        }

        return total;
    }

    private static void AddReserved(QuotaUsageRow row, QuotaAmounts amount)
    {
        row.ReservedUploads = checked(row.ReservedUploads + amount.Uploads);
        row.ReservedBytes = checked(row.ReservedBytes + amount.Bytes);
        row.ReservedObjects = checked(row.ReservedObjects + amount.Objects);
        row.ReservedComputeUnits =
            checked(row.ReservedComputeUnits + amount.Transformations);
        row.ReservedJobs = checked(row.ReservedJobs + amount.Jobs);
        row.ReservedBudgetUnits =
            checked(row.ReservedBudgetUnits + amount.BudgetUnits);
    }

    private static void SubtractReserved(QuotaUsageRow row, QuotaAmounts amount)
    {
        row.ReservedUploads = checked(row.ReservedUploads - amount.Uploads);
        row.ReservedBytes = checked(row.ReservedBytes - amount.Bytes);
        row.ReservedObjects = checked(row.ReservedObjects - amount.Objects);
        row.ReservedComputeUnits =
            checked(row.ReservedComputeUnits - amount.Transformations);
        row.ReservedJobs = checked(row.ReservedJobs - amount.Jobs);
        row.ReservedBudgetUnits =
            checked(row.ReservedBudgetUnits - amount.BudgetUnits);
    }

    private static void AddCommitted(QuotaUsageRow row, QuotaAmounts amount)
    {
        row.CommittedUploads = checked(row.CommittedUploads + amount.Uploads);
        row.CommittedBytes = checked(row.CommittedBytes + amount.Bytes);
        row.CommittedObjects = checked(row.CommittedObjects + amount.Objects);
        row.CommittedComputeUnits =
            checked(row.CommittedComputeUnits + amount.Transformations);
        row.CommittedJobs = checked(row.CommittedJobs + amount.Jobs);
        row.CommittedBudgetUnits =
            checked(row.CommittedBudgetUnits + amount.BudgetUnits);
    }
}
