using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Albums;
using Vistara.Api.Features.Favorites;
using Vistara.Api.Features.Tags;
using Vistara.Application.Common;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Albums;
using Vistara.Application.Gallery.Favorites;
using Vistara.Application.Gallery.Tags;
using Xunit;

namespace Vistara.Api.ContractTests.GalleryCuration;

public sealed class AlbumsTagsFavoritesEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000401");
    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000402");
    private static readonly Guid AlbumId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000403");
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000404");
    private static readonly Guid TagId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000405");
    private static readonly Guid JobId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000406");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Album_create_requires_idempotency_and_returns_strong_etag()
    {
        var albums = new FakeAlbumApplication
        {
            CreateResult = CurationResult.Success(Album(version: 1)),
        };

        TestResponse missing = await SendAsync(
            albums: albums,
            method: "POST",
            route: "/api/v1/albums",
            body: """{"name":"Road Trip"}""");
        TestResponse created = await SendAsync(
            albums: albums,
            method: "POST",
            route: "/api/v1/albums",
            body: """{"name":"Road Trip"}""",
            idempotencyKey: "album-create-1");

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_idempotency_key", ProblemCode(missing));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"v1\"", created.Headers.ETag.ToString());
        Assert.Equal("no-store", created.Headers.CacheControl.ToString());
        Assert.Equal("album-create-1", albums.LastIdempotencyKey);
        Assert.Equal("Road Trip", created.Json().RootElement
            .GetProperty("album").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Album_mutations_require_if_match_and_preserve_requested_order()
    {
        var albums = new FakeAlbumApplication
        {
            ReorderResult = CurationResult.Success(Album(version: 4)),
        };

        TestResponse missing = await SendAsync(
            albums: albums,
            method: "PATCH",
            route: "/api/v1/albums/{id:guid}/items/order",
            body:
                $"{{\"items\":[{{\"assetId\":\"{AssetId:D}\",\"position\":0}}]}}",
            idempotencyKey: "album-order-1");
        TestResponse reordered = await SendAsync(
            albums: albums,
            method: "PATCH",
            route: "/api/v1/albums/{id:guid}/items/order",
            body:
                $"{{\"items\":[{{\"assetId\":\"{AssetId:D}\",\"position\":0}}]}}",
            idempotencyKey: "album-order-1",
            ifMatch: "\"v3\"");

        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal("if_match_required", ProblemCode(missing));
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        Assert.Equal("\"v4\"", reordered.Headers.ETag.ToString());
        Assert.Equal(3, albums.LastExpectedVersion);
        Assert.Equal(
            new AlbumItemPosition(AssetId, 0),
            Assert.Single(albums.LastOrder!));
    }

    [Fact]
    public async Task Stale_tag_version_and_duplicate_name_are_explicit_conflicts()
    {
        var tags = new FakeTagApplication
        {
            UpdateResult = CurationResult.Failure<TagSnapshot>(
                CurationFailure.Conflict("tag_name_conflict")),
        };

        TestResponse response = await SendAsync(
            tags: tags,
            method: "PATCH",
            route: "/api/v1/tags/{id:guid}",
            body: """{"name":" TRAVEL "}""",
            idempotencyKey: "tag-update-1",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tag_name_conflict", ProblemCode(response));
        Assert.Equal(2, tags.LastExpectedVersion);
        Assert.Equal(" TRAVEL ", tags.LastUpdate?.Name.Value);
    }

    [Fact]
    public async Task Favorite_retry_is_idempotent_and_bulk_submission_keeps_per_item_versions()
    {
        var favorites = new FakeFavoriteApplication
        {
            SetResult = CurationResult.Success(Asset(version: 7)),
            BulkResult = CurationResult.Success(
                new BulkCurationSubmission(JobId, "queued", 1, Now)),
        };

        TestResponse favorite = await SendAsync(
            favorites: favorites,
            method: "PUT",
            route: "/api/v1/assets/{id:guid}/favorite",
            idempotencyKey: "favorite-1",
            ifMatch: "\"v6\"");
        TestResponse bulk = await SendAsync(
            favorites: favorites,
            method: "POST",
            route: "/api/v1/assets/bulk",
            body:
                $"{{\"items\":[{{\"id\":\"{AssetId:D}\",\"version\":6}}],\"action\":{{\"kind\":\"setFavorite\",\"favorite\":true}}}}",
            idempotencyKey: "bulk-favorite-1");

        Assert.Equal(HttpStatusCode.OK, favorite.StatusCode);
        Assert.Equal("\"v7\"", favorite.Headers.ETag.ToString());
        Assert.True(favorites.LastFavorite);
        Assert.Equal(6, favorites.LastExpectedVersion);
        Assert.Equal(HttpStatusCode.Accepted, bulk.StatusCode);
        Assert.Equal(AssetId, Assert.Single(favorites.LastBulk!.Items).AssetId);
        Assert.Equal(6, Assert.Single(favorites.LastBulk.Items).Version);
        Assert.Equal("setFavorite", favorites.LastBulk.Action.Kind);
    }

    [Fact]
    public async Task Concealed_resources_return_not_found_without_calling_application()
    {
        var authorization = new FakeGalleryCurationAuthorization
        {
            Access = GalleryCurationAccess.Denied(GalleryCurationAccessStatus.Concealed),
        };
        var albums = new FakeAlbumApplication();

        TestResponse response = await SendAsync(
            authorization: authorization,
            albums: albums,
            method: "GET",
            route: "/api/v1/albums/{id:guid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, albums.GetCalls);
    }

    [Fact]
    public async Task Bulk_size_is_bounded_before_application_execution()
    {
        var favorites = new FakeFavoriteApplication();
        string items = string.Join(
            ',',
            Enumerable.Range(0, 201).Select(_ =>
                $"{{\"id\":\"{Guid.CreateVersion7():D}\",\"version\":1}}"));

        TestResponse response = await SendAsync(
            favorites: favorites,
            method: "POST",
            route: "/api/v1/assets/bulk",
            body:
                $"{{\"items\":[{items}],\"action\":{{\"kind\":\"setFavorite\",\"favorite\":true}}}}",
            idempotencyKey: "bulk-too-large");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, favorites.BulkCalls);
    }

    private static string ProblemCode(TestResponse response) =>
        response.Json().RootElement.GetProperty("code").GetString()!;

    private static AlbumSnapshot Album(long version) =>
        new(
            AlbumId,
            "Road Trip",
            null,
            null,
            0,
            Now,
            version,
            []);

    private static CuratedAssetSnapshot Asset(long version) =>
        new(
            AssetId,
            "Image",
            null,
            "Ready",
            "Private",
            1,
            "image/jpeg",
            "jpeg",
            100,
            80,
            1234,
            null,
            Now,
            Now,
            true,
            [],
            [],
            version);

    private static async Task<TestResponse> SendAsync(
        string method,
        string route,
        string? body = null,
        string? idempotencyKey = null,
        string? ifMatch = null,
        FakeGalleryCurationAuthorization? authorization = null,
        FakeAlbumApplication? albums = null,
        FakeTagApplication? tags = null,
        FakeFavoriteApplication? favorites = null)
    {
        authorization ??= new FakeGalleryCurationAuthorization();
        albums ??= new FakeAlbumApplication();
        tags ??= new FakeTagApplication();
        favorites ??= new FakeFavoriteApplication();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IGalleryCurationAuthorizationPort>(_ => authorization);
        builder.Services.AddScoped<IAlbumApplication>(_ => albums);
        builder.Services.AddScoped<ITagApplication>(_ => tags);
        builder.Services.AddScoped<IFavoriteApplication>(_ => favorites);
        builder.Services.AddSingleton<IClock>(new FixedClock(Now));
        builder.Services.AddSingleton<IUuid7Generator>(new FixedUuid7Generator(
            route.Contains("tags", StringComparison.Ordinal) ? TagId : AlbumId));
        await using WebApplication app = builder.Build();
        app.MapVistaraAlbums();
        app.MapVistaraTags();
        app.MapVistaraFavorites();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains(method, StringComparer.Ordinal));

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-gallery-curation";
        context.Request.RouteValues["id"] = route.Contains("/albums/", StringComparison.Ordinal)
            ? AlbumId.ToString("D")
            : AssetId.ToString("D");
        context.Request.RouteValues["tagId"] = TagId.ToString("D");
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }

        if (idempotencyKey is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers,
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        IHeaderDictionary Headers,
        string Body)
    {
        public JsonDocument Json() => JsonDocument.Parse(Body);
    }
}

