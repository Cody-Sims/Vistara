using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Media;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Application.Gallery.Queries;
using Vistara.Application.Jobs;
using Vistara.Contracts.Idempotency;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.IntegrationTests.DerivativeConcurrency;
using Vistara.IntegrationTests.MediaDelivery;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Queries;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Storage.Local;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Features.Ingest;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.AssetReadiness;

/// <summary>
/// Drives real ingest and derivative workers over real SQLite persistence and a
/// virgin local blob root, then reads and serves the asset through the
/// production delivery adapters. Ready-only private delivery is the observable
/// contract: an asset must not serve an advertised rendition until every
/// required standard derivative is durably visible.
/// </summary>
public sealed class AssetReadyLifecycleTests
{
    [Fact]
    public async Task Six_uploads_and_their_standard_derivatives_promote_every_asset_to_ready()
    {
        await using AssetReadinessScenario scenario =
            await AssetReadinessScenario.CreateAsync();

        Guid[] assets = await scenario.IngestAsync(count: 6);
        int completed = await scenario.RunAllDerivativesAsync();

        Assert.Equal(6, assets.Length);
        Assert.Equal(24, completed);
        foreach (Guid assetId in assets)
        {
            Assert.Equal("Ready", await scenario.ReadAssetStatusAsync(assetId));
            Assert.Equal(4, await scenario.CountReadyDerivativesAsync(assetId));
            RenditionResponse served = await scenario.ServeFirstRenditionAsync(assetId);
            Assert.Equal(HttpStatusCode.OK, served.Response.StatusCode);
            Assert.Equal(AssetReadinessScenario.ExpectedRenditionBytes, served.Response.Body);
            Assert.Equal("image/webp", served.Response.ContentType);
        }
    }

    [Fact]
    public async Task An_asset_stays_processing_until_the_last_required_derivative_completes()
    {
        await using AssetReadinessScenario scenario =
            await AssetReadinessScenario.CreateAsync();
        Guid assetId = (await scenario.IngestAsync(count: 1))[0];

        await scenario.RunDerivativesAsync(assetId, "thumb", "grid", "viewer");

        Assert.Equal("Processing", await scenario.ReadAssetStatusAsync(assetId));
        Assert.Equal(3, await scenario.CountReadyDerivativesAsync(assetId));
        RenditionResponse withheld = await scenario.ServeFirstRenditionAsync(assetId);
        Assert.Equal(HttpStatusCode.NotFound, withheld.Response.StatusCode);

        await scenario.RunDerivativesAsync(assetId, "download-web");

        Assert.Equal("Ready", await scenario.ReadAssetStatusAsync(assetId));
        RenditionResponse served = await scenario.ServeFirstRenditionAsync(assetId);
        Assert.Equal(HttpStatusCode.OK, served.Response.StatusCode);
        Assert.Equal(AssetReadinessScenario.ExpectedRenditionBytes, served.Response.Body);
    }

    [Fact]
    public async Task A_failed_derivative_withholds_readiness_until_its_retry_succeeds()
    {
        await using AssetReadinessScenario scenario =
            await AssetReadinessScenario.CreateAsync();
        Guid assetId = (await scenario.IngestAsync(count: 1))[0];

        await scenario.RunDerivativesAsync(assetId, "thumb", "grid", "viewer");
        scenario.Imaging.Error = ImageProcessorErrorCode.MalformedImage;
        JobHandlerResult failed = Assert.Single(
            await scenario.RunDerivativesAsync(assetId, "download-web"));

        Assert.False(failed.IsSuccess);
        Assert.Equal("Processing", await scenario.ReadAssetStatusAsync(assetId));
        Assert.Equal(
            "Failed",
            await scenario.ReadDerivativeStateAsync(assetId, "download-web"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await scenario.ServeFirstRenditionAsync(assetId)).Response.StatusCode);

        scenario.Imaging.Error = null;
        JobHandlerResult retried = Assert.Single(
            await scenario.RunDerivativesAsync(assetId, "download-web"));

        Assert.True(retried.IsSuccess);
        Assert.Equal("Ready", await scenario.ReadAssetStatusAsync(assetId));
        Assert.Equal(
            HttpStatusCode.OK,
            (await scenario.ServeFirstRenditionAsync(assetId)).Response.StatusCode);
    }

