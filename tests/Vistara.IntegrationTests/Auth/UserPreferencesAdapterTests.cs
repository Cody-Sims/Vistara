using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Exercises the shipped preference adapter over real persistence, including
/// validation, merge-patch semantics, and version concurrency.
/// </summary>
public sealed class UserPreferencesAdapterTests
{
    [Fact]
    public async Task An_account_without_a_document_reads_the_defaults_at_version_zero()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        UserPreferencesView view = await ReadAsync(harness, owner.UserId);

        Assert.Equal("comfortable", view.Density);
        Assert.False(view.ReducedMotion);
        Assert.False(view.ScreenReaderPagedMode);
        Assert.Null(view.Locale);
        Assert.Null(view.TimeZone);
        Assert.Equal(0, view.Version);
    }

    [Fact]
    public async Task The_first_patch_creates_the_document_at_version_one()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<UserPreferencesView> updated = await PatchAsync(
            harness,
            owner.UserId,
            new UserPreferencesPatch(
                "compact",
                true,
                null,
                PatchValue.Of<string?>("en-GB"),
                PatchValue.Of<string?>("Europe/Berlin")),
            0);

        Assert.True(updated.TryGetValue(out UserPreferencesView? view));
        Assert.Equal("compact", view.Density);
        Assert.True(view.ReducedMotion);
        Assert.False(view.ScreenReaderPagedMode);
        Assert.Equal("en-GB", view.Locale);
        Assert.Equal("Europe/Berlin", view.TimeZone);
        Assert.Equal(1, view.Version);
        Assert.Equal(1, (await ReadAsync(harness, owner.UserId)).Version);
    }

    [Fact]
    public async Task An_absent_member_is_unchanged_and_an_explicit_null_clears_it()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchAsync(
            harness,
            owner.UserId,
            new UserPreferencesPatch(
                "compact",
                true,
                true,
                PatchValue.Of<string?>("en-GB"),
                PatchValue.Of<string?>("Europe/Berlin")),
            0);

        Result<UserPreferencesView> updated = await PatchAsync(
            harness,
            owner.UserId,
            new UserPreferencesPatch(
                null,
                null,
                null,
                PatchValue.Absent<string?>(),
                PatchValue.Of<string?>(null)),
            1);

        Assert.True(updated.TryGetValue(out UserPreferencesView? view));
        Assert.Equal("compact", view.Density);
        Assert.True(view.ReducedMotion);
        Assert.True(view.ScreenReaderPagedMode);
        Assert.Equal("en-GB", view.Locale);
        Assert.Null(view.TimeZone);
        Assert.Equal(2, view.Version);
    }

    [Fact]
    public async Task A_stale_version_is_refused_and_leaves_the_document_intact()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchAsync(
            harness,
            owner.UserId,
            Density("compact"),
            0);

        Result<UserPreferencesView> stale = await PatchAsync(
            harness,
            owner.UserId,
            Density("comfortable"),
            0);

        Assert.True(stale.IsFailure);
        Assert.Equal("preferences.version_conflict", stale.Error!.Code);
        UserPreferencesView current = await ReadAsync(harness, owner.UserId);
        Assert.Equal("compact", current.Density);
        Assert.Equal(1, current.Version);
    }

    [Fact]
    public async Task Concurrent_patches_admit_exactly_one_writer_per_version()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await PatchAsync(harness, owner.UserId, Density("compact"), 0);

        Result<UserPreferencesView>[] results = await Task.WhenAll(
            Task.Run(() => PatchAsync(
                harness,
                owner.UserId,
                Density("comfortable"),
                1).AsTask()),
            Task.Run(() => PatchAsync(
                harness,
                owner.UserId,
                Density("compact"),
                1).AsTask()));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.IsFailure);
        Assert.Equal(2, (await ReadAsync(harness, owner.UserId)).Version);
    }

    [Theory]
    [InlineData("cozy", null, null, "preferences.invalid_density")]
    [InlineData(null, "not a locale", null, "preferences.invalid_locale")]
    [InlineData(null, null, "Mars/Olympus", "preferences.invalid_time_zone")]
    public async Task Invalid_values_are_refused_before_anything_is_stored(
        string? density,
        string? locale,
        string? timeZone,
        string expectedCode)
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<UserPreferencesView> updated = await PatchAsync(
            harness,
            owner.UserId,
            new UserPreferencesPatch(
                density,
                null,
                null,
                locale is null
                    ? PatchValue.Absent<string?>()
                    : PatchValue.Of<string?>(locale),
                timeZone is null
                    ? PatchValue.Absent<string?>()
                    : PatchValue.Of<string?>(timeZone)),
            0);

        Assert.True(updated.IsFailure);
        Assert.Equal(expectedCode, updated.Error!.Code);
        Assert.Equal(ErrorCategory.Validation, updated.Error.Category);
        Assert.Equal(0, (await ReadAsync(harness, owner.UserId)).Version);
    }

    [Fact]
    public async Task Preferences_of_an_unknown_principal_are_never_created()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await harness.ProvisionAsync();

        Result<UserPreferencesView> updated = await PatchAsync(
            harness,
            Guid.CreateVersion7(),
            Density("compact"),
            0);

        Assert.True(updated.IsFailure);
        Assert.Equal("preferences.unknown_user", updated.Error!.Code);
        Assert.Equal(ErrorCategory.NotFound, updated.Error.Category);
    }

    private static UserPreferencesPatch Density(string density) =>
        new(
            density,
            null,
            null,
            PatchValue.Absent<string?>(),
            PatchValue.Absent<string?>());

    private static async ValueTask<UserPreferencesView> ReadAsync(
        AccountSurfaceHarness harness,
        Guid userId)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IUserPreferencesPort>()
            .GetAsync(userId, default);
    }

    private static async ValueTask<Result<UserPreferencesView>> PatchAsync(
        AccountSurfaceHarness harness,
        Guid userId,
        UserPreferencesPatch patch,
        long expectedVersion)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IUserPreferencesPort>()
            .UpdateAsync(userId, patch, expectedVersion, default);
    }
}
