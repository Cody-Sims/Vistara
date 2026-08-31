using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Identity;

/// <summary>Account-level presentation preferences and their version.</summary>
public sealed record PersistedUserPreferences(
    string Density,
    bool ReducedMotion,
    bool ScreenReaderPagedMode,
    string? Locale,
    string? TimeZone,
    long Version)
{
    public const string DefaultDensity = "comfortable";

    /// <summary>
    /// The document a user has before anything is stored. Version zero means
    /// "no stored row yet" and is a valid <c>If-Match</c> value.
    /// </summary>
    public static PersistedUserPreferences Default { get; } =
        new(DefaultDensity, false, false, null, null, 0);
}

public enum UserPreferenceWriteStatus
{
    Applied,
    VersionConflict,
    UnknownUser,
}

/// <summary>
/// Reads and writes account preferences through the tenant-independent
/// identity catalog, so a user keeps one document across every tenant.
/// </summary>
public sealed class RelationalUserPreferenceStore(IdentityCatalogDbContext context)
{
    private readonly IdentityCatalogDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<PersistedUserPreferences> GetAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        UserPreferenceRow? row = await _context.UserPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == userId,
                cancellationToken);
        return row is null
            ? PersistedUserPreferences.Default
            : new PersistedUserPreferences(
                row.Density,
                row.ReducedMotion,
                row.ScreenReaderPagedMode,
                row.Locale,
                row.TimeZone,
                row.Version);
    }

    public async ValueTask<UserPreferenceWriteStatus> SaveAsync(
        Guid userId,
        PersistedUserPreferences desired,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _context.Users
                .AsNoTracking()
                .AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return UserPreferenceWriteStatus.UnknownUser;
        }

        UserPreferenceRow? row = await _context.UserPreferences
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == userId,
                cancellationToken);
        if (row is null)
        {
            if (expectedVersion != 0)
            {
                return UserPreferenceWriteStatus.VersionConflict;
            }

            _context.UserPreferences.Add(new UserPreferenceRow
            {
                UserId = userId,
                Density = desired.Density,
                ReducedMotion = desired.ReducedMotion,
                ScreenReaderPagedMode = desired.ScreenReaderPagedMode,
                Locale = desired.Locale,
                TimeZone = desired.TimeZone,
                UpdatedAtUtc = updatedAtUtc,
                Version = 1,
            });
        }
        else
        {
            if (row.Version != expectedVersion)
            {
                return UserPreferenceWriteStatus.VersionConflict;
            }

            row.Density = desired.Density;
            row.ReducedMotion = desired.ReducedMotion;
            row.ScreenReaderPagedMode = desired.ScreenReaderPagedMode;
            row.Locale = desired.Locale;
            row.TimeZone = desired.TimeZone;
            row.UpdatedAtUtc = updatedAtUtc;
            row.Version = checked(row.Version + 1);
            _context.Entry(row).Property(entry => entry.Version).OriginalValue =
                expectedVersion;
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return UserPreferenceWriteStatus.Applied;
        }
        catch (DbUpdateConcurrencyException)
        {
            return UserPreferenceWriteStatus.VersionConflict;
        }
        catch (DbUpdateException)
        {
            return UserPreferenceWriteStatus.VersionConflict;
        }
    }
}
