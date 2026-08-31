using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Assets;
using Vistara.Application.Common;
using Vistara.Application.Gallery.Queries;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Queries;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.Api.ContractTests.AssetQueries;

/// <summary>
/// A facet value is the argument a client sends straight back as a filter, so
/// these tests read the real relational facets through the real endpoints and
/// then feed the published value back in. A stored <c>Ready</c> would look
/// plausible in the browser and answer 400 on the very next request.
/// </summary>
public sealed class StatusFacetContractTests : IAsyncLifetime, IDisposable
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000007c1");
    private static readonly Guid ActorId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000007c2");
    private static readonly Guid ReadyAssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000007c3");
    private static readonly Guid ProcessingAssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000007c4");
    private static readonly Guid TrashedAssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000007c5");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] CursorKey =
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using VistaraDbContext schema = CreateContext();
        await schema.Database.EnsureCreatedAsync();
        await SeedTenantAsync();
        await SeedAssetAsync(ReadyAssetId, "Ready lake", "Ready");
        await SeedAssetAsync(ProcessingAssetId, "Processing ridge", "Processing");
        await SeedAssetAsync(TrashedAssetId, "Trashed dune", "Trashed");
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Status_facets_publish_documented_tokens_with_readable_labels()
    {
        TestResponse response = await SendAsync("/api/v1/search/facets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement status = SingleGroup(response, "status");
        (string Value, string Label)[] values = status
            .GetProperty("values")
            .EnumerateArray()
            .Select(value => (
                value.GetProperty("value").GetString()!,
                value.GetProperty("label").GetString()!))
            .OrderBy(value => value.Item1, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [("processing", "Processing"), ("ready", "Ready")],
            values);
    }

    [Fact]
    public async Task No_stored_status_name_leaks_from_the_facet_payload()
    {
        TestResponse response = await SendAsync("/api/v1/search/facets");

        JsonElement status = SingleGroup(response, "status");
        Assert.All(
            status.GetProperty("values").EnumerateArray(),
            value => Assert.Contains(
                value.GetProperty("value").GetString(),
                AssetContractVocabularyTokens));
        foreach (string stored in (string[])["Processing", "Ready", "Failed", "Trashed", "Purged"])
        {
            Assert.DoesNotContain(
                $"\"value\":\"{stored}\"",
                response.Body,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("/api/v1/assets")]
    [InlineData("/api/v1/timeline")]
    public async Task The_published_facet_value_is_accepted_by_the_filter(string route)
    {
        TestResponse facets = await SendAsync("/api/v1/search/facets");
        string readyToken = SingleGroup(facets, "status")
            .GetProperty("values")
            .EnumerateArray()
            .Single(value => value.GetProperty("count").GetInt64() == 1 &&
                value.GetProperty("label").GetString() == "Ready")
            .GetProperty("value")
            .GetString()!;

        TestResponse filtered = await SendAsync(route, $"?statuses={readyToken}");

        Assert.Equal("ready", readyToken);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        Assert.Contains("Ready lake", filtered.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Processing ridge", filtered.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Trashed dune", filtered.Body, StringComparison.Ordinal);
    }

    private static readonly string[] AssetContractVocabularyTokens =
        ["processing", "ready", "failed"];

    private static JsonElement SingleGroup(TestResponse response, string name)
    {
        using JsonDocument json = JsonDocument.Parse(response.Body);
        return Assert.Single(
            json.RootElement.GetProperty("groups").EnumerateArray().ToArray(),
            group => group.GetProperty("name").GetString() == name)
            .Clone();
    }

    private async Task<TestResponse> SendAsync(string route, string? query = null)
    {
        await using VistaraDbContext context = CreateContext();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IAssetQueryAuthorizationPort>(
            _ => new FakeAssetAuthorizationPort
            {
                CollectionAccess = AssetQueryAccess.Authorized(
                    TenantId,
                    ActorId,
                    canReadRestrictedMetadata: true,
                    canUpdateMetadata: true),
            });
        builder.Services.AddScoped<IAssetQueryService>(_ => new AssetQueryService(
            new RelationalAssetQueryStore(context),
            new AssetCursorProtector(CursorKey),
            new FacetClock(Now)));
        await using WebApplication app = builder.Build();
        app.MapVistaraAssetQueries();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains("GET", StringComparer.Ordinal));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        http.Request.Method = "GET";
        http.Request.Path = route;
        http.Request.QueryString = new QueryString(query ?? string.Empty);
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        http.Response.Body.Position = 0;
        string body = await new StreamReader(http.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse((HttpStatusCode)http.Response.StatusCode, body);
    }

    private VistaraDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(_connection)
                .Options,
            new FacetTenantScope(TenantId));

    private async Task SeedTenantAsync()
    {
        await using VistaraDbContext context = CreateContext();
        context.Tenants.Add(new TenantRow
        {
            Id = TenantId,
            TenantId = TenantId,
            Slug = "facets",
            Name = "Facets",
            Status = "Active",
            SettingsJson = "{}",
            QuotasJson = "{}",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.Users.Add(new UserRow
        {
            Id = ActorId,
            NormalizedEmail = "facets@example.test",
            DisplayName = "Facets",
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedAssetAsync(Guid assetId, string title, string status)
    {
        Guid revisionId = RelatedId(assetId, 0x55);
        Guid blobId = RelatedId(assetId, 0xAA);
        await using VistaraDbContext context = CreateContext();
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = TenantId,
            Provider = "local",
            Container = "assets",
            ObjectKey = $"tenant/{TenantId:D}/{assetId:D}",
            Sha256 = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{assetId:D}"))),
            SizeBytes = 1_024,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = Now,
        });
        var asset = new AssetRow
        {
            Id = assetId,
            TenantId = TenantId,
            OwnerId = ActorId,
            Title = title,
            Status = status,
            Visibility = "Private",
            CapturedAtUtc = Now.AddDays(-1),
            CreatedAtUtc = Now.AddDays(-1),
            UpdatedAtUtc = Now.AddDays(-1),
            Version = 1,
        };
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = revisionId,
            TenantId = TenantId,
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = blobId,
            DetectedFormat = "jpeg",
            DetectedContentType = "image/jpeg",
            Width = 800,
            Height = 600,
            FrameCount = 1,
            SafeMetadataJson = "{}",
            PrivateMetadataJson = "{}",
            CreatedAtUtc = Now,
        });
        await context.SaveChangesAsync();
        asset.CurrentRevisionId = revisionId;
        await context.SaveChangesAsync();
    }

    private static Guid RelatedId(Guid assetId, byte discriminator)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = assetId.TryWriteBytes(bytes);
        bytes[^1] ^= discriminator;
        return new Guid(bytes);
    }

    private sealed record TestResponse(HttpStatusCode StatusCode, string Body);

    private sealed class FacetClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FacetTenantScope(Guid tenantId) : ITenantScope
    {
        public Guid TenantId { get; } = tenantId;
    }
}
