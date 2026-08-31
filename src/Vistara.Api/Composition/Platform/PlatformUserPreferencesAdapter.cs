using System.Globalization;
using Vistara.Api.Features.Account;
using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Persistence.Identity;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Validates and persists account preferences through the tenant-independent
/// identity catalog, applying merge-patch semantics with optimistic
/// concurrency on the document version.
/// </summary>
internal sealed class PlatformUserPreferencesAdapter(
    RelationalUserPreferenceStore store,
    IClock clock) : IUserPreferencesPort
{
    private static readonly string[] Densities = ["comfortable", "compact"];

    public async ValueTask<UserPreferencesView> GetAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        PersistedUserPreferences stored =
            await store.GetAsync(userId, cancellationToken);
        return Map(stored);
    }

    public async ValueTask<Result<UserPreferencesView>> UpdateAsync(
        Guid userId,
        UserPreferencesPatch patch,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        PersistedUserPreferences current =
            await store.GetAsync(userId, cancellationToken);
        if (current.Version != expectedVersion)
        {
            return Result.Failure<UserPreferencesView>(StaleVersion);
        }

        string density = patch.Density ?? current.Density;
        if (!Densities.Contains(density, StringComparer.Ordinal))
        {
            return Result.Failure<UserPreferencesView>(ResultError.Validation(
                "preferences.invalid_density",
                "The density must be either comfortable or compact."));
        }

        string? locale = patch.Locale.IsPresent ? patch.Locale.Value : current.Locale;
        if (locale is not null && !IsValidLocale(locale))
        {
            return Result.Failure<UserPreferencesView>(ResultError.Validation(
                "preferences.invalid_locale",
                "The locale must be a known BCP 47 language tag."));
        }

        string? timeZone = patch.TimeZone.IsPresent
            ? patch.TimeZone.Value
            : current.TimeZone;
        if (timeZone is not null && !IsValidTimeZone(timeZone))
        {
            return Result.Failure<UserPreferencesView>(ResultError.Validation(
                "preferences.invalid_time_zone",
                "The time zone must be a known IANA identifier."));
        }

        var desired = new PersistedUserPreferences(
            density,
            patch.ReducedMotion ?? current.ReducedMotion,
            patch.ScreenReaderPagedMode ?? current.ScreenReaderPagedMode,
            Normalize(locale),
            Normalize(timeZone),
            current.Version);
        UserPreferenceWriteStatus status = await store.SaveAsync(
            userId,
            desired,
            expectedVersion,
            clock.UtcNow,
            cancellationToken);
        return status switch
        {
            UserPreferenceWriteStatus.Applied => Result.Success(
                Map(desired with { Version = expectedVersion + 1 })),
            UserPreferenceWriteStatus.UnknownUser =>
                Result.Failure<UserPreferencesView>(ResultError.NotFound(
                    "preferences.unknown_user",
                    "The current principal no longer exists.")),
            _ => Result.Failure<UserPreferencesView>(StaleVersion),
        };
    }

    internal static bool IsValidLocale(string locale)
    {
        if (locale.Length is 0 or > 35)
        {
            return false;
        }

        try
        {
            return CultureInfo.GetCultureInfo(locale, predefinedOnly: true) is not null;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    internal static bool IsValidTimeZone(string timeZone)
    {
        if (timeZone.Length is 0 or > 64)
        {
            return false;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _);
    }

    private static ResultError StaleVersion => ResultError.Conflict(
        "preferences.version_conflict",
        "The preference document changed since it was read.");

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static UserPreferencesView Map(PersistedUserPreferences stored) =>
        new(
            stored.Density,
            stored.ReducedMotion,
            stored.ScreenReaderPagedMode,
            stored.Locale,
            stored.TimeZone,
            stored.Version);
}