    [Fact]
    public async Task Parallel_final_completions_promote_the_asset_exactly_once()
    {
        await using AssetReadinessScenario scenario =
            await AssetReadinessScenario.CreateAsync();
        Guid assetId = (await scenario.IngestAsync(count: 1))[0];

        await scenario.RunDerivativesAsync(assetId, "thumb", "grid");
        long beforePromotion = await scenario.ReadAssetVersionAsync(assetId);
        await scenario.RunDerivativesConcurrentlyAsync(
            assetId,
            "viewer",
            "download-web");

        Assert.Equal("Ready", await scenario.ReadAssetStatusAsync(assetId));
        Assert.Equal(
            beforePromotion + 1,
            await scenario.ReadAssetVersionAsync(assetId));
        Assert.Equal(1, await scenario.CountReadyAuditsAsync(assetId));
        Assert.Equal(1, await scenario.CountReadyEventsAsync(assetId));
    }

    [Fact]
    public async Task Redelivering_every_derivative_job_never_promotes_the_asset_twice()
    {
        await using AssetReadinessScenario scenario =
            await AssetReadinessScenario.CreateAsync();
        Guid assetId = (await scenario.IngestAsync(count: 1))[0];
        long ingested = await scenario.ReadAssetVersionAsync(assetId);

        await scenario.RunAllDerivativesAsync(deliveries: 2);

        Assert.Equal("Ready", await scenario.ReadAssetStatusAsync(assetId));
        Assert.Equal(ingested + 1, await scenario.ReadAssetVersionAsync(assetId));
        Assert.Equal(1, await scenario.CountReadyAuditsAsync(assetId));
        Assert.Equal(1, await scenario.CountReadyEventsAsync(assetId));
    }

    [Fact]
    public async Task Readiness_never_crosses_a_tenant_boundary()
    {
        await using AssetReadinessScenario scenario =
            await AssetReadinessScenario.CreateAsync();
        Guid owned = (await scenario.IngestAsync(count: 1))[0];
        Guid neighbour = (await scenario.IngestAsync(
            count: 1,
            tenant: AssetReadinessTenant.Neighbour))[0];

        await scenario.RunAllDerivativesAsync();

        Assert.Equal("Ready", await scenario.ReadAssetStatusAsync(owned));
        Assert.Equal(
            "Ready",
            await scenario.ReadAssetStatusAsync(
                neighbour,
                AssetReadinessTenant.Neighbour));
        RenditionResponse crossTenant = await scenario.ServeFirstRenditionAsync(
            owned,
            AssetReadinessTenant.Owner,
            requestAs: AssetReadinessTenant.Neighbour);
        Assert.NotEqual(HttpStatusCode.OK, crossTenant.Response.StatusCode);
    }

    [Fact]
    public async Task A_trashed_asset_is_never_re_promoted_by_a_late_derivative()
    {
        await using AssetReadinessScenario scenario =
            await AssetReadinessScenario.CreateAsync();
        Guid assetId = (await scenario.IngestAsync(count: 1))[0];

        await scenario.RunDerivativesAsync(assetId, "thumb", "grid", "viewer");
        await scenario.SetAssetStatusAsync(assetId, "Trashed");
        await scenario.RunDerivativesAsync(assetId, "download-web");

        Assert.Equal("Trashed", await scenario.ReadAssetStatusAsync(assetId));
        Assert.Equal(4, await scenario.CountReadyDerivativesAsync(assetId));
        Assert.Equal(0, await scenario.CountReadyAuditsAsync(assetId));
    }
}

internal enum AssetReadinessTenant
{
    Owner,
    Neighbour,
}

internal sealed record RenditionResponse(string Path, DeliveryResponse Response);

internal sealed class AssetReadinessScenario : IAsyncDisposable
{
    private static readonly DateTimeOffset Now =
        new(2036, 9, 10, 11, 12, 13, TimeSpan.Zero);

