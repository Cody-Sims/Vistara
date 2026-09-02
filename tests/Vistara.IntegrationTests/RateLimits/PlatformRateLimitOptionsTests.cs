using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Security;
using Vistara.Application.Common;
using Xunit;

namespace Vistara.IntegrationTests.RateLimits;

/// <summary>
/// Composition-time behaviour of the persisted rate-limit ceiling.
///
/// Two things are proven here. A deployment that configures nothing keeps the
/// limits the adapter shipped with, so an existing Compose host is unchanged.
/// A deployment that configures something impossible fails to start, because a
/// rate limit that is wrong is only ever discovered as an outage, and the
/// failure names the setting without repeating what the operator wrote.
/// </summary>
public sealed class PlatformRateLimitOptionsTests
{
    [Fact]
    public void An_unconfigured_deployment_keeps_the_limits_it_shipped_with()
    {
        using ServiceProvider services = Compose([]);

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
        Assert.Equal(300, options.Api);
        Assert.Equal(30, options.Events);
        Assert.Equal(120, options.Delivery);
        Assert.Equal(600, options.Media);
    }

    [Fact]
    public void Every_bucket_binds_from_its_exact_configuration_key()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:PartitionMode"] = "ForwardedClient",
            ["Platform:RateLimits:Window"] = "00:05:00",
            ["Platform:RateLimits:Api"] = "1200",
            ["Platform:RateLimits:Events"] = "45",
            ["Platform:RateLimits:Delivery"] = "2400",
            ["Platform:RateLimits:Media"] = "3600",
        });

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(TimeSpan.FromMinutes(5), options.Window);
        Assert.Equal(1200, options.Api);
        Assert.Equal(45, options.Events);
        Assert.Equal(2400, options.Delivery);
        Assert.Equal(3600, options.Media);
    }

    /// <summary>
    /// Raising one bucket must not quietly reset the others, because an
    /// operator fixing the one bucket that failed will configure exactly that
    /// bucket.
    /// </summary>
    [Fact]
    public void A_partial_configuration_leaves_the_other_buckets_alone()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:PartitionMode"] = "SharedIngress",
            ["Platform:RateLimits:Events"] = "600",
            ["Security:Limits:RequestsPerWindow"] = "600",
        });

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(600, options.Events);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
        Assert.Equal(300, options.Api);
        Assert.Equal(120, options.Delivery);
        Assert.Equal(600, options.Media);
    }

    /// <summary>
    /// There is no disabled, zero, or unlimited setting: each of these is a
    /// way of spelling "no limit", and each fails the host.
    /// </summary>
    [Theory]
    [InlineData("Platform:RateLimits:Api", "0")]
    [InlineData("Platform:RateLimits:Api", "-1")]
    [InlineData("Platform:RateLimits:Api", "1000001")]
    [InlineData("Platform:RateLimits:Api", "2147483647")]
    [InlineData("Platform:RateLimits:Events", "0")]
    [InlineData("Platform:RateLimits:Events", "-5")]
    [InlineData("Platform:RateLimits:Events", "1000001")]
    [InlineData("Platform:RateLimits:Delivery", "0")]
    [InlineData("Platform:RateLimits:Delivery", "1000001")]
    [InlineData("Platform:RateLimits:Media", "0")]
    [InlineData("Platform:RateLimits:Media", "1000001")]
    [InlineData("Platform:RateLimits:Window", "00:00:00")]
    [InlineData("Platform:RateLimits:Window", "-00:01:00")]
    [InlineData("Platform:RateLimits:Window", "00:00:00.500")]
    [InlineData("Platform:RateLimits:Window", "01:00:01")]
    [InlineData("Platform:RateLimits:Window", "1.00:00:00")]
    public void A_limit_that_is_not_a_limit_fails_the_host(string key, string value)
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            [key] = value,
        });

        OptionsValidationException error =
            Assert.Throws<OptionsValidationException>(() => Resolve(services));
        string failures = string.Join(" ", error.Failures);
        Assert.Contains(
            $"{PlatformRateLimitOptions.SectionName} is invalid",
            failures,
            StringComparison.Ordinal);
        Assert.Contains(
            key[(key.LastIndexOf(':') + 1)..],
            failures,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Several impossible settings report together, so an operator does not
    /// discover them one restart at a time.
    /// </summary>
    [Fact]
    public void Every_impossible_setting_is_reported_at_once()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = "12:00:00",
            ["Platform:RateLimits:Api"] = "0",
            ["Platform:RateLimits:Events"] = "-1",
            ["Platform:RateLimits:Delivery"] = "1000001",
            ["Platform:RateLimits:Media"] = "0",
        });

        OptionsValidationException error =
            Assert.Throws<OptionsValidationException>(() => Resolve(services));

        Assert.Equal(5, error.Failures.Count());
    }

    /// <summary>
    /// A setting that cannot be read at all is a startup failure too. A bare
    /// number is included on purpose: the configuration binder reads
    /// <c>60</c> as sixty days, which is a rate limit that looks configured
    /// and never resets.
    /// </summary>
    [Theory]
    [InlineData("Platform:RateLimits:Window", "sixty-seconds")]
    [InlineData("Platform:RateLimits:Window", "60")]
    [InlineData("Platform:RateLimits:Window", "")]
    [InlineData("Platform:RateLimits:Api", "lots")]
    [InlineData("Platform:RateLimits:Api", "")]
    [InlineData("Platform:RateLimits:Api", "6000.5")]
    [InlineData("Platform:RateLimits:Events", "unlimited")]
    [InlineData("Platform:RateLimits:Delivery", "none")]
    [InlineData("Platform:RateLimits:Media", "  ")]
    public void A_setting_that_cannot_be_read_fails_the_host(
        string key,
        string value)
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            [key] = value,
        });

        OptionsValidationException error =
            Assert.Throws<OptionsValidationException>(() => Resolve(services));
        string failures = string.Join(" ", error.Failures);

        Assert.Contains(
            $"{PlatformRateLimitOptions.SectionName} is invalid",
            failures,
            StringComparison.Ordinal);
        Assert.Contains(
            key[(key.LastIndexOf(':') + 1)..],
            failures,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected deployment reports the setting and what it accepts. It never
    /// repeats the configured value, so failing to start cannot copy a
    /// deployment's own configuration into a log.
    /// </summary>
    [Fact]
    public void A_rejected_configuration_never_repeats_what_was_configured()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = "the-quick-brown-fox",
            ["Platform:RateLimits:Api"] = "987654321",
            ["Platform:RateLimits:Events"] = "jumps-over",
        });

        OptionsValidationException error =
            Assert.Throws<OptionsValidationException>(() => Resolve(services));
        string failures = string.Join(" ", error.Failures);

        Assert.DoesNotContain(
            "the-quick-brown-fox",
            failures,
            StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", failures, StringComparison.Ordinal);
        Assert.DoesNotContain("jumps-over", failures, StringComparison.Ordinal);
        Assert.Equal(3, error.Failures.Count());
    }

    /// <summary>
    /// The hosted profile raises the shared ceiling; it does not remove it.
    /// </summary>
    [Fact]
    public void The_hosted_profile_is_still_a_ceiling()
    {
        Assert.InRange(PlatformRateLimitHostedProfile.Events, 1, 1_000_000);
        Assert.InRange(PlatformRateLimitHostedProfile.Api, 1, 1_000_000);
        Assert.InRange(PlatformRateLimitHostedProfile.Delivery, 1, 1_000_000);
        Assert.InRange(PlatformRateLimitHostedProfile.Media, 1, 1_000_000);
        Assert.True(
            PlatformRateLimitHostedProfile.Events <
            PlatformRateLimitHostedProfile.Api);
    }

    private static PlatformRateLimitOptions Resolve(ServiceProvider services) =>
        services.GetRequiredService<IOptions<PlatformRateLimitOptions>>().Value;

    private static ServiceProvider Compose(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        services.Configure<VistaraSecurityOptions>(
            configuration.GetSection(VistaraSecurityOptions.SectionName));
        services.AddVistaraApiPlatform(configuration);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }
}
