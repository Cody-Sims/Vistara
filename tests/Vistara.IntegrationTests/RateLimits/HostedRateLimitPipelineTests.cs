using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.Composition.Security;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Oidc;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Xunit;

namespace Vistara.IntegrationTests.RateLimits;

/// <summary>
/// Both request ceilings through the composed pipeline: forwarded-header
/// processing, the in-process framework limiter, and the persisted counter, in
/// the order a real request meets them.
///
/// The hosted profile has to admit the traffic the shipped limits refused, and
/// it has to do so while treating every request as one shared peer - the
/// property that made the shared ceiling necessary in the first place.
/// </summary>
public sealed class HostedRateLimitPipelineTests
{
    private static readonly IPAddress Ingress = IPAddress.Parse("10.0.0.8");

    /// <summary>
    /// More than the shipped 300 a minute from one peer, all admitted. Under
    /// the shipped configuration the same traffic is refused, so this is the
    /// outage the profile exists to fix.
    /// </summary>
    [Fact]
    public async Task A_hosted_deployment_admits_more_than_the_shipped_ceiling()
    {
        await using RateLimitPipeline pipeline = await RateLimitPipeline.CreateAsync(
            HostedSettings());

        for (int request = 0; request < 400; request++)
        {
            (int status, _) = await pipeline.SendAsync(
                "/api/v1/probe",
                forwardedFor: $"198.51.100.{request % 200}");
            Assert.Equal(StatusCodes.Status200OK, status);
        }
    }

    /// <summary>
    /// The same traffic under the shipped configuration, which is the outage:
    /// one ingress peer, so the deployment-wide ceiling is 300 a minute for
    /// everyone behind it.
    /// </summary>
    [Fact]
    public async Task The_shipped_ceiling_refuses_the_same_hosted_traffic()
    {
        await using RateLimitPipeline pipeline = await RateLimitPipeline.CreateAsync(
            new Dictionary<string, string?>(StringComparer.Ordinal));

        int admitted = 0;
        int refused = 0;
        for (int request = 0; request < 400; request++)
        {
            (int status, _) = await pipeline.SendAsync(
                "/api/v1/probe",
                forwardedFor: $"198.51.100.{request % 200}");
            if (status == StatusCodes.Status200OK)
            {
                admitted++;
            }
            else
            {
                Assert.Equal(StatusCodes.Status429TooManyRequests, status);
                refused++;
            }
        }

        Assert.Equal(300, admitted);
        Assert.Equal(100, refused);
    }