    private static readonly JobRetryPolicy RetryPolicy =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    private readonly string _scratchRoot;
    private readonly SqliteConnection _anchor;
    private readonly ServiceProvider _worker;
    private readonly WebApplication _delivery;
    private readonly DbContextOptions<VistaraDbContext> _vistaraOptions;
    private readonly DbContextOptions<JobDbContext> _jobOptions;
    private readonly ConcurrentDictionary<AssetReadinessTenant, ServiceProvider> _apis;
    private readonly List<DerivativeWork> _leased = [];
    private DateTimeOffset _leaseAtUtc = Now;
    private int _uploads;

    private AssetReadinessScenario(
        string scratchRoot,
        SqliteConnection anchor,
        ServiceProvider worker,
        WebApplication delivery,
        DbContextOptions<VistaraDbContext> vistaraOptions,
        DbContextOptions<JobDbContext> jobOptions,
        ConcurrentDictionary<AssetReadinessTenant, ServiceProvider> apis,
        ReadinessImageProcessor imaging,
        IReadOnlyDictionary<AssetReadinessTenant, TenantIdentity> tenants)
    {
        _scratchRoot = scratchRoot;
        _anchor = anchor;
        _worker = worker;
        _delivery = delivery;
        _vistaraOptions = vistaraOptions;
        _jobOptions = jobOptions;
        _apis = apis;
        Imaging = imaging;
        Tenants = tenants;
    }

    internal ReadinessImageProcessor Imaging { get; }

    internal IReadOnlyDictionary<AssetReadinessTenant, TenantIdentity> Tenants { get; }

    internal static byte[] ExpectedRenditionBytes =>
        ReadinessImageProcessor.OutputBytes;

    internal static async ValueTask<AssetReadinessScenario> CreateAsync()
    {
        string scratchRoot = DerivativeScratchDirectory.Create();
        string mediaRoot = Path.Combine(scratchRoot, "media");
        Directory.CreateDirectory(mediaRoot);
        string connectionString =
            $"Data Source={Path.Combine(scratchRoot, "readiness.db")}";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        var vistaraOptions = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var jobOptions = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Dictionary<AssetReadinessTenant, TenantIdentity> tenants = new()
        {
            [AssetReadinessTenant.Owner] = TenantIdentity.Create("owner"),
            [AssetReadinessTenant.Neighbour] = TenantIdentity.Create("neighbour"),
        };
        await using (var schema = new VistaraDbContext(
            vistaraOptions,
            new FixedTenantScope(tenants[AssetReadinessTenant.Owner].TenantId)))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        foreach (TenantIdentity tenant in tenants.Values)
        {
            await SeedTenantAsync(vistaraOptions, tenant);
        }

        var store = new LocalBlobStore(new LocalBlobStoreOptions(mediaRoot));
        var imaging = new ReadinessImageProcessor();
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = connectionString,
            ["Worker:InstanceId"] = "asset-readiness-tests",
            ["Worker:Jobs:MaximumConcurrency"] = "1",
            ["Worker:ImagingLimits:ScratchDirectory"] =
                Path.Combine(scratchRoot, "transform-scratch"),
            ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
            ["Platform:Authentication:ApiKeys:Peppers:v1"] =
                "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=",
            ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] = "asset-readiness",
            ["Platform:Authentication:Jwt:Issuers:0:Issuer"] =
                "https://issuer.example",
            ["Platform:Authentication:Jwt:Issuers:0:Audience"] = "vistara-api",
            ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
                "https://issuer.example/.well-known/openid-configuration",
            ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] = "RS256",
        };
        IConfiguration configuration =
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection workerServices = [];
        workerServices.AddSingleton<IBlobStore>(store);
        workerServices.AddSingleton<IImageProcessor>(imaging);
        workerServices.AddSingleton<IClock>(new ReadinessClock(Now));
        workerServices.AddSingleton<IUuid7Generator>(
            new Uuid7Generator(new ReadinessClock(Now)));
        workerServices.AddVistaraWorkerPlatform(configuration);
        ServiceProvider worker = workerServices.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        WebApplicationBuilder deliveryBuilder = WebApplication.CreateBuilder();
        deliveryBuilder.Configuration.AddInMemoryCollection(settings);
        deliveryBuilder.Services.AddSingleton<IBlobStore>(store);
        deliveryBuilder.Services.AddVistaraApiPlatform(deliveryBuilder.Configuration);
        deliveryBuilder.Services.AddVistaraApiPersistence(deliveryBuilder.Configuration);
        WebApplication delivery = deliveryBuilder.Build();
        delivery.UseRouting();
