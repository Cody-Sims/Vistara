using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.Features.Media;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Application.Gallery.Queries;
using Vistara.Application.Identity;
using Vistara.Application.Jobs;
using Vistara.Application.Tenancy;
using Vistara.Auth.ApiKeys;
using Vistara.Contracts.Media;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Jobs;
using Vistara.Domain.Tenancy;
using Vistara.IntegrationTests.DerivativeWorker;
using Vistara.Persistence;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Derivatives.Worker;
using Vistara.Persistence.Gallery.Queries;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Storage.Local;
using Xunit;

namespace Vistara.IntegrationTests.MediaDelivery;

/// <summary>
/// Exercises the advertised private rendition route end to end over real
/// persistence, the production delivery adapters, real routing, and a real
/// local blob store.
/// </summary>
public sealed class AssetRenditionDeliveryTests
{
    [Fact]
    public async Task Advertised_rendition_path_streams_the_ready_derivative_to_its_owner()
    {
        await using AssetRenditionDeliveryHarness harness =
            await AssetRenditionDeliveryHarness.CreateAsync();
        ReadyRendition rendition = await harness.AddReadyRenditionAsync();

        AssetDeliverySource advertised = await harness.ReadAdvertisedRenditionAsync();
        DeliveryResponse response = await harness.SendAsync(
            advertised.Path,
            harness.OwnerPrincipal());
        DeliveryResponse ranged = await harness.SendAsync(
            advertised.Path,
            harness.OwnerPrincipal(),
            headers: new Dictionary<string, string> { ["Range"] = "bytes=2-6" });
        DeliveryResponse head = await harness.SendAsync(
            advertised.Path,
            harness.OwnerPrincipal(),
            method: HttpMethods.Head);

        Assert.Equal(
            $"/delivery/assets/{harness.AssetId:D}/{rendition.RequestId:D}",
            advertised.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rendition.Content, response.Body);
        Assert.Equal("image/webp", response.ContentType);
        Assert.Equal(rendition.Content.Length, response.ContentLength);
        Assert.Equal($"\"{rendition.Sha256}\"", response.Headers.ETag.ToString());
        Assert.Equal(
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.False(response.Headers.ContainsKey("Content-Disposition"));
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(rendition.Content[2..7], ranged.Body);
        Assert.Equal(
            $"bytes 2-6/{rendition.Content.Length}",
            ranged.Headers.ContentRange.ToString());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(head.Body);
        Assert.Equal(rendition.Content.Length, head.ContentLength);
    }

    [Fact]
    public async Task Advertised_rendition_path_never_exposes_hashes_or_storage_topology()
    {
        await using AssetRenditionDeliveryHarness harness =
            await AssetRenditionDeliveryHarness.CreateAsync();
        ReadyRendition rendition = await harness.AddReadyRenditionAsync();

        AssetDeliverySource advertised = await harness.ReadAdvertisedRenditionAsync();
        DeliveryResponse response = await harness.SendAsync(
            advertised.Path,
            harness.OwnerPrincipal());
        string headers = string.Join(
            '\n',
            response.Headers.Select(header => $"{header.Key}:{header.Value}"));

        Assert.DoesNotContain(
            rendition.SourceSha256,
            advertised.Path,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            rendition.RecipeSha256,
            advertised.Path,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            rendition.StorageKey,
            advertised.Path,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            rendition.StorageKey,
            headers,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            rendition.SourceSha256,
            headers,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            rendition.RecipeSha256,
            headers,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_and_pending_renditions_are_reported_without_content()
    {
        await using AssetRenditionDeliveryHarness harness =
            await AssetRenditionDeliveryHarness.CreateAsync();
        await harness.AddReadyRenditionAsync();
        Guid pendingId = await harness.AddPendingRenditionAsync();

        DeliveryResponse unknown = await harness.SendAsync(
            $"/delivery/assets/{harness.AssetId:D}/{Guid.CreateVersion7():D}",
            harness.OwnerPrincipal());
        DeliveryResponse pending = await harness.SendAsync(
            $"/delivery/assets/{harness.AssetId:D}/{pendingId:D}",
            harness.OwnerPrincipal());
        AssetDeliverySource[] advertised =
            await harness.ReadAdvertisedRenditionsAsync();

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("media_not_found", unknown.ProblemCode());
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            pending.Headers.CacheControl.ToString());
        Assert.DoesNotContain(
            advertised,
            source => source.Path.Contains(
                pendingId.ToString("D"),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trashing_and_purging_the_asset_conceals_its_advertised_rendition()
    {
        await using AssetRenditionDeliveryHarness harness =
            await AssetRenditionDeliveryHarness.CreateAsync();
        ReadyRendition rendition = await harness.AddReadyRenditionAsync();
        string path = $"/delivery/assets/{harness.AssetId:D}/{rendition.RequestId:D}";

        DeliveryResponse ready = await harness.SendAsync(
            path,
            harness.OwnerPrincipal());
        await harness.TrashAssetAsync();
        DeliveryResponse trashed = await harness.SendAsync(
            path,
            harness.OwnerPrincipal());
        DeliveryResponse trashedHead = await harness.SendAsync(
            path,
            harness.OwnerPrincipal(),
            method: HttpMethods.Head);
        await harness.PurgeAssetAsync();
        DeliveryResponse purged = await harness.SendAsync(
            path,
            harness.OwnerPrincipal());

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(rendition.Content, ready.Body);
        Assert.Equal(HttpStatusCode.NotFound, trashed.StatusCode);
        Assert.Equal("media_not_found", trashed.ProblemCode());
        Assert.NotEqual(rendition.Content, trashed.Body);
        Assert.Equal(HttpStatusCode.NotFound, trashedHead.StatusCode);
        Assert.Empty(trashedHead.Body);
        Assert.Equal(HttpStatusCode.NotFound, purged.StatusCode);
        Assert.All(
            new[] { trashed, purged },
            response => Assert.Equal(
                MediaDeliveryHttpContract.NoStoreCacheControl,
                response.Headers.CacheControl.ToString()));
    }

    [Fact]
    public async Task Real_api_key_streams_the_advertised_rendition_through_the_platform()
    {
        await using AssetRenditionDeliveryHarness harness =
            await AssetRenditionDeliveryHarness.CreateAsync();
        ReadyRendition rendition = await harness.AddReadyRenditionAsync();
        AssetDeliverySource advertised = await harness.ReadAdvertisedRenditionAsync();
        string apiKey = await harness.IssueApiKeyAsync(
            harness.TenantId,
            harness.OwnerId);

        DeliveryResponse response = await harness.SendThroughPlatformAsync(
            advertised.Path,
            apiKey: apiKey);
        DeliveryResponse head = await harness.SendThroughPlatformAsync(
            advertised.Path,
            method: HttpMethods.Head,
            apiKey: apiKey);
        DeliveryResponse ranged = await harness.SendThroughPlatformAsync(
            advertised.Path,
            apiKey: apiKey,
            headers: new Dictionary<string, string> { ["Range"] = "bytes=4-9" });
        DeliveryResponse forged = await harness.SendThroughPlatformAsync(
            advertised.Path,
            apiKey: $"{apiKey[..^4]}zzzz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rendition.Content, response.Body);
        Assert.Equal("image/webp", response.ContentType);
        Assert.Equal($"\"{rendition.Sha256}\"", response.Headers.ETag.ToString());
        Assert.Equal(
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(head.Body);
        Assert.Equal(rendition.Content.Length, head.ContentLength);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(rendition.Content[4..10], ranged.Body);
        Assert.Equal(
            $"bytes 4-9/{rendition.Content.Length}",
            ranged.Headers.ContentRange.ToString());
        Assert.NotEqual(HttpStatusCode.OK, forged.StatusCode);
        Assert.NotEqual(rendition.Content, forged.Body);
    }

    [Fact]
    public async Task Platform_pipeline_denies_anonymous_cross_tenant_and_trashed_requests()
    {
        await using AssetRenditionDeliveryHarness harness =
            await AssetRenditionDeliveryHarness.CreateAsync();
        ReadyRendition rendition = await harness.AddReadyRenditionAsync();
        AssetDeliverySource advertised = await harness.ReadAdvertisedRenditionAsync();
        string ownerKey = await harness.IssueApiKeyAsync(
            harness.TenantId,
            harness.OwnerId);
        string neighbourKey = await harness.IssueApiKeyAsync(
            harness.OtherTenantId,
            harness.OtherUserId);

        DeliveryResponse anonymous = await harness.SendThroughPlatformAsync(
            advertised.Path);
        DeliveryResponse crossTenant = await harness.SendThroughPlatformAsync(
            advertised.Path,
            apiKey: neighbourKey);
        await harness.TrashAssetAsync();
        DeliveryResponse trashed = await harness.SendThroughPlatformAsync(
            advertised.Path,
            apiKey: ownerKey);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal("media_not_found", crossTenant.ProblemCode());
        Assert.Equal(HttpStatusCode.NotFound, trashed.StatusCode);
        Assert.Equal("media_not_found", trashed.ProblemCode());
        Assert.All(
            new[] { anonymous, crossTenant, trashed },
            response =>
            {
                Assert.NotEqual(rendition.Content, response.Body);
                Assert.DoesNotContain(
                    rendition.StorageKey,
                    response.BodyText,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    rendition.SourceSha256,
                    response.BodyText,
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task Anonymous_cross_tenant_and_revoked_principals_never_receive_bytes()
    {
        await using AssetRenditionDeliveryHarness harness =
            await AssetRenditionDeliveryHarness.CreateAsync();
        ReadyRendition rendition = await harness.AddReadyRenditionAsync();
        string path = $"/delivery/assets/{harness.AssetId:D}/{rendition.RequestId:D}";

        DeliveryResponse anonymous = await harness.SendAsync(
            path,
            new ClaimsPrincipal(new ClaimsIdentity()));
        DeliveryResponse unscoped = await harness.SendAsync(
            path,
            harness.OwnerPrincipal(scope: "assets.write"));
        DeliveryResponse crossTenant = await harness.SendCrossTenantAsync(path);
        await harness.RevokeOwnerMembershipAsync();
        DeliveryResponse revoked = await harness.SendAsync(
            path,
            harness.OwnerPrincipal());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("authentication_required", anonymous.ProblemCode());
        Assert.Equal(HttpStatusCode.NotFound, unscoped.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal("media_not_found", crossTenant.ProblemCode());
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.All(
            new[] { anonymous, unscoped, crossTenant, revoked },
            response =>
            {
                Assert.Equal("application/problem+json", response.ContentType);
                Assert.Equal(
                    MediaDeliveryHttpContract.NoStoreCacheControl,
                    response.Headers.CacheControl.ToString());
                Assert.DoesNotContain(
                    rendition.StorageKey,
                    response.BodyText,
                    StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(rendition.Content, response.Body);
            });
    }
}

internal sealed class RenditionBlobContent(byte[] bytes) : IReplayableBlobContent
{
    public long Length => bytes.LongLength;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(
            new MemoryStream(bytes, writable: false));
    }
}

internal sealed record ReadyRendition(
    Guid RequestId,
    byte[] Content,
    string Sha256,
    string StorageKey,
    string SourceSha256,
    string RecipeSha256);

internal sealed record DeliveryResponse(
    HttpStatusCode StatusCode,
    string? ContentType,
    long? ContentLength,
    IHeaderDictionary Headers,
    byte[] Body)
{
    internal string BodyText => Encoding.UTF8.GetString(Body);

    internal string ProblemCode() =>
        JsonDocument.Parse(Body).RootElement.GetProperty("code").GetString()!;
}

internal sealed class AssetRenditionDeliveryHarness : IAsyncDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly ImagePipelineFingerprint Fingerprint =
        new("asset-rendition-delivery");
    private const string ApiKeyPepper =
        "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=";

    private readonly string _scratchRoot;
    private readonly WebApplication _app;
    private readonly WebApplication _platformApp;
    private readonly DbContextOptions<VistaraDbContext> _vistaraOptions;
    private readonly DbContextOptions<JobDbContext> _jobOptions;
    private readonly LocalBlobStore _blobStore;
    private readonly MutableClock _clock = new(Now);
    private readonly Guid _revisionId;
    private readonly string _sourceSha256;

    private AssetRenditionDeliveryHarness(
        string scratchRoot,
        WebApplication app,
        WebApplication platformApp,
        DbContextOptions<VistaraDbContext> vistaraOptions,
        DbContextOptions<JobDbContext> jobOptions,
        LocalBlobStore blobStore,
        Guid tenantId,
        Guid ownerId,
        Guid assetId,
        Guid revisionId,
        string sourceSha256,
        Guid otherTenantId,
        Guid otherUserId)
    {
        _scratchRoot = scratchRoot;
        _app = app;
        _platformApp = platformApp;
        _vistaraOptions = vistaraOptions;
        _jobOptions = jobOptions;
        _blobStore = blobStore;
        _revisionId = revisionId;
        _sourceSha256 = sourceSha256;
        TenantId = tenantId;
        OwnerId = ownerId;
        AssetId = assetId;
        OtherTenantId = otherTenantId;
        OtherUserId = otherUserId;
    }

    internal Guid TenantId { get; }

    internal Guid OwnerId { get; }

    internal Guid AssetId { get; }

    internal Guid OtherTenantId { get; }

    internal Guid OtherUserId { get; }

    internal static async ValueTask<AssetRenditionDeliveryHarness> CreateAsync()
    {
        string scratchRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"asset-rendition-delivery-{Guid.NewGuid():N}");
        string mediaRoot = Path.Combine(scratchRoot, "media");
        Directory.CreateDirectory(mediaRoot);
        string databasePath = Path.Combine(scratchRoot, "delivery.db");
        string connectionString = $"Data Source={databasePath}";
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid revisionId = Guid.CreateVersion7();
        Guid blobId = Guid.CreateVersion7();
        Guid otherTenantId = Guid.CreateVersion7();
        Guid otherUserId = Guid.CreateVersion7();
        string sourceSha256 = Convert.ToHexStringLower(
            SHA256.HashData("asset-rendition-source"u8));

        var vistaraOptions = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var jobOptions = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var schema = new VistaraDbContext(
            vistaraOptions,
            new FixedTenantScope(tenantId)))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await SeedTenantAsync(vistaraOptions, tenantId, ownerId, "owner");
        await SeedTenantAsync(
            vistaraOptions,
            otherTenantId,
            otherUserId,
            "neighbour");
        await SeedAssetAsync(
            vistaraOptions,
            tenantId,
            ownerId,
            assetId,
            revisionId,
            blobId,
            sourceSha256);

        var blobStore = new LocalBlobStore(new LocalBlobStoreOptions(mediaRoot));
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = connectionString,
            ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
            ["Platform:Authentication:ApiKeys:Peppers:v1"] = ApiKeyPepper,
            ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] =
                "asset-rendition-delivery",
            ["Platform:Authentication:Jwt:Issuers:0:Issuer"] =
                "https://issuer.example",
            ["Platform:Authentication:Jwt:Issuers:0:Audience"] = "vistara-api",
            ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
                "https://issuer.example/.well-known/openid-configuration",
            ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] =
                "RS256",
        };
        return new AssetRenditionDeliveryHarness(
            scratchRoot,
            CreateEndpointHost(settings, blobStore),
            CreatePlatformHost(settings, blobStore),
            vistaraOptions,
            jobOptions,
            blobStore,
            tenantId,
            ownerId,
            assetId,
            revisionId,
            sourceSha256,
            otherTenantId,
            otherUserId);
    }

    internal ClaimsPrincipal OwnerPrincipal(string scope = "assets.read") =>
        Principal(TenantId, OwnerId, scope);

    /// <summary>
    /// Composes the delivery endpoints alone so tests can vary the principal
    /// directly. The platform host below proves the same endpoints behind the
    /// production middleware.
    /// </summary>
    private static WebApplication CreateEndpointHost(
        IReadOnlyDictionary<string, string?> settings,
        LocalBlobStore blobStore)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton<IBlobStore>(blobStore);
        builder.Services.AddVistaraApiPlatform(builder.Configuration);
        builder.Services.AddVistaraApiPersistence(builder.Configuration);
        WebApplication app = builder.Build();
        app.UseRouting();
#pragma warning disable ASP0014
        app.UseEndpoints(static _ => { });
#pragma warning restore ASP0014
        app.MapVistaraMedia();
        return app;
    }

    /// <summary>
    /// Composes the production pipeline: rate limiting, authentication, tenant
    /// context, antiforgery, and authorization, so requests carry only the
    /// credentials a browser or client would send.
    /// </summary>
    private static WebApplication CreatePlatformHost(
        IReadOnlyDictionary<string, string?> settings,
        LocalBlobStore blobStore)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton<IBlobStore>(blobStore);
        builder.Services.AddVistaraApiRuntime(builder.Configuration);
        builder.Services.AddVistaraApiPlatform(builder.Configuration);
        builder.Services.AddVistaraApiPersistence(builder.Configuration);
        WebApplication app = builder.Build();
        app.UseVistaraPlatform();
        app.MapVistaraMedia();
        return app;
    }

    /// <summary>
    /// Issues a read-scoped API key through the production key store and
    /// returns the plaintext credential presented in <c>X-API-Key</c>.
    /// </summary>
    internal async Task<string> IssueApiKeyAsync(Guid tenantId, Guid userId)
    {
        Guid keyId = Guid.CreateVersion7();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        string encodedSecret = Convert.ToBase64String(secret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string prefix = $"vst_v1{keyId:N}";
        string digest = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Convert.FromBase64String(ApiKeyPepper),
                secret));
        Result<ApiKeyMetadata> metadata = ApiKeyMetadata.Create(
            new ApiKeyId(keyId),
            new TenantId(tenantId),
            new UserId(userId),
            prefix,
            digest,
            ApiKeyScope.ReadAssets,
            DateTimeOffset.UtcNow,
            null);
        Assert.True(
            metadata.TryGetValue(out ApiKeyMetadata? created),
            metadata.Error?.Message);
        await using AsyncServiceScope scope =
            _platformApp.Services.CreateAsyncScope();
        scope.ServiceProvider
            .GetRequiredService<IMutableTenantScope>()
            .Establish(tenantId);
        Result added = await scope.ServiceProvider
            .GetRequiredService<IApiKeyStore>()
            .AddAsync(created!, CancellationToken.None);
        Assert.True(added.IsSuccess, added.Error?.Message);
        return $"{prefix}_{encodedSecret}";
    }

    /// <summary>
    /// Replays a request through the production pipeline with no ambient tenant
    /// scope, exactly as an unauthenticated socket would arrive.
    /// </summary>
    internal async Task<DeliveryResponse> SendThroughPlatformAsync(
        string path,
        string method = "GET",
        string? apiKey = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        RequestDelegate pipeline = ((IApplicationBuilder)_platformApp).Build();
        await using AsyncServiceScope scope =
            _platformApp.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("vistara.example");
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-platform-rendition";
        if (apiKey is not null)
        {
            context.Request.Headers[
                PlatformAuthenticationDefaults.ApiKeyHeaderName] = apiKey;
        }

        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                context.Request.Headers[name] = value;
            }
        }

        await pipeline(context);
        return new DeliveryResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.ContentLength,
            context.Response.Headers,
            ((MemoryStream)context.Response.Body).ToArray());
    }

