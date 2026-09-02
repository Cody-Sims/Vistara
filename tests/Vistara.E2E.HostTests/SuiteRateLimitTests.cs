using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.Composition.Security;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Xunit;

namespace Vistara.E2E.HostTests;

/// <summary>
/// The rate limits the end-to-end harness gives the deployments it starts.
///
/// With WebKit signing in again, three browser engines run a full pass one
/// after another through a single loopback address, and every request lands in
/// the same bucket. The shipped per-visitor budget is not sized for that, and
/// a run that exhausted it failed whichever assertion happened to be in flight
/// with a `429` rather than with anything it meant to prove.
///
/// Raising a budget is a claim about two ceilings at once — the persisted
/// buckets and the in-process framework limiter that counts the same peer in
/// front of them — so these cases hold the harness to the whole claim: the
/// values are the shipped hosted profile's rather than invented ones, they
/// satisfy the coupling rather than side-step it, they admit the traffic the
/// suite actually makes, and they still refuse a bucket that is exhausted.
/// </summary>
public sealed partial class SuiteRateLimitTests
{
    /// <summary>
    /// The address every request in an end-to-end run arrives from, which is
    /// the whole reason one bucket has to hold a three-engine pass.
    /// </summary>
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    /// <summary>
    /// Whatever the suite is served over, the settings are the hosted
    /// profile's. The one difference is the declared partition: a loopback run
    /// sits behind no ingress, so it counts per client, and nothing here
    /// trusts a forwarded header that would let a caller mint buckets.
    /// </summary>
    [Fact]
    public void The_suite_settings_are_the_hosted_profile_restated_for_loopback()
    {
        Dictionary<string, string> declared = ReadSuiteRateLimits();

        Assert.Equal(
            PlatformRateLimitHostedProfile.EnvironmentVariables.Keys.Order(),
            declared.Keys.Order());
        Assert.Equal(
            nameof(PlatformRateLimitPartitionMode.ForwardedClient),
            declared["Platform__RateLimits__PartitionMode"]);
        foreach ((string key, string value) in
            PlatformRateLimitHostedProfile.EnvironmentVariables)
        {
            if (key == "Platform__RateLimits__PartitionMode")
            {
                continue;
            }

            Assert.Equal(value, declared[key]);
        }
    }

    /// <summary>
    /// Both ceilings are declared, and the framework one admits every bucket
    /// it governs. A harness that raised the persisted buckets alone would
    /// have a ceiling nothing could ever reach.
    /// </summary>
    [Fact]
    public void The_framework_ceiling_admits_every_bucket_it_governs()
    {
        Dictionary<string, string> declared = ReadSuiteRateLimits();
        TimeSpan bucketWindow = Duration(declared["Platform__RateLimits__Window"]);
        TimeSpan frameworkWindow =
            Duration(declared["Security__Limits__RateLimitWindow"]);
        int framework = Count(declared["Security__Limits__RequestsPerWindow"]);

        // The framework limiter never sees a media request, so the buckets it
        // governs are the ones reached under /api, /v1 and /delivery.
        foreach (string bucket in (string[])
            ["Platform__RateLimits__Api", "Platform__RateLimits__Delivery",
             "Platform__RateLimits__Events"])
        {
            long bucketRate = Count(declared[bucket]) * frameworkWindow.Ticks;
            long frameworkRate = (long)framework * bucketWindow.Ticks;
            Assert.True(
                bucketRate <= frameworkRate,
                $"{bucket} is above the rate the framework limiter admits.");
        }
    }

    /// <summary>
    /// The coupling is satisfied rather than avoided. A loopback run declares
    /// the per-client partition, which the coupling check does not even
    /// examine, so the same settings are validated under a shared ingress too:
    /// the ceilings agree on their own merits, not because of which partition
    /// happens to be declared.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(nameof(PlatformRateLimitPartitionMode.SharedIngress))]
    public async Task The_suite_settings_start_a_deployment(string? partition)
    {
        Dictionary<string, string?> settings = SuiteSettings();
        if (partition is not null)
        {
            settings["Platform:RateLimits:PartitionMode"] = partition;
        }

        await using RateLimitPipeline pipeline =
            await RateLimitPipeline.CreateAsync(settings);

        Assert.Equal(
            StatusCodes.Status200OK,
            (await pipeline.SendAsync("/api/v1/probe")).Status);
    }

