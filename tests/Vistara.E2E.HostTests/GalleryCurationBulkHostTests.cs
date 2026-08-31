using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Amazon.Runtime;
using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vistara.Api.Composition.Platform;
using Vistara.Application.Common.Imaging;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.OpenApi.Gallery;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Runtime.Jobs;
using Xunit;
using ApiMedia = Vistara.Api.Composition.Media;
using WorkerMedia = Vistara.Worker.Composition.Media;

namespace Vistara.E2E.HostTests;

/// <summary>
/// Runs the seeded host database through the real API bulk endpoint and the
/// real durable job worker so queued curation work is proven to be applied.
/// </summary>
public sealed class GalleryCurationBulkHostTests
{
    private const string Pepper = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Queued_bulk_favorite_is_applied_by_the_worker_and_completes()
    {
        string testRoot = Path.Combine(
            AppContext.BaseDirectory,
            ".artifacts",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(testRoot, "vistara-bulk.db");
        string mediaRoot = Path.Combine(testRoot, "media");
        string connectionString = $"Data Source={databasePath}";

        try
        {
            await SeedAsync(testRoot, databasePath, mediaRoot);
            SeededTenant tenant = await ReadSeededTenantAsync(
                Path.Combine(testRoot, "state.json"),
                connectionString);

            Guid jobId;
            await using (WebApplication api = BuildApi(connectionString, mediaRoot))
            {
                await api.StartAsync();
                using var client = new HttpClient
                {
                    BaseAddress = new Uri(ResolveAddress(api)),
                };
                client.DefaultRequestHeaders.Add("X-API-Key", tenant.ApiKey);
                client.DefaultRequestHeaders.Add(
                    "Idempotency-Key",
                    $"bulk-favorite-{Guid.CreateVersion7():N}");

                using HttpResponseMessage response = await client.PostAsJsonAsync(
                    "/api/v1/assets/bulk",
                    new
                    {
                        items = tenant.AssetIds
                            .Select(id => new { id, version = 1 })
                            .ToArray(),
                        action = new { kind = "setFavorite", favorite = true },
                    },
                    JsonOptions,
                    CancellationToken.None);

                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                JsonDocument body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(
                        CancellationToken.None));
                jobId = body.RootElement.GetProperty("jobId").GetGuid();
                await api.StopAsync();
            }

            await RunWorkerAsync(connectionString, mediaRoot, testRoot);

            await using VistaraDbContext context =
                CreateContext(connectionString, tenant.TenantId);
            Guid[] favorites = await context.AssetFavorites
                .Where(favorite =>
                    favorite.UserId == tenant.UserId &&
                    tenant.AssetIds.Contains(favorite.AssetId))
                .OrderBy(favorite => favorite.AssetId)
                .Select(favorite => favorite.AssetId)
                .ToArrayAsync(CancellationToken.None);
            long[] versions = await context.Assets
                .Where(asset => tenant.AssetIds.Contains(asset.Id))
                .OrderBy(asset => asset.Id)
                .Select(asset => asset.Version)
                .ToArrayAsync(CancellationToken.None);
            string? state = await context.Jobs
                .Where(job => job.Id == jobId)
                .Select(job => job.State)
                .SingleOrDefaultAsync(CancellationToken.None);

            Assert.Equal(tenant.AssetIds, favorites);
            Assert.Equal([2L, 2L], versions);
            Assert.Equal("Completed", state);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task SeedAsync(
        string testRoot,
        string databasePath,
        string mediaRoot)
    {
        Assembly host = Assembly.Load("Vistara.E2E.Host");
        MethodInfo entryPoint = host.EntryPoint
            ?? throw new InvalidOperationException(
                "The E2E host has no entry point.");
        object? seed = entryPoint.Invoke(
            null,
            [
                new[]
                {
                    "seed",
                    "--database",
                    databasePath,
                    "--media-root",
                    mediaRoot,
                    "--fixture",
                    Path.Combine(
                        FindRepositoryRoot(),
                        "tests",
                        "Vistara.E2E",
                        "fixtures",
                        "tiny.png.base64"),
                    "--state",
                    Path.Combine(testRoot, "state.json"),
                    "--pepper",
                    Pepper,
                },
            ]);
        if (seed is Task seedTask)
        {
            await seedTask;
        }
    }

    private static async Task<SeededTenant> ReadSeededTenantAsync(
        string statePath,
        string connectionString)
    {
        using JsonDocument state = JsonDocument.Parse(
            await File.ReadAllTextAsync(statePath, CancellationToken.None));
        JsonElement browser = state.RootElement
            .GetProperty("browsers")
            .GetProperty("chromium");
        Guid tenantId = browser.GetProperty("tenantId").GetGuid();
        Guid userId = browser.GetProperty("userId").GetGuid();
        Guid primaryAssetId = browser.GetProperty("primaryAssetId").GetGuid();
        string apiKey = browser.GetProperty("apiKey").GetString()!;

        await using VistaraDbContext context =
            CreateContext(connectionString, tenantId);
        Guid[] assetIds = await context.Assets
            .Where(asset =>
                asset.Status == "Ready" &&
                asset.OwnerId == userId &&
                asset.Id != primaryAssetId)
            .OrderBy(asset => asset.Id)
            .Select(asset => asset.Id)
            .Take(2)
            .ToArrayAsync(CancellationToken.None);
        Assert.Equal(2, assetIds.Length);
        return new SeededTenant(tenantId, userId, apiKey, assetIds);
    }

    private static WebApplication BuildApi(
        string connectionString,
        string mediaRoot)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Development,
            });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(
            RuntimeSettings(connectionString, mediaRoot));
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton<
            ApiMedia.IMediaRuntimeDependencies,
            TestMediaRuntimeDependencies>();
        builder.Services.AddVistaraApiRuntime(builder.Configuration);
        builder.Services.AddVistaraApiPlatform(builder.Configuration);
        builder.Services.AddVistaraApiPersistence(builder.Configuration);
        ApiMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
            builder.Services,
            builder.Configuration);
        builder.Services.AddVistaraPlatformSurface();
        WebApplication app = builder.Build();
        app.UseVistaraPlatform();
        app.MapVistaraPlatformEndpoints();
        app.MapVistaraPlatformSurface();
        app.MapVistaraGalleryOpenApi();
        return app;
    }

    private static async Task RunWorkerAsync(
        string connectionString,
        string mediaRoot,
        string testRoot)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            RuntimeSettings(connectionString, mediaRoot, testRoot));
        builder.Services.AddSingleton<
            WorkerMedia.IMediaRuntimeDependencies,
            TestMediaRuntimeDependencies>();
        WorkerMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
            builder.Services,
            builder.Configuration);
        builder.Services.AddVistaraWorkerPlatform(builder.Configuration);
        using IHost worker = builder.Build();
        worker.Services.ValidateVistaraWorkerPlatformComposition();
        await worker.Services
            .GetRequiredService<JobWorkerRuntime>()
            .RunOnceAsync(CancellationToken.None);
    }

    private static Dictionary<string, string?> RuntimeSettings(
        string connectionString,
        string mediaRoot,
        string? workerRoot = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = connectionString,
            ["Media:Storage:Provider"] = "Local",
            ["Media:Storage:Local:RootPath"] = mediaRoot,
            ["Media:Imaging:Provider"] = "NetVips",
            ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
            ["Platform:Authentication:ApiKeys:Peppers:v1"] = Pepper,
            ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] = "e2e",
            ["Platform:Authentication:Jwt:Issuers:0:Issuer"] =
                "https://issuer.e2e.invalid",
            ["Platform:Authentication:Jwt:Issuers:0:Audience"] = "vistara-api",
            ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
                "https://issuer.e2e.invalid/.well-known/openid-configuration",
            ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] = "RS256",
        };
        if (workerRoot is not null)
        {
            settings["Worker:InstanceId"] = "vistara-bulk-host-test";
            settings["Worker:Jobs:MaximumConcurrency"] = "1";
            settings["Worker:ImagingLimits:ScratchDirectory"] =
                Path.Combine(workerRoot, "scratch");
        }

        return settings;
    }

    private static string ResolveAddress(WebApplication app)
    {
        IServerAddressesFeature addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!;
        return addresses.Addresses.First();
    }

    private static VistaraDbContext CreateContext(
        string connectionString,
        Guid tenantId) =>
        new(
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(connectionString)
                .Options,
            new FixedTenantScope(tenantId));

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

        throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed record SeededTenant(
        Guid TenantId,
        Guid UserId,
        string ApiKey,
        Guid[] AssetIds);

    /// <summary>
    /// Keeps the host tests independent of a native libvips runtime, which the
    /// bulk curation path never touches.
    /// </summary>
    private sealed class TestMediaRuntimeDependencies :
        ApiMedia.IMediaRuntimeDependencies,
        WorkerMedia.IMediaRuntimeDependencies
    {
        public AWSCredentials CreateS3Credentials(
            ApiMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public TokenCredential CreateAzureCredential() =>
            throw new NotSupportedException();

        public IImageProcessor CreateImageProcessor() =>
            StubImageProcessor.Instance;

        AWSCredentials WorkerMedia.IMediaRuntimeDependencies.CreateS3Credentials(
            WorkerMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();
    }

    private sealed class StubImageProcessor : IImageProcessor
    {
        internal static StubImageProcessor Instance { get; } = new();

        public ImageProcessorCapabilities Capabilities { get; } = new()
        {
            InputFormats = [ImageFormat.Png],
            OutputFormats = [ImageFormat.Png],
            MaxFrames = 1,
            SupportsAutoOrientation = true,
            SupportsColorProfileNormalization = true,
            SupportsSensitiveMetadataStripping = true,
        };

        public ImagePipelineFingerprint PipelineFingerprint { get; } =
            new("vistara-bulk-host-test-pipeline");

        public ValueTask<ImageInspection> InspectAsync(
            IReplayableImageSource source,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ImageTransformResult> TransformAsync(
            IReplayableImageSource source,
            Stream destination,
            CanonicalTransformRecipe recipe,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
