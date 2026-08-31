using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Gallery;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Application.Identity;
using Vistara.Application.Jobs;
using Vistara.Auth.ApiKeys;
using Vistara.Contracts.Idempotency;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Jobs;
using Vistara.Domain.Tenancy;
using Vistara.IntegrationTests.AssetReadiness;
using Vistara.IntegrationTests.DerivativeConcurrency;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Storage.Local;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Features.Ingest;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Shares;

/// <summary>
/// Drives the production upload, ingest, and derivative path over real SQLite
/// persistence and a virgin local blob root, then creates, reads, and revokes
/// shares through the production API pipeline with a real issued API key.
/// The observable contract is that a share target carries the same optimistic
/// concurrency version the asset contract advertises as <c>version</c>, never
/// the blob <c>revisionNumber</c>, and that every absent, stale, trashed,
/// purged, or foreign target is concealed as a share target not found.
/// </summary>
public sealed class ShareTargetVersionEndToEndTests
{
    [Fact]
    public async Task An_uploaded_ready_asset_is_shared_by_its_advertised_asset_version()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);

        Assert.NotEqual(asset.RevisionNumber, asset.Version);

        ApiResponse created = await scenario.CreateSnapshotShareAsync(
            assetId,
            asset.Version);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        using JsonDocument body = JsonDocument.Parse(created.Body);
        JsonElement share = body.RootElement.GetProperty("share");
        JsonElement target = share.GetProperty("snapshotAssets")[0];
        Assert.Equal(assetId, target.GetProperty("id").GetGuid());
        Assert.Equal(asset.Version, target.GetProperty("version").GetInt64());
        Assert.Equal(
            1,
            share.GetProperty("share").GetProperty("target")
                .GetProperty("assetCount").GetInt32());

        string token = body.RootElement.GetProperty("publicToken").GetString()!;
        Guid shareId = share.GetProperty("share").GetProperty("id").GetGuid();
        ApiResponse published = await scenario.SendAsync(
            HttpMethods.Get,
            $"/api/v1/public/shares/{token}");

        Assert.Equal(HttpStatusCode.OK, published.Status);
        using JsonDocument publicBody = JsonDocument.Parse(published.Body);
        Assert.Equal(
            "active",
            publicBody.RootElement.GetProperty("status").GetString());
        JsonElement publicAsset = publicBody.RootElement
            .GetProperty("assets").GetProperty("items")[0];
        Assert.Equal(assetId, publicAsset.GetProperty("id").GetGuid());

        ApiResponse revoked = await scenario.SendAsync(
            HttpMethods.Delete,
            $"/api/v1/shares/{shareId:D}",
            apiKey: scenario.OwnerApiKey,
            headers: new Dictionary<string, string>
            {
                ["If-Match"] = "\"v1\"",
                ["Idempotency-Key"] = "share-version-revoke",
            });

        Assert.Equal(HttpStatusCode.NoContent, revoked.Status);
        ApiResponse gone = await scenario.SendAsync(
            HttpMethods.Get,
            $"/api/v1/public/shares/{token}");
        Assert.Equal(HttpStatusCode.Gone, gone.Status);
    }

    /// <summary>
    /// The blob revision number identifies stored bytes, not the asset
    /// resource, so it must never satisfy the optimistic concurrency check
    /// that guards a share target.
    /// </summary>
    [Fact]
    public async Task A_revision_number_is_never_accepted_as_a_target_version()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);

        Assert.Equal(1, asset.RevisionNumber);
        Assert.True(asset.Version > asset.RevisionNumber);

        ApiResponse rejected = await scenario.CreateSnapshotShareAsync(
            assetId,
            asset.RevisionNumber,
            idempotencyKey: "share-version-revision");

        Assert.Equal(HttpStatusCode.NotFound, rejected.Status);
        Assert.Equal("share_target_not_found", ProblemCode(rejected));
    }

    [Fact]
    public async Task A_stale_asset_version_conceals_the_share_target()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);

        await scenario.TouchAssetAsync(assetId);
        AssetContractVersions moved = await scenario.ReadAssetAsync(assetId);
        Assert.Equal(asset.Version + 1, moved.Version);
        Assert.Equal(asset.RevisionNumber, moved.RevisionNumber);

        ApiResponse stale = await scenario.CreateSnapshotShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-version-stale");

        Assert.Equal(HttpStatusCode.NotFound, stale.Status);
        Assert.Equal("share_target_not_found", ProblemCode(stale));

        ApiResponse ahead = await scenario.CreateSnapshotShareAsync(
            assetId,
            moved.Version + 1,
            idempotencyKey: "share-version-ahead");

        Assert.Equal(HttpStatusCode.NotFound, ahead.Status);
        Assert.Equal("share_target_not_found", ProblemCode(ahead));

        ApiResponse current = await scenario.CreateSnapshotShareAsync(
            assetId,
            moved.Version,
            idempotencyKey: "share-version-current");

        Assert.Equal(HttpStatusCode.Created, current.Status);
    }

    [Fact]
    public async Task A_trashed_or_purged_asset_conceals_the_share_target()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();

        await scenario.SetAssetStatusAsync(assetId, "Trashed");
        AssetContractVersions trashed = await scenario.ReadAssetRowAsync(assetId);
        ApiResponse afterTrash = await scenario.CreateSnapshotShareAsync(
            assetId,
            trashed.Version,
            idempotencyKey: "share-version-trashed");

        Assert.Equal(HttpStatusCode.NotFound, afterTrash.Status);
        Assert.Equal("share_target_not_found", ProblemCode(afterTrash));

        await scenario.SetAssetStatusAsync(assetId, "Purged");
        AssetContractVersions purged = await scenario.ReadAssetRowAsync(assetId);
        ApiResponse afterPurge = await scenario.CreateSnapshotShareAsync(
            assetId,
            purged.Version,
            idempotencyKey: "share-version-purged");

        Assert.Equal(HttpStatusCode.NotFound, afterPurge.Status);
        Assert.Equal("share_target_not_found", ProblemCode(afterPurge));
    }

    [Fact]
    public async Task A_neighbouring_tenant_cannot_share_another_tenants_asset()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);

        ApiResponse concealed = await scenario.CreateSnapshotShareAsync(
            assetId,
            asset.Version,
            apiKey: scenario.NeighbourApiKey,
            idempotencyKey: "share-version-cross-tenant");

        Assert.Equal(HttpStatusCode.NotFound, concealed.Status);
        Assert.Equal("share_target_not_found", ProblemCode(concealed));
    }

    /// <summary>
    /// An album share resolves its own members, so it must capture each current
    /// asset version rather than a revision number, and it must keep working
    /// after the album is curated with the very same versioned references the
    /// album endpoints accept.
    /// </summary>
    [Fact]
    public async Task An_album_share_captures_the_current_asset_versions()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);
        Guid albumId = await scenario.CreateAlbumWithAssetAsync(assetId, asset.Version);

        ApiResponse created = await scenario.SendAsync(
            HttpMethods.Post,
            "/api/v1/shares",
            apiKey: scenario.OwnerApiKey,
            body: $$"""
            {
              "name": "Album share",
              "targetKind": "album",
              "albumId": "{{albumId:D}}",
              "permissions": {
                "view": true,
                "downloadRenditions": false,
                "downloadOriginal": false
              },
              "metadataExposure": "basic"
            }
            """,
            headers: new Dictionary<string, string>
            {
                ["Idempotency-Key"] = "share-version-album",
            });

        Assert.Equal(HttpStatusCode.Created, created.Status);
        using JsonDocument body = JsonDocument.Parse(created.Body);
        JsonElement detail = body.RootElement.GetProperty("share");
        JsonElement target = detail.GetProperty("snapshotAssets")[0];
        Assert.Equal(assetId, target.GetProperty("id").GetGuid());
        Assert.Equal(
            await scenario.ReadAssetVersionAsync(assetId),
            target.GetProperty("version").GetInt64());
        Assert.Equal(
            "album",
            detail.GetProperty("share").GetProperty("target")
                .GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Revoking_a_share_requires_the_current_share_version()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);
        ApiResponse created = await scenario.CreateSnapshotShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-version-precondition");
        Assert.Equal(HttpStatusCode.Created, created.Status);
        using JsonDocument body = JsonDocument.Parse(created.Body);
        Guid shareId = body.RootElement
            .GetProperty("share").GetProperty("share").GetProperty("id").GetGuid();

        ApiResponse missing = await scenario.SendAsync(
            HttpMethods.Delete,
            $"/api/v1/shares/{shareId:D}",
            apiKey: scenario.OwnerApiKey,
            headers: new Dictionary<string, string>
            {
                ["Idempotency-Key"] = "share-version-missing-if-match",
            });

        Assert.Equal(
            HttpStatusCode.PreconditionRequired,
            missing.Status);
        Assert.Equal("if_match_required", ProblemCode(missing));

        ApiResponse stale = await scenario.SendAsync(
            HttpMethods.Delete,
            $"/api/v1/shares/{shareId:D}",
            apiKey: scenario.OwnerApiKey,
            headers: new Dictionary<string, string>
            {
                ["If-Match"] = "\"v7\"",
                ["Idempotency-Key"] = "share-version-stale-if-match",
            });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal("share_version_conflict", ProblemCode(stale));

        ApiResponse foreign = await scenario.SendAsync(
            HttpMethods.Delete,
            $"/api/v1/shares/{shareId:D}",
            apiKey: scenario.NeighbourApiKey,
            headers: new Dictionary<string, string>
            {
                ["If-Match"] = "\"v1\"",
                ["Idempotency-Key"] = "share-version-foreign-revoke",
            });

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("share_not_found", ProblemCode(foreign));
    }

    private static string? ProblemCode(ApiResponse response)
    {
        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.TryGetProperty("code", out JsonElement code)
            ? code.GetString()
            : document.RootElement.GetProperty("type").GetString();
    }
}