    /// <summary>
    /// The traffic a three-engine pass makes, admitted. Both ceilings count
    /// the one peer these requests arrive from, and both governed buckets are
    /// exercised, because the run spends its budget on gallery reads and on
    /// delivering the images they find.
    /// </summary>
    [Fact]
    public async Task The_suite_settings_admit_a_full_browser_matrix_pass()
    {
        await using RateLimitPipeline pipeline =
            await RateLimitPipeline.CreateAsync(SuiteSettings());

        for (int request = 0; request < 200; request++)
        {
            Assert.Equal(
                StatusCodes.Status200OK,
                (await pipeline.SendAsync("/api/v1/probe")).Status);
            Assert.Equal(
                StatusCodes.Status200OK,
                (await pipeline.SendAsync("/delivery/probe")).Status);
        }
    }

    /// <summary>
    /// The same traffic under the shipped budget, which is the failure the
    /// harness settings exist to remove: one peer, so the deployment-wide
    /// ceiling is reached part way through the pass and the rest of the run
    /// fails for a reason it never meant to test.
    /// </summary>
    [Fact]
    public async Task The_shipped_budget_refuses_that_same_pass()
    {
        await using RateLimitPipeline pipeline = await RateLimitPipeline.CreateAsync(
            new Dictionary<string, string?>(StringComparer.Ordinal));

        int refused = 0;
        for (int request = 0; request < 200; request++)
        {
            foreach (string path in (string[])["/api/v1/probe", "/delivery/probe"])
            {
                if ((await pipeline.SendAsync(path)).Status ==
                    StatusCodes.Status429TooManyRequests)
                {
                    refused++;
                }
            }
        }

        Assert.True(refused > 0, "The shipped budget admitted the whole pass.");
    }