    /// <summary>
    /// The forwarded header does not partition either ceiling when no proxy is
    /// trusted, so a hosted deployment cannot pretend its shared bucket is
    /// per-client - and a caller cannot mint a bucket by claiming an address.
    /// </summary>
    [Fact]
    public async Task A_hosted_deployment_counts_every_request_as_one_peer()
    {
        Dictionary<string, string?> settings = HostedSettings();
        settings["Platform:RateLimits:Api"] = "3";

        await using RateLimitPipeline pipeline =
            await RateLimitPipeline.CreateAsync(settings);

        for (int request = 0; request < 3; request++)
        {
            Assert.Equal(
                StatusCodes.Status200OK,
                (await pipeline.SendAsync(
                    "/api/v1/probe",
                    forwardedFor: $"198.51.100.{request}")).Status);
        }

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            (await pipeline.SendAsync(
                "/api/v1/probe",
                forwardedFor: "198.51.100.99")).Status);
    }

    /// <summary>
    /// The setup surface is the one anonymous read on the account surface, so
    /// it holds its own throttle behind the pipeline's. That guard is still
    /// there under the hosted profile: with one permit in the bucket the
    /// pipeline admits the request and the endpoint refuses it itself.
    /// </summary>
    [Fact]
    public async Task The_setup_guard_is_still_enforced_by_the_endpoint()
    {
        Dictionary<string, string?> settings = HostedSettings();
        settings["Platform:RateLimits:Api"] = "1";

        await using RateLimitPipeline pipeline =
            await RateLimitPipeline.CreateAsync(settings);

        (int status, string body) = await pipeline.SendAsync(
            "/api/v1/setup",
            forwardedFor: "198.51.100.1");

        Assert.Equal(StatusCodes.Status429TooManyRequests, status);
        Assert.Contains("setup_throttled", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// And under a shared ingress that guard is honestly shared rather than
    /// per-client: claiming a different address does not get a fresh
    /// allowance, because no forwarded header is trusted.
    /// </summary>
    [Fact]
    public async Task The_setup_guard_is_honestly_shared()
    {
        Dictionary<string, string?> settings = HostedSettings();
        settings["Platform:RateLimits:Api"] = "2";

        await using RateLimitPipeline pipeline =
            await RateLimitPipeline.CreateAsync(settings);

        (int first, string body) = await pipeline.SendAsync(
            "/api/v1/setup",
            forwardedFor: "198.51.100.1");
        Assert.Equal(StatusCodes.Status200OK, first);
        Assert.Contains("\"available\":false", body, StringComparison.Ordinal);

        (int second, _) = await pipeline.SendAsync(
            "/api/v1/setup",
            forwardedFor: "198.51.100.3");

        Assert.Equal(StatusCodes.Status429TooManyRequests, second);
    }

    /// <summary>
    /// The shipped configuration is untouched: 300 a minute, and per-client
    /// once a proxy the deployment trusts has forwarded the client.
    /// </summary>
    [Fact]
    public async Task A_trusted_proxy_deployment_still_counts_each_client()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Security:Proxy:KnownProxies:0"] = Ingress.ToString(),
            ["Platform:RateLimits:Api"] = "2",
        };

        await using RateLimitPipeline pipeline =
            await RateLimitPipeline.CreateAsync(settings);

        Assert.Equal(
            StatusCodes.Status200OK,
            (await pipeline.SendAsync(
                "/api/v1/probe",
                forwardedFor: "198.51.100.1")).Status);
        Assert.Equal(
            StatusCodes.Status200OK,
            (await pipeline.SendAsync(
                "/api/v1/probe",
                forwardedFor: "198.51.100.1")).Status);
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            (await pipeline.SendAsync(
                "/api/v1/probe",
                forwardedFor: "198.51.100.1")).Status);

        Assert.Equal(
            StatusCodes.Status200OK,
            (await pipeline.SendAsync(
                "/api/v1/probe",
                forwardedFor: "198.51.100.2")).Status);
    }

    /// <summary>
    /// A deployment whose two ceilings disagree never serves a request: the
    /// host refuses to start.
    /// </summary>
    [Fact]
    public async Task A_deployment_whose_ceilings_disagree_never_starts()
    {
        Dictionary<string, string?> settings = HostedSettings();
        settings["Security:Limits:RequestsPerWindow"] = "300";

        await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await RateLimitPipeline.CreateAsync(settings));
    }

    /// <summary>
    /// So does one that claims a shared ingress while trusting a proxy.
    /// </summary>
    [Fact]
    public async Task A_shared_ingress_that_trusts_a_proxy_never_starts()
    {
        Dictionary<string, string?> settings = HostedSettings();
        settings["Security:Proxy:KnownProxies:0"] = Ingress.ToString();

        await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await RateLimitPipeline.CreateAsync(settings));
    }

    private static Dictionary<string, string?> HostedSettings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string value) in
            PlatformRateLimitHostedProfile.Configuration)
        {
            settings[key] = value;
        }

        return settings;
    }

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
                $"rate-limit-pipeline-{Guid.NewGuid():N}");
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
            builder.Services.AddSingleton<IFirstOwnerProvisioningPort>(
                new ClosedProvisioning());
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
                Directory.Delete(directory, recursive: true);
                throw;
            }

            app.UseVistaraApiSecurity();
            app.UseVistaraPlatform();
            app.MapGet("/api/v1/probe", () => Results.Text("probe"))
                .AllowAnonymous();
            app.MapGet(
                    "/api/v1/setup",
                    (HttpContext context,
                        IFirstOwnerProvisioningPort provisioning,
                        IOidcProviderCatalog providers,
                        IPlatformRateLimitHook rateLimit,
                        CancellationToken cancellationToken) =>
                        AccountEndpoint.DescribeSetupAsync(
                            context,
                            provisioning,
                            providers,
                            rateLimit,
                            cancellationToken))
                .AllowAnonymous();
            RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
            return new RateLimitPipeline(app, pipeline, directory);
        }

        internal async Task<(int Status, string Body)> SendAsync(
            string path,
            string forwardedFor)
        {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("vistara.example.test");
            context.Request.Path = path;
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Connection.RemoteIpAddress = Ingress;
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
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static Dictionary<string, string?> BaseSettings(
            string databasePath) =>
            new(StringComparer.Ordinal)
            {
                ["Security:Hosts:AllowedHosts:0"] = "vistara.example.test",
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = $"Data Source={databasePath}",
                ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
                ["Platform:Authentication:ApiKeys:Peppers:v1"] =
                    "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=",
            };

        private sealed class ClosedProvisioning : IFirstOwnerProvisioningPort
        {
            public ValueTask<bool> IsAvailableAsync(
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(false);

            public ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
                FirstOwnerProvisioningCommand command,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }
    }
}