#pragma warning disable ASP0014
        delivery.UseEndpoints(static _ => { });
#pragma warning restore ASP0014
        delivery.MapVistaraMedia();

        ConcurrentDictionary<AssetReadinessTenant, ServiceProvider> apis = new();
        foreach ((AssetReadinessTenant key, TenantIdentity tenant) in tenants)
        {
            ServiceCollection apiServices = [];
            var context = new ReadinessTenantContext(tenant.TenantId);
            apiServices.AddScoped<ITenantScope>(_ => context);
            apiServices.AddScoped<IPlatformTenantContext>(_ => context);
            apiServices.AddSingleton<IBlobStore>(store);
            apiServices.AddSingleton<IClock>(new ReadinessClock(Now));
            apiServices.AddSingleton<IUuid7Generator>(
                new Uuid7Generator(new ReadinessClock(Now)));
            apiServices.AddVistaraApiPlatform(configuration);
            apiServices.AddVistaraApiPersistence(configuration);
            apis[key] = apiServices.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true });
        }

        return new AssetReadinessScenario(
            scratchRoot,
            anchor,
            worker,
            delivery,
            vistaraOptions,
            jobOptions,
            apis,
            imaging,
            tenants);
    }

    /// <summary>
    /// Uploads through the production proxy strategy the local provider selects,
    /// then runs the real ingest worker so assets, revisions, derivative jobs,
    /// audit, and outbox all land in one persisted transaction.
    /// </summary>
    internal async ValueTask<Guid[]> IngestAsync(
        int count,
        AssetReadinessTenant tenant = AssetReadinessTenant.Owner)
    {
        List<Guid> assets = [];
        for (int index = 0; index < count; index++)
        {
            assets.Add(await IngestOneAsync(tenant, ++_uploads));
        }

        return [.. assets];
    }

    private async ValueTask<Guid> IngestOneAsync(
        AssetReadinessTenant tenant,
        int ordinal)
    {
        TenantIdentity identity = Tenants[tenant];
        Guid uploadId = Guid.CreateVersion7(Now.AddMilliseconds(ordinal));
        byte[] content = Encoding.UTF8.GetBytes($"vistara-source-image-{ordinal:D4}");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using (AsyncServiceScope scope = _apis[tenant].CreateAsyncScope())
        {
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                new ReserveUploadRequest(
                    identity.TenantId,
                    identity.UserId,
                    uploadId,
                    "proxy",
                    $"readiness-{ordinal:D4}.png",
                    content.LongLength,
                    "image/png",
                    sha256,
                    $"staging/{identity.TenantId.ToString("N")[..2]}/" +
                        $"{identity.TenantId:D}/{uploadId:D}",
                    Convert.ToHexStringLower(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes($"readiness-{ordinal}"))),
                    new IdempotencyKey($"readiness-{ordinal}-create"),
                    Now.AddHours(1)),
                CancellationToken.None);
            Assert.NotNull(reserved.Session);
            UploadIssuance issued = await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None);
            UploadWriteResult written = await application.WriteProxyAsync(
                issued.Session,
                new MemoryStream(content, writable: false),
                issued.Session.Version,
                CancellationToken.None);
            Assert.Equal(UploadWriteStatus.Written, written.Status);
            UploadCommitResult committed = await application.CommitAsync(
                written.Session!,
                [],
                new IdempotencyKey($"readiness-{ordinal}-commit"),
                written.Session!.Version,
                CancellationToken.None);
            Assert.NotNull(committed.Session);
        }

        await using (AsyncServiceScope scope = _worker.CreateAsyncScope())
        {
            scope.ServiceProvider
                .GetRequiredService<IMutableTenantScope>()
                .Establish(identity.TenantId);
            JobHandlerResult ingested = await scope.ServiceProvider
                .GetRequiredService<IngestService>()
                .ProcessAsync(identity.TenantId, uploadId, CancellationToken.None);
            Assert.True(ingested.IsSuccess);
        }

        await using VistaraDbContext context = CreateContext(identity.TenantId);
        TenantKey tenantKey = identity.TenantId;
        return await context.IngestOperations
            .Where(row =>
                row.TenantId == tenantKey &&
                row.UploadSessionId == uploadId)
            .Select(row => row.AssetId!.Value)
            .SingleAsync();
    }

    /// <summary>
    /// Runs every leased derivative job. <paramref name="deliveries"/> above one
    /// replays the same leased job, which is what an at-least-once queue does
    /// after a worker crash between handling and acknowledgement.
    /// </summary>
    internal async ValueTask<int> RunAllDerivativesAsync(int deliveries = 1)
    {
        int handled = 0;
        foreach (TenantIdentity identity in Tenants.Values)
        {
            await LeaseDerivativesAsync(identity);
            foreach (DerivativeWork work in Drain(identity))
            {
                JobHandlerResult result = await ExecuteAsync(
                    identity,
                    work,
                    deliveries);
                Assert.True(result.IsSuccess, Describe(work, result));
                handled++;
            }
        }

        return handled;
    }

    internal ValueTask<IReadOnlyList<JobHandlerResult>> RunDerivativesAsync(
        Guid assetId,
        params string[] presets) =>
        RunDerivativesAsync(assetId, AssetReadinessTenant.Owner, presets);

    internal async ValueTask<IReadOnlyList<JobHandlerResult>> RunDerivativesAsync(
        Guid assetId,
        AssetReadinessTenant tenant,
        params string[] presets)
    {
        TenantIdentity identity = Tenants[tenant];
        List<JobHandlerResult> results = [];
        foreach (DerivativeWork work in await SelectAsync(identity, assetId, presets))
        {
            results.Add(await ExecuteAsync(identity, work));
        }

        return results;
    }

    internal async ValueTask RunDerivativesConcurrentlyAsync(
        Guid assetId,
        params string[] presets)
    {
        TenantIdentity identity = Tenants[AssetReadinessTenant.Owner];
        DerivativeWork[] selected = await SelectAsync(identity, assetId, presets);
        using var barrier = new Barrier(selected.Length);
        JobHandlerResult[] results = await Task.WhenAll(
            selected.Select(work => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ExecuteAsync(identity, work);
            })));
        Assert.All(
            results,
            result => Assert.True(
                result.IsSuccess,
                result.Failure?.Reason.ToString()));
    }

    /// <summary>
    /// Leases derivative work once and keeps unexecuted assignments so a test
    /// can run part of an asset's required set and finish the rest later,
    /// exactly as a worker that is still catching up would.
    /// </summary>
    private async ValueTask<DerivativeWork[]> SelectAsync(
        TenantIdentity identity,
        Guid assetId,
        string[] presets)
    {
        await LeaseDerivativesAsync(identity);
        DerivativeWork[] selected = [.. _leased.Where(work =>
            work.TenantId == identity.TenantId &&
            work.AssetId == assetId &&
            presets.Contains(work.PresetName))];
        Assert.Equal(presets.Length, selected.Length);
        foreach (DerivativeWork work in selected)
        {
            _leased.Remove(work);
        }

        return selected;
    }

    private static string Describe(DerivativeWork work, JobHandlerResult result) =>
        $"{work.PresetName}: {result.Failure?.Reason}";

    private DerivativeWork[] Drain(TenantIdentity identity)
    {
        DerivativeWork[] owned = [.. _leased.Where(work =>
            work.TenantId == identity.TenantId)];
        foreach (DerivativeWork work in owned)
        {
            _leased.Remove(work);
        }

        return owned;
    }

    private async ValueTask LeaseDerivativesAsync(TenantIdentity identity)
    {
        await using var jobs = new JobDbContext(
            _jobOptions,
            new FixedTenantScope(identity.TenantId));
        var queue = new RelationalJobQueue(
            jobs,
            new JobQueueOptions { ConfiguredWorkerCount = 1 });
        Result<IReadOnlyList<JobLeaseAssignment>> leased = await queue.LeaseAsync(
            new JobLeaseRequest(
                new JobLeaseOwner($"readiness-{identity.Slug}"),
                _leaseAtUtc,
                TimeSpan.FromHours(2),
                MaximumCount: 64),
            CancellationToken.None);
        Assert.True(
            leased.TryGetValue(out IReadOnlyList<JobLeaseAssignment>? assignments),
            leased.Error?.Message);
        foreach (JobLeaseAssignment assignment in assignments!)
        {
            if (assignment.Job.Type != DerivativeJobHandler.SupportedJobType ||
                !DerivativeJobContract.TryParse(
                    assignment.Job.Type,
                    assignment.Job.PayloadVersion,
                    assignment.Job.Payload,
                    out DerivativeJobPayloadV1? payload) ||
                payload is null)
            {
                continue;
            }

            _leased.Add(new DerivativeWork(
                identity.TenantId,
                assignment,
                payload.Generation.AssetId,
                payload.Generation.PresetName));
        }
    }

    private async ValueTask<JobHandlerResult> ExecuteAsync(
        TenantIdentity identity,
        DerivativeWork work,
        int deliveries = 1)
    {
        JobHandlerResult result = JobHandlerResult.Success();
        for (int delivery = 0; delivery < deliveries; delivery++)
        {
            await using AsyncServiceScope scope = _worker.CreateAsyncScope();
            scope.ServiceProvider
                .GetRequiredService<IMutableTenantScope>()
                .Establish(identity.TenantId);
            result = await scope.ServiceProvider
                .GetRequiredService<DerivativeJobHandler>()
                .HandleAsync(work.Assignment.Job, CancellationToken.None);
            if (!result.IsSuccess)
            {
                break;
            }
        }

        await SettleAsync(identity, work, result);
        return result;
    }

    private async ValueTask SettleAsync(
        TenantIdentity identity,
        DerivativeWork work,
        JobHandlerResult result)
    {
        await using var jobs = new JobDbContext(
            _jobOptions,
            new FixedTenantScope(identity.TenantId));
        var queue = new RelationalJobQueue(
            jobs,
            new JobQueueOptions { ConfiguredWorkerCount = 1 });
        JobLease lease = work.Assignment.Lease;
        if (result.IsSuccess)
        {
            _ = await queue.CompleteAsync(
                new JobCompletionRequest(lease.JobId, lease.Owner, lease.JobVersion, Now),
                CancellationToken.None);
            return;
        }

        _ = await queue.FailAsync(
            new JobFailureRequest(
                lease.JobId,
                lease.Owner,
                lease.JobVersion,
                result.Failure!,
                Now,
                RetryPolicy),
            CancellationToken.None);
        _leaseAtUtc = _leaseAtUtc.Add(RetryPolicy.MaximumDelay);
    }

    internal async ValueTask<string> ReadAssetStatusAsync(
        Guid assetId,
        AssetReadinessTenant tenant = AssetReadinessTenant.Owner)
    {
        await using VistaraDbContext context = CreateContext(Tenants[tenant].TenantId);
        return await context.Assets
            .Where(row => row.Id == assetId)
            .Select(row => row.Status)
            .SingleAsync();
    }

    internal async ValueTask<long> ReadAssetVersionAsync(Guid assetId)
    {
        await using VistaraDbContext context =
            CreateContext(Tenants[AssetReadinessTenant.Owner].TenantId);
        return await context.Assets
            .Where(row => row.Id == assetId)
            .Select(row => row.Version)
            .SingleAsync();
    }

    internal async ValueTask SetAssetStatusAsync(Guid assetId, string status)
    {
        await using VistaraDbContext context =
            CreateContext(Tenants[AssetReadinessTenant.Owner].TenantId);
        AssetRow asset = await context.Assets.SingleAsync(row => row.Id == assetId);
        asset.Status = status;
        asset.UpdatedAtUtc = Now;
        asset.Version = checked(asset.Version + 1);
        await context.SaveChangesAsync();
    }

    internal async ValueTask<string[]> DumpDerivativesAsync()
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            "SELECT asset_id, preset_name, state, failure_code, version " +
            "FROM derivative_requests;";
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join(
                " | ",
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index)
                        ? "<null>"
                        : reader.GetValue(index).ToString())));
        }

        return [.. rows];
    }

    internal async ValueTask<int> CountReadyDerivativesAsync(Guid assetId) =>
        (int)await ScalarAsync(
            "SELECT COUNT(*) FROM derivative_requests " +
            "WHERE asset_id = $asset AND state = 'Ready';",
            ("$asset", assetId));

    internal async ValueTask<string> ReadDerivativeStateAsync(
        Guid assetId,
        string preset)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            "SELECT state FROM derivative_requests " +
            "WHERE asset_id = $asset AND preset_name = $preset;";
        command.Parameters.AddWithValue("$asset", assetId);
        command.Parameters.AddWithValue("$preset", preset);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    internal async ValueTask<int> CountReadyAuditsAsync(Guid assetId) =>
        (int)await ScalarAsync(
            "SELECT COUNT(*) FROM audit_events " +
            "WHERE action = 'asset.ready' AND resource_identifier = $asset;",
            ("$asset", assetId.ToString("D")));

    internal async ValueTask<int> CountReadyEventsAsync(Guid assetId) =>
        (int)await ScalarAsync(
            "SELECT COUNT(*) FROM outbox_messages " +
            "WHERE event_type = 'asset.ready' AND correlation_id = $asset;",
            ("$asset", assetId));

    private async ValueTask<long> ScalarAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    internal async ValueTask<RenditionResponse> ServeFirstRenditionAsync(
        Guid assetId,
        AssetReadinessTenant owner = AssetReadinessTenant.Owner,
        AssetReadinessTenant? requestAs = null)
    {
        TenantIdentity identity = Tenants[owner];
        TenantIdentity caller = Tenants[requestAs ?? owner];
        string path = await ReadAdvertisedRenditionAsync(identity, assetId);
        RequestDelegate pipeline = ((IApplicationBuilder)_delivery).Build();
        await using AsyncServiceScope scope = _delivery.Services.CreateAsyncScope();
        scope.ServiceProvider
            .GetRequiredService<IMutableTenantScope>()
            .Establish(caller.TenantId);
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("scope", "assets.read"),
                    new Claim("tenant_id", caller.TenantId.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, caller.UserId.ToString("D")),
                ],
                "Test")),
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-asset-readiness";
        await pipeline(context);
        return new RenditionResponse(
            path,
            new DeliveryResponse(
                (HttpStatusCode)context.Response.StatusCode,
                context.Response.ContentType,
                context.Response.ContentLength,
                context.Response.Headers,
                ((MemoryStream)context.Response.Body).ToArray()));
    }

    /// <summary>
    /// Reads the rendition path the asset query API advertises to the gallery,
    /// which is exactly the URL a client would follow.
    /// </summary>
    private async ValueTask<string> ReadAdvertisedRenditionAsync(
        TenantIdentity identity,
        Guid assetId)
    {
        await using VistaraDbContext context = CreateContext(identity.TenantId);
        var store = new RelationalAssetQueryStore(context);
        AssetQuerySlice slice = await store.QueryAsync(
            new AssetQueryScope(identity.TenantId, identity.UserId),
            AssetQueryCriteria.Create(),
            new AssetQueryWindow(Now.AddDays(1), Continuation: null),
            CancellationToken.None);
        AssetQueryItem item = Assert.Single(
            slice.Items,
            candidate => candidate.Id == assetId);
        return item.Renditions
            .First(candidate => candidate.Path.StartsWith(
                "/delivery/assets/",
                StringComparison.Ordinal))
            .Path;
    }

    private VistaraDbContext CreateContext(Guid tenantId) =>
        new(_vistaraOptions, new FixedTenantScope(tenantId));

    private static async Task SeedTenantAsync(
        DbContextOptions<VistaraDbContext> options,
        TenantIdentity identity)
    {
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(identity.TenantId));
        context.Tenants.Add(new TenantRow
        {
            Id = identity.TenantId,
            TenantId = identity.TenantId,
            Slug = identity.Slug,
            Name = identity.Slug,
            Status = "Active",
            SettingsJson = "{}",
            QuotasJson = "{}",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.Users.Add(new UserRow
        {
            Id = identity.UserId,
            NormalizedEmail = $"{identity.Slug}@example.invalid",
            DisplayName = identity.Slug,
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = identity.TenantId,
            UserId = identity.UserId,
            Role = "TenantOwner",
            Status = "Active",
            InvitedAtUtc = Now,
            JoinedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider api in _apis.Values)
        {
            await api.DisposeAsync();
        }

        await _delivery.DisposeAsync();
        await _worker.DisposeAsync();
        await _anchor.DisposeAsync();
        SqliteConnection.ClearAllPools();
        DerivativeScratchDirectory.Delete(_scratchRoot);
    }

    private sealed record DerivativeWork(
        Guid TenantId,
        JobLeaseAssignment Assignment,
        Guid AssetId,
        string PresetName);

    internal sealed record TenantIdentity(Guid TenantId, Guid UserId, string Slug)
    {
        internal static TenantIdentity Create(string slug) =>
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), slug);
    }

    private sealed class ReadinessTenantContext(Guid tenantId) :
        ITenantScope,
        IPlatformTenantContext
    {
        public Guid TenantId { get; } = tenantId;

        Guid? IPlatformTenantContext.TenantId => TenantId;
    }
}

