using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Albums;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Albums;
using Vistara.Persistence;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Gallery.Curation;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Persistence;

/// <summary>
/// An album cover is an ordinary asset rendition. These tests read the real
/// relational projection and the real endpoint payload, because a cover that
/// invents its own kind or points at a JSON route renders a broken image.
/// </summary>
public sealed class AlbumCoverProjectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cover_projects_the_preferred_ready_preset_rendition()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId, assetId, albumId);

        AlbumSnapshot album = await ListSingleAlbumAsync(
            database.Context,
            tenantId,
            ownerId);

        Assert.NotNull(album.Cover);
        Assert.Equal("grid", album.Cover!.Kind);
        Assert.Equal(
            $"/delivery/assets/{assetId:D}/{GridRequestId:D}",
            album.Cover.Path);
        Assert.Equal(1024, album.Cover.Width);
        Assert.Equal(768, album.Cover.Height);
        Assert.Equal("image/webp", album.Cover.ContentType);
    }

    [Fact]
    public async Task Cover_falls_back_to_the_thumbnail_preset_when_no_grid_is_ready()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(
            database.Context,
            tenantId,
            ownerId,
            assetId,
            albumId,
            gridState: "Processing");

        AlbumSnapshot album = await ListSingleAlbumAsync(
            database.Context,
            tenantId,
            ownerId);

        Assert.Equal("thumb", album.Cover!.Kind);
        Assert.Equal(512, album.Cover.Width);
        Assert.Equal(
            "/media/pipeline-1/" + SourceSha + "/" + ThumbRecipeSha + ".webp",
            album.Cover.Path);
    }

    [Fact]
    public async Task Cover_is_omitted_while_no_rendition_is_ready()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(
            database.Context,
            tenantId,
            ownerId,
            assetId,
            albumId,
            gridState: "Processing",
            thumbState: "Queued",
            viewerState: "Failed");

        AlbumSnapshot album = await ListSingleAlbumAsync(
            database.Context,
            tenantId,
            ownerId);

        Assert.Null(album.Cover);
    }

    [Fact]
    public async Task Album_list_publishes_the_cover_as_a_documented_rendition()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        await SeedAsync(database.Context, tenantId, ownerId, assetId, albumId);

        (HttpStatusCode status, string body) = await ListAlbumsOverHttpAsync(
            database.Context,
            tenantId,
            ownerId);

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement cover = json.RootElement
            .GetProperty("items")[0]
            .GetProperty("cover");
        Assert.Equal("grid", cover.GetProperty("kind").GetString());
        Assert.Equal(
            $"/delivery/assets/{assetId:D}/{GridRequestId:D}",
            cover.GetProperty("path").GetString());
        Assert.Equal(1024, cover.GetProperty("width").GetInt32());
        Assert.DoesNotContain("/derivatives", body, StringComparison.Ordinal);
    }

    private static async Task<AlbumSnapshot> ListSingleAlbumAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid ownerId)
    {
        var application = new AlbumApplication(
            new RelationalGalleryCurationStore(context));
        CurationResult<IReadOnlyList<AlbumSnapshot>> result =
            await application.ListAsync(
                new CurationActor(tenantId, ownerId, canManageAll: false),
                20,
                CancellationToken.None);
        Assert.True(result.IsSuccess);
        return Assert.Single(result.Value!);
    }

    private static async Task<(HttpStatusCode Status, string Body)>
        ListAlbumsOverHttpAsync(
            VistaraDbContext context,
            Guid tenantId,
            Guid ownerId)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IGalleryCurationAuthorizationPort>(
            _ => new CoverCurationAuthorization(tenantId, ownerId));
        builder.Services.AddScoped<IAlbumApplication>(
            _ => new AlbumApplication(new RelationalGalleryCurationStore(context)));
        await using WebApplication app = builder.Build();
        app.MapVistaraAlbums();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == "/api/v1/albums" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains("GET", StringComparer.Ordinal));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        http.Request.Method = "GET";
        http.Request.Path = "/api/v1/albums";
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        http.Response.Body.Position = 0;
        string body = await new StreamReader(http.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return ((HttpStatusCode)http.Response.StatusCode, body);
    }

    private const string SourceSha =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ThumbRecipeSha =
        "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly Guid GridRequestId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009b2");
    private static readonly Guid ThumbRequestId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009b1");
    private static readonly Guid ViewerRequestId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009b3");

    private static async Task SeedAsync(
        VistaraDbContext context,
        Guid tenantId,
        Guid ownerId,
        Guid assetId,
        Guid albumId,
        string gridState = "Ready",
        string thumbState = "Ready",
        string viewerState = "Ready")
    {
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = $"tenant-{tenantId:N}",
            Name = "Tenant",
            Status = "Active",
            SettingsJson = "{}",
            QuotasJson = "{}",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.Users.Add(new UserRow
        {
            Id = ownerId,
            NormalizedEmail = $"{ownerId:N}@example.test",
            DisplayName = "User",
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        Guid blobId = Guid.CreateVersion7();
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = tenantId,
            Provider = "test",
            Container = "media",
            ObjectKey = $"originals/{tenantId:N}/{assetId:N}/1/image.jpg",
            ProviderVersion = "original-v1",
            Sha256 = SourceSha,
            SizeBytes = 4_096,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = Now,
        });
        context.Assets.Add(new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = ownerId,
            Title = "Alpine lake",
            Status = "Ready",
            Visibility = "Private",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.Albums.Add(new AlbumRow
        {
            Id = albumId,
            TenantId = tenantId,
            OwnerId = ownerId,
            Name = "Album",
            SortMode = "Manual",
            CoverAssetId = assetId,
            Version = 1,
        });
        await context.SaveChangesAsync();

        Guid revisionId = Guid.CreateVersion7();
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = revisionId,
            TenantId = tenantId,
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = blobId,
            DetectedFormat = "jpeg",
            DetectedContentType = "image/jpeg",
            Width = 4000,
            Height = 3000,
            FrameCount = 1,
            SafeMetadataJson = "{}",
            PrivateMetadataJson = "{}",
            CreatedAtUtc = Now,
        });
        Guid jobId = Guid.CreateVersion7();
        context.Jobs.Add(new JobRow
        {
            Id = jobId,
            TenantId = tenantId,
            Type = "asset.derivative.generate",
            Payload = """{"generation":{}}""",
            PayloadVersion = 2,
            DedupeKey = $"derivative:{jobId:N}",
            Priority = 0,
            MaxAttempts = 5,
            Attempts = 1,
            State = "Completed",
            AvailableAtUtc = Now,
            CreatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();

        context.Set<DerivativeRequestRow>().AddRange(
            Derivative(
                tenantId,
                assetId,
                revisionId,
                jobId,
                ThumbRequestId,
                "thumb",
                512,
                384,
                thumbState,
                isPublic: true,
                ThumbRecipeSha),
            Derivative(
                tenantId,
                assetId,
                revisionId,
                jobId,
                GridRequestId,
                "grid",
                1024,
                768,
                gridState,
                isPublic: false,
                "3333333333333333333333333333333333333333333333333333333333333333"),
            Derivative(
                tenantId,
                assetId,
                revisionId,
                jobId,
                ViewerRequestId,
                "viewer",
                2400,
                1800,
                viewerState,
                isPublic: false,
                "4444444444444444444444444444444444444444444444444444444444444444"));
        await context.SaveChangesAsync();
    }

    private static DerivativeRequestRow Derivative(
        Guid tenantId,
        Guid assetId,
        Guid revisionId,
        Guid jobId,
        Guid requestId,
        string presetName,
        int width,
        int height,
        string state,
        bool isPublic,
        string recipeSha256)
    {
        bool ready = string.Equals(state, "Ready", StringComparison.Ordinal);
        return new DerivativeRequestRow
        {
            Id = requestId,
            TenantId = tenantId,
            AssetId = assetId,
            RevisionId = revisionId,
            JobId = jobId,
            IdempotencyKey = $"cover/{presetName}",
            RequestHash = recipeSha256,
            PresetName = presetName,
            PresetRevision = 1,
            Width = width,
            Height = height,
            Fit = "cover",
            Format = "webp",
            Quality = 80,
            PipelineId = "pipeline-1",
            PipelineFingerprint = "fingerprint",
            SourceSha256 = SourceSha,
            RecipeSha256 = recipeSha256,
            GenerationIdentity = $"{presetName}-{recipeSha256[..8]}",
            CacheKey = $"derivatives/{presetName}/{recipeSha256}.webp",
            Extension = "webp",
            IsPublic = isPublic,
            State = state,
            RepresentationStorageKey = ready ? $"blobs/{recipeSha256}" : null,
            RepresentationContentLength = ready ? 4_096 : null,
            RepresentationContentType = ready ? "image/webp" : null,
            RepresentationSha256 = ready ? recipeSha256 : null,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        };
    }

    private sealed class CoverCurationAuthorization(Guid tenantId, Guid ownerId)
        : IGalleryCurationAuthorizationPort
    {
        public ValueTask<GalleryCurationAccess> AuthorizeAsync(
            HttpContext context,
            GalleryCurationOperation operation,
            Guid? resourceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GalleryCurationAccess.Authorized(
                new CurationActor(tenantId, ownerId, canManageAll: false)));
    }
}