internal sealed record ApiResponse(
    HttpStatusCode Status,
    string Body,
    IHeaderDictionary Headers);

internal sealed record AssetContractVersions(long Version, long RevisionNumber);

internal sealed class ShareVersionScenario : IAsyncDisposable
{
    private const string ApiKeyPepper =
        "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=";

    private static readonly DateTimeOffset Now =
        new(2036, 11, 12, 13, 14, 15, TimeSpan.Zero);

    private readonly string _scratchRoot;
    private readonly SqliteConnection _anchor;
    private readonly ServiceProvider _worker;
    private readonly WebApplication _api;
    private readonly ServiceProvider _ownerUploads;
    private readonly DbContextOptions<VistaraDbContext> _vistaraOptions;
    private readonly DbContextOptions<JobDbContext> _jobOptions;
    private int _uploads;

    private ShareVersionScenario(
        string scratchRoot,
        SqliteConnection anchor,
        ServiceProvider worker,
        WebApplication api,
        ServiceProvider ownerUploads,
        DbContextOptions<VistaraDbContext> vistaraOptions,
        DbContextOptions<JobDbContext> jobOptions,
        TenantIdentity owner,
        TenantIdentity neighbour,
        string ownerApiKey,
        string neighbourApiKey)
    {
        _scratchRoot = scratchRoot;
        _anchor = anchor;
        _worker = worker;
        _api = api;
        _ownerUploads = ownerUploads;
        _vistaraOptions = vistaraOptions;
        _jobOptions = jobOptions;
        Owner = owner;
        Neighbour = neighbour;
        OwnerApiKey = ownerApiKey;
        NeighbourApiKey = neighbourApiKey;
    }