internal sealed class ReadinessClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}

/// <summary>
/// Deterministic stand-in for the native pipeline: it inspects the real
/// uploaded bytes and emits fixed encoded output so readiness assertions do not
/// depend on a native libvips installation.
/// </summary>
internal sealed class ReadinessImageProcessor : IImageProcessor
{
    internal static byte[] OutputBytes { get; } =
        "deterministic-readiness-webp"u8.ToArray();

    internal ImageProcessorErrorCode? Error { get; set; }

    public ImageProcessorCapabilities Capabilities { get; } = new()
    {
        InputFormats = [ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.WebP],
        OutputFormats = [ImageFormat.WebP, ImageFormat.Jpeg, ImageFormat.Png],
        MaxFrames = 1,
        SupportsAutoOrientation = true,
        SupportsColorProfileNormalization = true,
        SupportsSensitiveMetadataStripping = true,
        StreamRequirements = new(false, false),
    };

    public ImagePipelineFingerprint PipelineFingerprint { get; } =
        new("asset-readiness-pipeline");

    public async ValueTask<ImageInspection> InspectAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await source.OpenReadAsync(cancellationToken);
        long bytes = 0;
        byte[] buffer = new byte[64];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            bytes = checked(bytes + read);
        }

        return new ImageInspection(
            ImageFormat.Png,
            new ImageMediaType("image/png"),
            width: 3_000,
            height: 2_000,
            frameCount: 1,
            aggregatePixels: 6_000_000,
            ImagePixelFormat.Rgba8,
            ImageOrientation.Normal,
            new ImagePrivacyMetadata(
                HasExif: false,
                HasGps: false,
                HasXmp: false,
                HasIptc: false,
                HasComments: false,
                HasEmbeddedThumbnail: false,
                HasEmbeddedFileName: false),
            bytes,
            estimatedDecodedBytes: 24_000_000);
    }

    public async ValueTask<ImageTransformResult> TransformAsync(
        IReplayableImageSource source,
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Error.HasValue)
        {
            throw new ImageProcessorException(Error.Value, "private decoder details");
        }

        await using Stream input = await source.OpenReadAsync(cancellationToken);
        await input.CopyToAsync(Stream.Null, cancellationToken);
        await destination.WriteAsync(OutputBytes, cancellationToken);
        return new ImageTransformResult(
            new ImageInspection(
                ImageFormat.WebP,
                new ImageMediaType("image/webp"),
                width: recipe.Width,
                height: recipe.Height,
                frameCount: 1,
                aggregatePixels: (long)recipe.Width * recipe.Height,
                ImagePixelFormat.Rgba8,
                ImageOrientation.Normal,
                new ImagePrivacyMetadata(
                    HasExif: false,
                    HasGps: false,
                    HasXmp: false,
                    HasIptc: false,
                    HasComments: false,
                    HasEmbeddedThumbnail: false,
                    HasEmbeddedFileName: false),
                OutputBytes.LongLength,
                estimatedDecodedBytes: (long)recipe.Width * recipe.Height * 4),
            OutputBytes.LongLength,
            new ImageSha256(Convert.ToHexStringLower(SHA256.HashData(OutputBytes))),
            recipe.Fingerprint,
            PipelineFingerprint);
    }
}
