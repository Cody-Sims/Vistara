using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Security;
using Vistara.Application.Common;
using Xunit;

namespace Vistara.IntegrationTests.RateLimits;

/// <summary>
/// A deployment has two request ceilings: the in-process framework limiter in
/// the security composition, and the persisted counter in the platform
/// composition. Both partition on the same peer address, so raising one alone
/// changes nothing - the other becomes the binding constraint.
///
/// A deployment therefore has to say which kind of peer it has. SharedIngress
/// means every request arrives from the same ingress and each bucket is a
/// ceiling for the whole deployment; ForwardedClient means the peer is the
/// client, either directly or after forwarded-header processing behind a
/// trusted proxy. The declaration is checked against the rest of the
/// configuration at startup, so a deployment cannot claim one and be
/// configured for the other.
/// </summary>
public sealed class HostedRateLimitCouplingTests
{
    /// <summary>
    /// The shipped configuration says nothing about partitioning and is
    /// unaffected: a Compose deployment keeps 300, 30, 120 and 600 a minute.
    /// </summary>
    [Fact]
    public void An_unconfigured_deployment_needs_no_declaration()
    {
        using ServiceProvider services = Compose([]);

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(PlatformRateLimitPartitionMode.ForwardedClient, options.Mode);
        Assert.Equal(300, options.Api);
        Assert.Equal(30, options.Events);
        Assert.Equal(120, options.Delivery);
        Assert.Equal(600, options.Media);
    }