internal sealed class FakeGalleryCurationAuthorization :
    IGalleryCurationAuthorizationPort
{
    public GalleryCurationAccess Access { get; init; } =
        GalleryCurationAccess.Authorized(
            new CurationActor(
                Guid.Parse("01990a2a-bc00-7000-8000-000000000401"),
                Guid.Parse("01990a2a-bc00-7000-8000-000000000402"),
                canManageAll: false));

    public ValueTask<GalleryCurationAccess> AuthorizeAsync(
        HttpContext context,
        GalleryCurationOperation operation,
        Guid? resourceId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Access);
}

internal sealed class FakeAlbumApplication : IAlbumApplication
{
    public CurationResult<AlbumSnapshot> CreateResult { get; init; } =
        CurationResult.Failure<AlbumSnapshot>(CurationFailure.NotFound("album_not_found"));

    public CurationResult<AlbumSnapshot> ReorderResult { get; init; } =
        CurationResult.Failure<AlbumSnapshot>(CurationFailure.NotFound("album_not_found"));

    public string? LastIdempotencyKey { get; private set; }

    public long? LastExpectedVersion { get; private set; }

    public IReadOnlyList<AlbumItemPosition>? LastOrder { get; private set; }

    public int GetCalls { get; private set; }

    public ValueTask<CurationResult<IReadOnlyList<AlbumSnapshot>>> ListAsync(
        CurationActor actor,
        int limit,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CurationResult.Success<IReadOnlyList<AlbumSnapshot>>([]));