    internal Task TrashAssetAsync() => SetAssetStatusAsync("Trashed");

    internal Task PurgeAssetAsync() => SetAssetStatusAsync("Purged");

    private async Task SetAssetStatusAsync(string status)
    {
        await using var context = new VistaraDbContext(
            _vistaraOptions,
            new FixedTenantScope(TenantId));
        AssetRow asset = await context.Assets.SingleAsync(
            row => row.Id == AssetId);
        asset.Status = status;
        asset.UpdatedAtUtc = Now;
        asset.Version++;
        await context.SaveChangesAsync();
    }

    internal async Task<ReadyRendition> AddReadyRenditionAsync()
    {
        DerivativeGenerationRequest generation = Generation("thumb");
        DerivativeJobPayloadV1 payload =
            DerivativeJobContract.CreatePayload(generation);
        Guid requestId = await SubmitAsync(payload, "ready-rendition");
        JobLeaseAssignment assignment = await LeaseAsync();
        var statePort = new RelationalDerivativeStatePort(
            _vistaraOptions,
            new TestMutableTenantScope(TenantId),
            _clock);
        DerivativeAcquireResult acquired = await statePort.AcquireAsync(
            new DerivativeAcquireRequest(
                TenantId,
                assignment.Job.Id.Value,
                payload,
                _blobStore.Name,
                Fingerprint,
                assignment.Lease,
                _clock.UtcNow,
                TimeSpan.FromMinutes(5)),
            CancellationToken.None);
        Assert.Equal(DerivativeAcquireDisposition.Acquired, acquired.Disposition);

        byte[] content = Encoding.ASCII.GetBytes("0123456789abcdef");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var key = new BlobKey(generation.CacheKey.Value);
        _ = await _blobStore.PutAsync(
            key,
            new RenditionBlobContent(content),
            new BlobWriteOptions(
                new BlobMediaType("image/webp"),
                checksums:
                [
                    new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256),
                ],
                conditions: BlobRequestConditions.CreateOnly),
            CancellationToken.None);
        BlobHead? head = await _blobStore.HeadAsync(key, CancellationToken.None);
        Assert.NotNull(head);