    /// <summary>
    /// A Compose deployment that trusts its reverse proxy is still per-client
    /// after forwarded-header processing, and still needs no declaration.
    /// </summary>
    [Fact]
    public void A_trusted_proxy_deployment_needs_no_declaration()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Security:Proxy:KnownProxies:0"] = "10.0.0.8",
            ["Security:Proxy:KnownNetworks:0"] = "10.1.0.0/16",
        });

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(PlatformRateLimitPartitionMode.ForwardedClient, options.Mode);
        Assert.Equal(300, options.Api);
    }

    /// <summary>
    /// Raising a bucket is the moment the deployment has to say whose requests
    /// the bucket counts. Without that, hosted values would silently become a
    /// per-client allowance of thousands of requests a minute.
    /// </summary>
    [Theory]
    [InlineData("Platform:RateLimits:Api", "6000")]
    [InlineData("Platform:RateLimits:Events", "600")]
    [InlineData("Platform:RateLimits:Delivery", "6000")]
    [InlineData("Platform:RateLimits:Media", "6000")]
    public void Raising_a_bucket_without_declaring_a_partition_fails_the_host(
        string key,
        string value)
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            [key] = value,
        });

        AssertRejects(services, "PartitionMode");
    }

    /// <summary>
    /// Lowering a bucket is always safe and never needs a declaration.
    /// </summary>
    [Fact]
    public void Lowering_a_bucket_needs_no_declaration()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Api"] = "60",
        });

        Assert.Equal(60, Resolve(services).Api);
    }

    /// <summary>
    /// A bucket is raised by shortening the window as surely as by raising the
    /// number. Three hundred requests every three seconds is twenty times the
    /// rate this deployment ships with, and it needs the same declaration.
    /// </summary>
    [Theory]
    [InlineData("Platform:RateLimits:Api", "300", "00:00:03")]
    [InlineData("Platform:RateLimits:Media", "600", "00:00:01")]
    [InlineData("Platform:RateLimits:Events", "30", "00:00:30")]
    public void Raising_a_rate_by_shortening_the_window_needs_a_declaration(
        string key,
        string limit,
        string window)
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = window,
            [key] = limit,
        });

        AssertRejects(services, "PartitionMode");
    }

    /// <summary>
    /// A shorter window with nothing else changed raises every bucket, so it
    /// needs the declaration too.
    /// </summary>
    [Fact]
    public void A_shorter_window_alone_needs_a_declaration()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = "00:00:30",
        });

        AssertRejects(services, "PartitionMode");
    }

    /// <summary>
    /// The same rate expressed over a different window is the same rate. A
    /// deployment that restates its shipped limits has not raised anything.
    /// </summary>
    [Fact]
    public void Restating_the_shipped_rate_over_a_longer_window_is_not_a_raise()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = "00:02:00",
            ["Platform:RateLimits:Api"] = "600",
            ["Platform:RateLimits:Events"] = "60",
            ["Platform:RateLimits:Delivery"] = "240",
            ["Platform:RateLimits:Media"] = "1200",
        });

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(TimeSpan.FromMinutes(2), options.Window);
        Assert.Equal(600, options.Api);
        Assert.Equal(1200, options.Media);
    }

    /// <summary>
    /// A longer window with the shipped numbers is a lower rate, and lowering
    /// is always safe.
    /// </summary>
    [Fact]
    public void A_longer_window_with_the_shipped_limits_is_not_a_raise()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = "00:05:00",
        });

        Assert.Equal(TimeSpan.FromMinutes(5), Resolve(services).Window);
    }

    /// <summary>
    /// So is a shorter window whose numbers come down with it.
    /// </summary>
    [Fact]
    public void A_shorter_window_with_lower_numbers_is_not_a_raise()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = "00:00:30",
            ["Platform:RateLimits:Api"] = "150",
            ["Platform:RateLimits:Events"] = "15",
            ["Platform:RateLimits:Delivery"] = "60",
            ["Platform:RateLimits:Media"] = "100",
        });

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(150, options.Api);
        Assert.Equal(100, options.Media);
    }

    /// <summary>
    /// The comparison is a rate, so it multiplies a limit by a window. Both
    /// ends of what a deployment can configure have to answer, not overflow:
    /// the largest limit over the shortest window, and a window so long that
    /// it is refused for being one.
    /// </summary>
    [Fact]
    public void The_highest_configurable_rate_is_answered_not_overflowed()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:PartitionMode"] = "ForwardedClient",
            ["Platform:RateLimits:Window"] = "00:00:01",
            ["Platform:RateLimits:Api"] = "1000000",
            ["Platform:RateLimits:Events"] = "1000000",
            ["Platform:RateLimits:Delivery"] = "1000000",
            ["Platform:RateLimits:Media"] = "1000000",
        });

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(TimeSpan.FromSeconds(1), options.Window);
        Assert.Equal(1_000_000, options.Api);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ForwardedClient")]
    [InlineData("SharedIngress")]
    public void A_window_beyond_every_bound_fails_the_host_without_overflowing(
        string? mode)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Platform:RateLimits:Window"] = "10675199.02:48:05",
            ["Platform:RateLimits:Api"] = "1000000",
            ["Platform:RateLimits:Media"] = "1000000",
        };
        if (mode is not null)
        {
            settings["Platform:RateLimits:PartitionMode"] = mode;
        }

        using ServiceProvider services = Compose(settings);

        AssertRejects(services, "Window");
    }

    [Theory]
    [InlineData("shared-ingress")]
    [InlineData("Shared")]
    [InlineData("PerClient")]
    [InlineData("")]
    [InlineData("0")]
    public void A_partition_that_is_not_one_of_the_two_fails_the_host(string value)
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:PartitionMode"] = value,
        });

        string failures = AssertRejects(services, "PartitionMode");
        Assert.Contains("SharedIngress", failures, StringComparison.Ordinal);
        Assert.Contains("ForwardedClient", failures, StringComparison.Ordinal);
        if (value.Length > 0)
        {
            Assert.DoesNotContain(
                value,
                failures.Replace("SharedIngress", string.Empty, StringComparison.Ordinal)
                    .Replace("ForwardedClient", string.Empty, StringComparison.Ordinal),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A deployment that trusts a proxy gets a per-client peer, so it cannot
    /// also be a deployment whose buckets are one shared ceiling. Allowing the
    /// claim would hand every client the shared allowance.
    /// </summary>
    [Theory]
    [InlineData("Security:Proxy:KnownProxies:0", "10.0.0.8")]
    [InlineData("Security:Proxy:KnownNetworks:0", "10.1.0.0/16")]
    public void A_shared_ingress_that_trusts_a_proxy_fails_the_host(
        string key,
        string value)
    {
        using ServiceProvider services = Compose(Hosted(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            [key] = value,
        }));

        string failures = AssertRejects(services, "PartitionMode");
        Assert.Contains("Security:Proxy", failures, StringComparison.Ordinal);
    }

    /// <summary>
    /// Behind a shared ingress the framework limiter counts the same peer, so
    /// a persisted bucket it cannot admit is a ceiling that will never be
    /// reached. Raising one without the other is the misconfiguration that
    /// made the deployment unusable, and it now fails the host.
    /// </summary>
    [Theory]
    [InlineData("Platform:RateLimits:Api")]
    [InlineData("Platform:RateLimits:Events")]
    [InlineData("Platform:RateLimits:Delivery")]
    public void A_shared_ingress_bucket_above_the_framework_limit_fails_the_host(
        string key)
    {
        Dictionary<string, string?> settings = Hosted([]);
        settings["Security:Limits:RequestsPerWindow"] = "300";
        settings[key] = "6000";

        using ServiceProvider services = Compose(settings);

        string failures = AssertRejects(services, "Security:Limits");
        Assert.Contains(
            key[(key.LastIndexOf(':') + 1)..],
            failures,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The window is part of the comparison: the same permit count over a
    /// longer framework window is a lower rate.
    /// </summary>
    [Fact]
    public void A_shared_ingress_framework_window_that_is_longer_fails_the_host()
    {
        Dictionary<string, string?> settings = Hosted([]);
        settings["Security:Limits:RateLimitWindow"] = "00:10:00";

        using ServiceProvider services = Compose(settings);

        AssertRejects(services, "Security:Limits");
    }

    /// <summary>
    /// Media is not one of the paths the framework limiter governs, so it is
    /// not compared against it. Saying otherwise would demand a framework
    /// limit for a ceiling the framework never applies.
    /// </summary>
    [Fact]
    public void A_media_bucket_is_not_compared_with_the_framework_limit()
    {
        Dictionary<string, string?> settings = Hosted([]);
        settings["Security:Limits:RequestsPerWindow"] = "6000";
        settings["Platform:RateLimits:Media"] = "60000";

        using ServiceProvider services = Compose(settings);

        Assert.Equal(60000, Resolve(services).Media);
    }

    /// <summary>
    /// The whole hosted handoff has to be a configuration a host accepts, and
    /// it has to declare a shared ingress: the values are a deployment-wide
    /// ceiling, not a per-client budget.
    /// </summary>
    [Fact]
    public void The_hosted_profile_composes_as_a_shared_ceiling()
    {
        using ServiceProvider services = Compose(Hosted([]));

        PlatformRateLimitOptions options = Resolve(services);
        SecurityLimitOptions framework = services
            .GetRequiredService<IOptions<VistaraSecurityOptions>>()
            .Value
            .Limits;

        Assert.Equal(PlatformRateLimitPartitionMode.SharedIngress, options.Mode);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
        Assert.Equal(6000, options.Api);
        Assert.Equal(600, options.Events);
        Assert.Equal(6000, options.Delivery);
        Assert.Equal(6000, options.Media);
        Assert.Equal(6000, framework.RequestsPerWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), framework.RateLimitWindow);
    }

    /// <summary>
    /// The handoff is exactly what a hosted deployment sets, in the form it
    /// sets it in. Both ceilings are in it, and nothing in it trusts a proxy.
    /// </summary>
    [Fact]
    public void The_hosted_profile_names_both_ceilings_and_trusts_no_proxy()
    {
        IReadOnlyDictionary<string, string> configuration =
            PlatformRateLimitHostedProfile.Configuration;

        Assert.Equal("SharedIngress", configuration["Platform:RateLimits:PartitionMode"]);
        Assert.Equal("00:01:00", configuration["Platform:RateLimits:Window"]);
        Assert.Equal("6000", configuration["Platform:RateLimits:Api"]);
        Assert.Equal("600", configuration["Platform:RateLimits:Events"]);
        Assert.Equal("6000", configuration["Platform:RateLimits:Delivery"]);
        Assert.Equal("6000", configuration["Platform:RateLimits:Media"]);
        Assert.Equal("6000", configuration["Security:Limits:RequestsPerWindow"]);
        Assert.Equal("00:01:00", configuration["Security:Limits:RateLimitWindow"]);
        Assert.DoesNotContain(
            configuration.Keys,
            static key => key.StartsWith("Security:Proxy", StringComparison.Ordinal));

        foreach ((string key, string value) in configuration)
        {
            Assert.Equal(
                value,
                PlatformRateLimitHostedProfile.EnvironmentVariables[
                    key.Replace(":", "__", StringComparison.Ordinal)]);
        }

        Assert.Equal(
            configuration.Count,
            PlatformRateLimitHostedProfile.EnvironmentVariables.Count);
    }

    /// <summary>
    /// A per-client deployment may raise its buckets, but it has to raise the
    /// framework limiter as deliberately as the persisted one - the hosted
    /// values are not a default anyone falls into.
    /// </summary>
    [Fact]
    public void A_forwarded_client_deployment_may_raise_its_own_buckets()
    {
        using ServiceProvider services = Compose(new Dictionary<string, string?>(
            StringComparer.Ordinal)
        {
            ["Platform:RateLimits:PartitionMode"] = "ForwardedClient",
            ["Platform:RateLimits:Api"] = "1200",
            ["Security:Proxy:KnownProxies:0"] = "10.0.0.8",
        });

        PlatformRateLimitOptions options = Resolve(services);

        Assert.Equal(PlatformRateLimitPartitionMode.ForwardedClient, options.Mode);
        Assert.Equal(1200, options.Api);
    }

    private static string AssertRejects(ServiceProvider services, string expected)
    {
        OptionsValidationException error =
            Assert.Throws<OptionsValidationException>(() => Resolve(services));
        string failures = string.Join(" ", error.Failures);
        Assert.Contains(
            $"{PlatformRateLimitOptions.SectionName} is invalid",
            failures,
            StringComparison.Ordinal);
        Assert.Contains(expected, failures, StringComparison.Ordinal);
        return failures;
    }

    private static Dictionary<string, string?> Hosted(
        Dictionary<string, string?> settings)
    {
        foreach ((string key, string value) in
            PlatformRateLimitHostedProfile.Configuration)
        {
            settings.TryAdd(key, value);
        }

        return settings;
    }

    private static PlatformRateLimitOptions Resolve(ServiceProvider services) =>
        services.GetRequiredService<IOptions<PlatformRateLimitOptions>>().Value;

    private static ServiceProvider Compose(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddVistaraApiSecurity(configuration, new TestEnvironment());
        services.AddVistaraApiPlatform(configuration);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Vistara.IntegrationTests";
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = Environments.Production;
    }
}