    internal TenantIdentity Owner { get; }

    internal TenantIdentity Neighbour { get; }

    internal string OwnerApiKey { get; }

    internal string NeighbourApiKey { get; }

    internal static async ValueTask<ShareVersionScenario> CreateAsync()
    {
        string scratchRoot = DerivativeScratchDirectory.Create();
        string mediaRoot = Path.Combine(scratchRoot, "media");
        Directory.CreateDirectory(mediaRoot);
        string connectionString =
            $"Data Source={Path.Combine(scratchRoot, "share-version.db")}";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        var vistaraOptions = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var jobOptions = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite(connectionString)
            .Options;
        TenantIdentity owner = TenantIdentity.Create("share-owner");
        TenantIdentity neighbour = TenantIdentity.Create("share-neighbour");
        await using (var schema = new VistaraDbContext(
            vistaraOptions,
            new FixedTenantScope(owner.TenantId)))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await SeedTenantAsync(vistaraOptions, owner);
        await SeedTenantAsync(vistaraOptions, neighbour);

        var store = new LocalBlobStore(new LocalBlobStoreOptions(mediaRoot));
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = connectionString,
            ["Worker:InstanceId"] = "share-version-tests",
            ["Worker:Jobs:MaximumConcurrency"] = "1",
            ["Worker:ImagingLimits:ScratchDirectory"] =
                Path.Combine(scratchRoot, "transform-scratch"),
            ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
            ["Platform:Authentication:ApiKeys:Peppers:v1"] = ApiKeyPepper,
            ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] = "share-version",
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
        workerServices.AddSingleton<IImageProcessor>(new ReadinessImageProcessor());
        workerServices.AddSingleton<IClock>(new ReadinessClock(Now));
        workerServices.AddSingleton<IUuid7Generator>(
            new Uuid7Generator(new ReadinessClock(Now)));
        workerServices.AddVistaraWorkerPlatform(configuration);
        ServiceProvider worker = workerServices.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        ServiceCollection uploadServices = [];
        var ownerContext = new FixedPlatformTenantContext(owner.TenantId);
        uploadServices.AddScoped<ITenantScope>(_ => ownerContext);
        uploadServices.AddScoped<IPlatformTenantContext>(_ => ownerContext);
        uploadServices.AddSingleton<IBlobStore>(store);
        uploadServices.AddSingleton<IClock>(new ReadinessClock(Now));
        uploadServices.AddSingleton<IUuid7Generator>(
            new Uuid7Generator(new ReadinessClock(Now)));
        uploadServices.AddVistaraApiPlatform(configuration);
        uploadServices.AddVistaraApiPersistence(configuration);
        ServiceProvider ownerUploads = uploadServices.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        WebApplicationBuilder apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Configuration.AddInMemoryCollection(settings);
        apiBuilder.Services.AddSingleton<IBlobStore>(store);
        apiBuilder.Services.AddVistaraApiRuntime(apiBuilder.Configuration);
        apiBuilder.Services.AddVistaraApiPlatform(apiBuilder.Configuration);
        apiBuilder.Services.AddVistaraApiPersistence(apiBuilder.Configuration);
        WebApplication api = apiBuilder.Build();
        api.UseVistaraPlatform();
        api.MapVistaraGalleryFeatures();

