using Vistara.Domain.Common;

namespace Vistara.Api.Features.Account;

public sealed record UserPreferencesView(
    string Density,
    bool ReducedMotion,
    bool ScreenReaderPagedMode,
    string? Locale,
    string? TimeZone,
    long Version);

/// <summary>
/// A merge patch over the preference document. A property that is
/// <c>null</c> was absent from the request and stays unchanged; a property
/// whose inner value is <c>null</c> clears the stored value.
/// </summary>
public sealed record UserPreferencesPatch(
    string? Density,
    bool? ReducedMotion,
    bool? ScreenReaderPagedMode,
    PatchValue<string?> Locale,
    PatchValue<string?> TimeZone);

/// <summary>Distinguishes an absent member from an explicit JSON null.</summary>
public readonly record struct PatchValue<T>(T Value, bool IsPresent);

public static class PatchValue
{
    public static PatchValue<T> Absent<T>() => default;

    public static PatchValue<T> Of<T>(T value) => new(value, true);
}

public interface IUserPreferencesPort
{
    ValueTask<UserPreferencesView> GetAsync(
        Guid userId,
        CancellationToken cancellationToken);

    ValueTask<Result<UserPreferencesView>> UpdateAsync(
        Guid userId,
        UserPreferencesPatch patch,
        long expectedVersion,
        CancellationToken cancellationToken);
}