    /// <summary>
    /// The limiter is still a limiter. A raised budget is a bigger bucket, not
    /// an absent one, so a bucket the harness exhausts is still refused.
    /// </summary>
    [Fact]
    public async Task An_exhausted_bucket_is_still_refused()
    {
        Dictionary<string, string?> settings = SuiteSettings();
        settings["Platform:RateLimits:Api"] = "3";

        await using RateLimitPipeline pipeline =
            await RateLimitPipeline.CreateAsync(settings);

        for (int request = 0; request < 3; request++)
        {
            Assert.Equal(
                StatusCodes.Status200OK,
                (await pipeline.SendAsync("/api/v1/probe")).Status);
        }

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            (await pipeline.SendAsync("/api/v1/probe")).Status);
    }

    /// <summary>
    /// And a harness that raised the persisted buckets while leaving the
    /// framework ceiling behind never starts, which is what makes declaring
    /// both a requirement rather than a courtesy.
    /// </summary>
    [Fact]
    public async Task Raising_one_ceiling_without_the_other_never_starts()
    {
        Dictionary<string, string?> settings = SuiteSettings();
        settings["Platform:RateLimits:PartitionMode"] =
            nameof(PlatformRateLimitPartitionMode.SharedIngress);
        settings.Remove("Security:Limits:RequestsPerWindow");
        settings.Remove("Security:Limits:RateLimitWindow");

        await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await RateLimitPipeline.CreateAsync(settings));
    }

    /// <summary>
    /// The settings the harness actually passes, read from the harness rather
    /// than restated here, so a value that changed in one place and not the
    /// other is a failing test instead of a surprise in a run.
    /// </summary>
    private static Dictionary<string, string> ReadSuiteRateLimits()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Vistara.E2E",
            "support",
            "deployment.ts"));
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match entry in SuiteRateLimitEntry().Matches(source))
        {
            declared[entry.Groups["key"].Value] = entry.Groups["value"].Value;
        }

        Assert.NotEmpty(declared);
        return declared;
    }

    /// <summary>The same settings as a configuration a deployment binds.</summary>
    private static Dictionary<string, string?> SuiteSettings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string value) in ReadSuiteRateLimits())
        {
            settings[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        return settings;
    }

    private static TimeSpan Duration(string value) =>
        TimeSpan.Parse(value, CultureInfo.InvariantCulture);

    private static int Count(string value) =>
        int.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex(
        @"^\s*(?<key>[A-Za-z][A-Za-z0-9_]*)\s*:\s*'(?<value>[^']*)'\s*,\s*$",
        RegexOptions.Multiline)]
    private static partial Regex SuiteRateLimitEntry();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vistara.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    /// <summary>
    /// Both ceilings through the composed pipeline, in the order a request
    /// meets them: forwarded-header processing, the in-process framework
    /// limiter, and the persisted counter.
    /// </summary>
    private sealed class RateLimitPipeline : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RequestDelegate _pipeline;
        private readonly string _directory;

        private RateLimitPipeline(
            WebApplication app,
            RequestDelegate pipeline,
            string directory)
        {
            _app = app;
            _pipeline = pipeline;
            _directory = directory;
        }

        internal static async Task<RateLimitPipeline> CreateAsync(
            Dictionary<string, string?> overrides)
        {
            string directory = Path.Combine(
                AppContext.BaseDirectory,
                ".artifacts",
                $"suite-rate-limits-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string databasePath = Path.Combine(directory, "pipeline.db");
            await using (VistaraDbContext schema = new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite($"Data Source={databasePath}")
                    .Options,
                new FixedTenantScope(Guid.CreateVersion7())))
            {
                await schema.Database.EnsureCreatedAsync(CancellationToken.None);
            }

            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Configuration.Sources.Clear();
            Dictionary<string, string?> settings = BaseSettings(databasePath);
            foreach ((string key, string? value) in overrides)
            {
                settings[key] = value;
            }

            builder.Configuration.AddInMemoryCollection(settings);
            builder.Logging.ClearProviders();
            builder.Services.AddVistaraApiSecurity(
                builder.Configuration,
                builder.Environment);
            builder.Services.AddVistaraApiRuntime(builder.Configuration);
            builder.Services.AddVistaraApiPlatform(builder.Configuration);
            builder.Services.AddVistaraApiPersistence(builder.Configuration);

            WebApplication app = builder.Build();
            try
            {
                // Both ceilings are validated before a request is served, so a
                // deployment whose configuration disagrees with itself fails
                // here rather than at the first request.
                _ = app.Services
                    .GetRequiredService<IOptions<PlatformRateLimitOptions>>()
                    .Value;
            }
            catch
            {
                await app.DisposeAsync();
                Delete(directory);
                throw;
            }

            app.UseVistaraApiSecurity();
            app.UseVistaraPlatform();
            app.MapGet("/api/v1/probe", () => Results.Text("probe"))
                .AllowAnonymous();
            app.MapGet("/delivery/probe", () => Results.Text("probe"))
                .AllowAnonymous();
            RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
            return new RateLimitPipeline(app, pipeline, directory);
        }

        internal async Task<(int Status, string Body)> SendAsync(string path)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("127.0.0.1");
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = Loopback;
            var body = new MemoryStream();
            context.Response.Body = body;
            await _pipeline(context);
            body.Position = 0;
            return (
                context.Response.StatusCode,
                await new StreamReader(body).ReadToEndAsync(CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Delete(_directory);
        }

        private static void Delete(string directory)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static Dictionary<string, string?> BaseSettings(
            string databasePath) =>
            new(StringComparer.Ordinal)
            {
                ["Security:Hosts:AllowedHosts:0"] = "127.0.0.1",
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = $"Data Source={databasePath}",
                ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
                ["Platform:Authentication:ApiKeys:Peppers:v1"] =
                    "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=",
            };
    }
}