        var scenario = new ShareVersionScenario(
            scratchRoot,
            anchor,
            worker,
            api,
            ownerUploads,
            vistaraOptions,
            jobOptions,
            owner,
            neighbour,
            await IssueApiKeyAsync(api, owner),
            await IssueApiKeyAsync(api, neighbour));
        return scenario;
    }

    /// <summary>
    /// Uploads real bytes through the production upload application, runs the
    /// real ingest worker, then runs every required derivative so the asset
    /// reaches Ready exactly as the gallery would observe it.
    /// </summary>
    internal async ValueTask<Guid> IngestReadyAssetAsync()
    {
        int ordinal = ++_uploads;
        Guid uploadId = Guid.CreateVersion7(Now.AddMilliseconds(ordinal));
        byte[] content = Encoding.UTF8.GetBytes($"vistara-share-image-{ordinal:D4}");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using (AsyncServiceScope scope = _ownerUploads.CreateAsyncScope())
        {
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                new ReserveUploadRequest(
                    Owner.TenantId,
                    Owner.UserId,
                    uploadId,
                    "proxy",
                    $"share-{ordinal:D4}.png",
                    content.LongLength,
                    "image/png",
                    sha256,
                    $"staging/{Owner.TenantId.ToString("N")[..2]}/" +
                        $"{Owner.TenantId:D}/{uploadId:D}",
                    Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes($"share-{ordinal}"))),
                    new IdempotencyKey($"share-{ordinal}-create"),
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
                new IdempotencyKey($"share-{ordinal}-commit"),
                written.Session!.Version,
                CancellationToken.None);
            Assert.NotNull(committed.Session);
        }

        await using (AsyncServiceScope scope = _worker.CreateAsyncScope())
        {
            scope.ServiceProvider
                .GetRequiredService<IMutableTenantScope>()
                .Establish(Owner.TenantId);
            JobHandlerResult ingested = await scope.ServiceProvider
                .GetRequiredService<IngestService>()
                .ProcessAsync(Owner.TenantId, uploadId, CancellationToken.None);
            Assert.True(ingested.IsSuccess);
        }

        Guid assetId;
        await using (VistaraDbContext context = CreateContext(Owner.TenantId))
        {
            TenantKey tenantKey = Owner.TenantId;
            assetId = await context.IngestOperations
                .Where(row =>
                    row.TenantId == tenantKey &&
                    row.UploadSessionId == uploadId)
                .Select(row => row.AssetId!.Value)
                .SingleAsync();
        }

        await RunDerivativesAsync();
        Assert.Equal("Ready", await ReadAssetStatusAsync(assetId));
        return assetId;
    }

    internal async ValueTask<ApiResponse> CreateSnapshotShareAsync(
        Guid assetId,
        long version,
        string? apiKey = null,
        string idempotencyKey = "share-version-create") =>
        await SendAsync(
            HttpMethods.Post,
            "/api/v1/shares",
            apiKey: apiKey ?? OwnerApiKey,
            body: $$"""
            {
              "name": "Snapshot share",
              "targetKind": "snapshot",
              "snapshotAssets": [{ "id": "{{assetId:D}}", "version": {{version}} }],
              "permissions": {
                "view": true,
                "downloadRenditions": false,
                "downloadOriginal": false
              },
              "metadataExposure": "basic"
            }
            """,
            headers: new Dictionary<string, string>
            {
                ["Idempotency-Key"] = idempotencyKey,
            });

    /// <summary>
    /// Creates an album through the production curation endpoints and adds the
    /// asset with the very same versioned reference contract share creation
    /// uses, which is what makes the two surfaces comparable.
    /// </summary>
    internal async ValueTask<Guid> CreateAlbumWithAssetAsync(
        Guid assetId,
        long assetVersion)
    {
        ApiResponse created = await SendAsync(
            HttpMethods.Post,
            "/api/v1/albums",
            apiKey: OwnerApiKey,
            body: """
            { "name": "Shared album" }
            """,
            headers: new Dictionary<string, string>
            {
                ["Idempotency-Key"] = "share-version-album-create",
            });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        using JsonDocument body = JsonDocument.Parse(created.Body);
        JsonElement album = body.RootElement.GetProperty("album");
        Guid albumId = album.GetProperty("id").GetGuid();
        long albumVersion = album.GetProperty("version").GetInt64();

        ApiResponse added = await SendAsync(
            HttpMethods.Post,
            $"/api/v1/albums/{albumId:D}/items",
            apiKey: OwnerApiKey,
            body: $$"""
            { "items": [{ "id": "{{assetId:D}}", "version": {{assetVersion}} }] }
            """,
            headers: new Dictionary<string, string>
            {
                ["If-Match"] = $"\"v{albumVersion}\"",
                ["Idempotency-Key"] = "share-version-album-items",
            });
        Assert.Equal(HttpStatusCode.OK, added.Status);
        return albumId;
    }

    internal async ValueTask<AssetContractVersions> ReadAssetAsync(Guid assetId)
    {
        ApiResponse response = await SendAsync(
            HttpMethods.Get,
            $"/api/v1/assets/{assetId:D}",
            apiKey: OwnerApiKey);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        using JsonDocument document = JsonDocument.Parse(response.Body);
        JsonElement asset = document.RootElement.GetProperty("asset");
        return new AssetContractVersions(
            asset.GetProperty("version").GetInt64(),
            asset.GetProperty("revisionNumber").GetInt64());
    }

    /// <summary>
    /// Reads the persisted asset row for states the query API conceals, so a
    /// concealment test can still present the version a client would have held
    /// before the asset left the library.
    /// </summary>
    internal async ValueTask<AssetContractVersions> ReadAssetRowAsync(Guid assetId)
    {
        await using VistaraDbContext context = CreateContext(Owner.TenantId);
        AssetRow asset = await context.Assets
            .AsNoTracking()
            .SingleAsync(row => row.Id == assetId);
        long revision = await context.AssetRevisions
            .AsNoTracking()
            .Where(row => row.Id == asset.CurrentRevisionId)
            .Select(row => row.RevisionNumber)
            .SingleAsync();
        return new AssetContractVersions(asset.Version, revision);
    }

    internal async ValueTask<long> ReadAssetVersionAsync(Guid assetId) =>
        (await ReadAssetRowAsync(assetId)).Version;

    internal async ValueTask<string> ReadAssetStatusAsync(Guid assetId)
    {
        await using VistaraDbContext context = CreateContext(Owner.TenantId);
        return await context.Assets
            .AsNoTracking()
            .Where(row => row.Id == assetId)
            .Select(row => row.Status)
            .SingleAsync();
    }

    /// <summary>
    /// Moves the asset resource version forward without touching its blob
    /// revision, which is exactly what a metadata edit does.
    /// </summary>
    internal async ValueTask TouchAssetAsync(Guid assetId)
    {
        await using VistaraDbContext context = CreateContext(Owner.TenantId);
        AssetRow asset = await context.Assets.SingleAsync(row => row.Id == assetId);
        asset.UpdatedAtUtc = Now.AddMinutes(1);
        asset.Version = checked(asset.Version + 1);
        await context.SaveChangesAsync();
    }

    internal async ValueTask SetAssetStatusAsync(Guid assetId, string status)
    {
        await using VistaraDbContext context = CreateContext(Owner.TenantId);
        AssetRow asset = await context.Assets.SingleAsync(row => row.Id == assetId);
        asset.Status = status;
        asset.UpdatedAtUtc = Now.AddMinutes(2);
        asset.Version = checked(asset.Version + 1);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Replays a request through the production pipeline so authentication,
    /// tenant resolution, antiforgery, and authorization all apply.
    /// </summary>
    internal async Task<ApiResponse> SendAsync(
        string method,
        string path,
        string? apiKey = null,
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        RequestDelegate pipeline = ((IApplicationBuilder)_api).Build();
        await using AsyncServiceScope scope = _api.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("vistara.example");
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-share-version";

        // The hosting layer publishes the ambient context for scoped adapters;
        // replaying the pipeline directly has to do the same.
        scope.ServiceProvider
            .GetRequiredService<IHttpContextAccessor>()
            .HttpContext = context;
        if (apiKey is not null)
        {
            context.Request.Headers[
                PlatformAuthenticationDefaults.ApiKeyHeaderName] = apiKey;
        }

        if (body is not null)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = payload.LongLength;
            context.Request.Body = new MemoryStream(payload, writable: false);
        }

        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                context.Request.Headers[name] = value;
            }
        }

        await pipeline(context);
        return new ApiResponse(
            (HttpStatusCode)context.Response.StatusCode,
            Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()),
            context.Response.Headers);
    }

    private async ValueTask RunDerivativesAsync()
    {
        await using var jobs = new JobDbContext(
            _jobOptions,
            new FixedTenantScope(Owner.TenantId));
        var queue = new RelationalJobQueue(
            jobs,
            new JobQueueOptions { ConfiguredWorkerCount = 1 });
        Result<IReadOnlyList<JobLeaseAssignment>> leased = await queue.LeaseAsync(
            new JobLeaseRequest(
                new JobLeaseOwner("share-version"),
                Now,
                TimeSpan.FromHours(2),
                MaximumCount: 64),
            CancellationToken.None);
        Assert.True(
            leased.TryGetValue(out IReadOnlyList<JobLeaseAssignment>? assignments),
            leased.Error?.Message);
        foreach (JobLeaseAssignment assignment in assignments!)
        {
            if (assignment.Job.Type != DerivativeJobHandler.SupportedJobType)
            {
                continue;
            }

            JobHandlerResult result;
            await using (AsyncServiceScope scope = _worker.CreateAsyncScope())
            {
                scope.ServiceProvider
                    .GetRequiredService<IMutableTenantScope>()
                    .Establish(Owner.TenantId);
                result = await scope.ServiceProvider
                    .GetRequiredService<DerivativeJobHandler>()
                    .HandleAsync(assignment.Job, CancellationToken.None);
            }

            Assert.True(result.IsSuccess, result.Failure?.Reason.ToString());
            JobLease lease = assignment.Lease;
            _ = await queue.CompleteAsync(
                new JobCompletionRequest(
                    lease.JobId,
                    lease.Owner,
                    lease.JobVersion,
                    Now),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Issues a share-capable API key through the production key store and
    /// returns the plaintext credential presented in <c>X-API-Key</c>.
    /// </summary>
    private static async Task<string> IssueApiKeyAsync(
        WebApplication api,
        TenantIdentity identity)
    {
        Guid keyId = Guid.CreateVersion7();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        string encodedSecret = Convert.ToBase64String(secret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string prefix = $"vst_v1{keyId:N}";
        string digest = Convert.ToHexStringLower(
            HMACSHA256.HashData(Convert.FromBase64String(ApiKeyPepper), secret));
        Result<ApiKeyMetadata> metadata = ApiKeyMetadata.Create(
            new ApiKeyId(keyId),
            new Vistara.Domain.Tenancy.TenantId(identity.TenantId),
            new UserId(identity.UserId),
            prefix,
            digest,
            ApiKeyScope.ReadAssets |
                ApiKeyScope.UploadAssets |
                ApiKeyScope.ManageMetadata,
            Now,
            null);
        Assert.True(
            metadata.TryGetValue(out ApiKeyMetadata? created),
            metadata.Error?.Message);
        await using AsyncServiceScope scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider
            .GetRequiredService<IMutableTenantScope>()
            .Establish(identity.TenantId);
        Result added = await scope.ServiceProvider
            .GetRequiredService<IApiKeyStore>()
            .AddAsync(created!, CancellationToken.None);
        Assert.True(added.IsSuccess, added.Error?.Message);
        return $"{prefix}_{encodedSecret}";
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
        await _ownerUploads.DisposeAsync();
        await _api.DisposeAsync();
        await _worker.DisposeAsync();
        await _anchor.DisposeAsync();
        SqliteConnection.ClearAllPools();
        DerivativeScratchDirectory.Delete(_scratchRoot);
    }

    internal sealed record TenantIdentity(Guid TenantId, Guid UserId, string Slug)
    {
        internal static TenantIdentity Create(string slug) =>
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), slug);
    }

    private sealed class FixedPlatformTenantContext(Guid tenantId) :
        ITenantScope,
        IPlatformTenantContext
    {
        public Guid TenantId { get; } = tenantId;

        Guid? IPlatformTenantContext.TenantId => TenantId;
    }
}