    public ValueTask<CurationResult<AlbumSnapshot>> GetAsync(
        CurationActor actor,
        Guid albumId,
        CancellationToken cancellationToken)
    {
        GetCalls++;
        return ValueTask.FromResult(CreateResult);
    }

    public ValueTask<CurationResult<AlbumSnapshot>> CreateAsync(
        CurationActor actor,
        Guid albumId,
        string name,
        string? description,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        LastIdempotencyKey = idempotencyKey;
        return ValueTask.FromResult(CreateResult);
    }

    public ValueTask<CurationResult<AlbumSnapshot>> UpdateAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        AlbumUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CreateResult);

    public ValueTask<CurationResult<bool>> DeleteAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CurationResult.Success(true));

    public ValueTask<CurationResult<AlbumSnapshot>> AddItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CreateResult);

    public ValueTask<CurationResult<AlbumSnapshot>> RemoveItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CreateResult);

    public ValueTask<CurationResult<AlbumSnapshot>> ReorderItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<AlbumItemPosition> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        LastExpectedVersion = expectedVersion;
        LastOrder = items;
        return ValueTask.FromResult(ReorderResult);
    }
}

internal sealed class FakeTagApplication : ITagApplication
{
    public CurationResult<TagSnapshot> UpdateResult { get; init; } =
        CurationResult.Failure<TagSnapshot>(CurationFailure.NotFound("tag_not_found"));

    public long? LastExpectedVersion { get; private set; }

    public TagUpdate? LastUpdate { get; private set; }

    public ValueTask<CurationResult<IReadOnlyList<TagSnapshot>>> ListAsync(
        CurationActor actor,
        int limit,
        string? search,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CurationResult.Success<IReadOnlyList<TagSnapshot>>([]));

    public ValueTask<CurationResult<TagSnapshot>> CreateAsync(
        CurationActor actor,
        Guid tagId,
        string name,
        string? color,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UpdateResult);

    public ValueTask<CurationResult<TagSnapshot>> UpdateAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        TagUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        LastExpectedVersion = expectedVersion;
        LastUpdate = update;
        return ValueTask.FromResult(UpdateResult);
    }

    public ValueTask<CurationResult<bool>> DeleteAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CurationResult.Success(true));

    public ValueTask<CurationResult<CuratedAssetSnapshot>> SetAssetTagAsync(
        CurationActor actor,
        Guid assetId,
        Guid tagId,
        long expectedAssetVersion,
        bool tagged,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CurationResult.Failure<CuratedAssetSnapshot>(
            CurationFailure.NotFound("asset_not_found")));
}

internal sealed class FakeFavoriteApplication : IFavoriteApplication
{
    public CurationResult<CuratedAssetSnapshot> SetResult { get; init; } =
        CurationResult.Failure<CuratedAssetSnapshot>(CurationFailure.NotFound(
            "asset_not_found"));

    public CurationResult<BulkCurationSubmission> BulkResult { get; init; } =
        CurationResult.Failure<BulkCurationSubmission>(CurationFailure.Invalid(
            "bulk_invalid"));

    public bool? LastFavorite { get; private set; }

    public long? LastExpectedVersion { get; private set; }

    public BulkCurationRequest? LastBulk { get; private set; }

    public int BulkCalls { get; private set; }

    public ValueTask<CurationResult<CuratedAssetSnapshot>> SetAsync(
        CurationActor actor,
        Guid assetId,
        long expectedVersion,
        bool favorite,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        LastFavorite = favorite;
        LastExpectedVersion = expectedVersion;
        return ValueTask.FromResult(SetResult);
    }

    public ValueTask<CurationResult<BulkCurationSubmission>> QueueBulkAsync(
        CurationActor actor,
        Guid jobId,
        BulkCurationRequest request,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        BulkCalls++;
        LastBulk = request;
        return ValueTask.FromResult(BulkResult);
    }
}

internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}

internal sealed class FixedUuid7Generator(Guid id) : IUuid7Generator
{
    public Guid NewId() => id;
}