        Assert.Equal(
            DerivativeStateWriteResult.Applied,
            await statePort.MarkReadyAsync(
                new DerivativeReadyOutput(
                    acquired.Fence!.Value,
                    new DerivativeGenerationResult(
                        generation.Identity,
                        generation.CacheKey,
                        generation.Output,
                        content.LongLength,
                        new ImageSha256(sha256)),
                    head,
                    _clock.UtcNow),
                CancellationToken.None));

        return new ReadyRendition(
            requestId,
            content,
            sha256,
            key.Value,
            payload.Generation.SourceSha256,
            payload.Generation.RecipeSha256);
    }

    internal Task<Guid> AddPendingRenditionAsync() =>
        SubmitAsync(
            DerivativeJobContract.CreatePayload(Generation("grid")),
            "pending-rendition");

    internal async Task<AssetDeliverySource> ReadAdvertisedRenditionAsync()
    {
        AssetDeliverySource[] renditions = await ReadAdvertisedRenditionsAsync();
        return Assert.Single(
            renditions,
            rendition => rendition.Path.StartsWith(
                "/delivery/assets/",
                StringComparison.Ordinal));
    }

    internal async Task<AssetDeliverySource[]> ReadAdvertisedRenditionsAsync()
    {
        await using var context = new VistaraDbContext(
            _vistaraOptions,
            new FixedTenantScope(TenantId));
        var store = new RelationalAssetQueryStore(context);
        AssetQuerySlice slice = await store.QueryAsync(
            new AssetQueryScope(TenantId, OwnerId),
            AssetQueryCriteria.Create(),
            new AssetQueryWindow(Now.AddDays(1), Continuation: null),
            CancellationToken.None);
        AssetQueryItem item = Assert.Single(slice.Items);
        return [.. item.Renditions];
    }

    internal async Task RevokeOwnerMembershipAsync()
    {
        await using var context = new VistaraDbContext(
            _vistaraOptions,
            new FixedTenantScope(TenantId));
        TenantMembershipRow membership = await context.TenantMemberships
            .SingleAsync(row => row.UserId == OwnerId);
        membership.Status = "Suspended";
        membership.UpdatedAtUtc = Now;
        await context.SaveChangesAsync();
    }

    internal Task<DeliveryResponse> SendCrossTenantAsync(string path) =>
        SendAsync(
            path,
            Principal(OtherTenantId, OtherUserId, "assets.read"),
            tenantId: OtherTenantId);

    internal async Task<DeliveryResponse> SendAsync(
        string path,
        ClaimsPrincipal user,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        Guid? tenantId = null)
    {
        RequestDelegate pipeline = ((IApplicationBuilder)_app).Build();
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        if (user.Identity?.IsAuthenticated == true)
        {
            scope.ServiceProvider
                .GetRequiredService<IMutableTenantScope>()
                .Establish(tenantId ?? TenantId);
        }

        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = user,
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-asset-rendition";
        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                context.Request.Headers[name] = value;
            }
        }

        await pipeline(context);
        return new DeliveryResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.ContentLength,
            context.Response.Headers,
            ((MemoryStream)context.Response.Body).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _platformApp.DisposeAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_scratchRoot))
        {
            Directory.Delete(_scratchRoot, recursive: true);
        }
    }

    private static ClaimsPrincipal Principal(
        Guid tenantId,
        Guid userId,
        string scope) =>
        new(new ClaimsIdentity(
            [
                new Claim("scope", scope),
                new Claim("tenant_id", tenantId.ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString("D")),
            ],
            "Test"));

    private DerivativeGenerationRequest Generation(string presetName) =>
        Assert.IsType<DerivativeGenerationRequest>(
            DerivativePresetRegistry.Standard.ResolveDefault(
                new DerivativeSourceIdentity(
                    TenantId,
                    AssetId,
                    _revisionId,
                    revisionNumber: 1,
                    new ImageSha256(_sourceSha256)),
                presetName,
                Fingerprint)
            .GenerationRequest);

    private async Task<Guid> SubmitAsync(
        DerivativeJobPayloadV1 payload,
        string idempotencyKey)
    {
        Guid requestId = Guid.CreateVersion7();
        await using var context = new VistaraDbContext(
            _vistaraOptions,
            new FixedTenantScope(TenantId));
        var store = new RelationalDerivativeRequestStore(
            context,
            new FixedTenantScope(TenantId));
        PersistedDerivativeSubmissionResult submission = await store.SubmitAsync(
            new PersistedDerivativeSubmission(
                requestId,
                requestId,
                idempotencyKey,
                Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))),
                payload,
                isPublic: false,
                _clock.UtcNow),
            CancellationToken.None);
        Assert.Equal(
            PersistedDerivativeSubmissionStatus.Created,
            submission.Status);
        return requestId;
    }

    private async Task<JobLeaseAssignment> LeaseAsync()
    {
        await using var jobs = new JobDbContext(
            _jobOptions,
            new FixedTenantScope(TenantId));
        var queue = new RelationalJobQueue(
            jobs,
            new JobQueueOptions { ConfiguredWorkerCount = 1 });
        Result<IReadOnlyList<JobLeaseAssignment>> leased = await queue.LeaseAsync(
            new JobLeaseRequest(
                new JobLeaseOwner("asset-rendition-worker"),
                _clock.UtcNow,
                TimeSpan.FromMinutes(10),
                MaximumCount: 1),
            CancellationToken.None);
        Assert.True(
            leased.TryGetValue(out IReadOnlyList<JobLeaseAssignment>? assignments),
            leased.Error?.Message);
        return Assert.Single(assignments!);
    }

    private static async Task SeedTenantAsync(
        DbContextOptions<VistaraDbContext> options,
        Guid tenantId,
        Guid userId,
        string slug)
    {
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = slug,
            Name = slug,
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.Users.Add(new UserRow
        {
            Id = userId,
            NormalizedEmail = $"{slug}@example.invalid",
            DisplayName = slug,
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = tenantId,
            UserId = userId,
            Role = "TenantOwner",
            Status = "Active",
            InvitedAtUtc = Now,
            JoinedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
    }

    private static async Task SeedAssetAsync(
        DbContextOptions<VistaraDbContext> options,
        Guid tenantId,
        Guid ownerId,
        Guid assetId,
        Guid revisionId,
        Guid blobId,
        string sourceSha256)
    {
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = tenantId,
            Provider = "local",
            Container = "media",
            ObjectKey = $"originals/{tenantId:N}/{assetId:N}/1.png",
            ProviderVersion = "source-v1",
            Sha256 = sourceSha256,
            SizeBytes = 64,
            ContentType = "image/png",
            State = "Active",
            CreatedAtUtc = Now,
        });
        var asset = new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = ownerId,
            Title = "Rendition source",
            Status = "Ready",
            Visibility = "Private",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        };
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = revisionId,
            TenantId = tenantId,
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = blobId,
            DetectedFormat = "png",
            DetectedContentType = "image/png",
            Width = 1_024,
            Height = 768,
            SafeMetadataJson = "{}",
            PrivateMetadataJson = "{}",
            FrameCount = 1,
            CreatedAtUtc = Now,
        });
        await context.SaveChangesAsync();
        asset.CurrentRevisionId = revisionId;
        await context.SaveChangesAsync();
    }
}
