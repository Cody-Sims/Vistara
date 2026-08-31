using System.Text.Json;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Gallery;
using Vistara.Contracts.Lifecycle;
using Vistara.Contracts.Pagination;
using Xunit;

namespace Vistara.Api.ContractTests.AssetQueries;

/// <summary>
/// The gallery contract documents lower-camel enum tokens while storage keeps
/// the domain enum names. These tests pin the published JSON so a projection
/// can never leak <c>Ready</c> or <c>Private</c> to a browser client again.
/// </summary>
public sealed class AssetEnumSerializationContractTests
{
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000007a1");
    private static readonly DateTimeOffset Instant =
        new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Processing", "processing")]
    [InlineData("Ready", "ready")]
    [InlineData("Failed", "failed")]
    [InlineData("Trashed", "trashed")]
    [InlineData("Purged", "purged")]
    public void Stored_status_names_are_published_as_documented_tokens(
        string storedValue,
        string documentedToken)
    {
        using JsonDocument json = Serialize(Summary(status: storedValue));

        Assert.Equal(
            documentedToken,
            json.RootElement.GetProperty("status").GetString());
        Assert.Contains(documentedToken, AssetContractVocabulary.Statuses);
    }

    [Theory]
    [InlineData("Private", "private")]
    [InlineData("Tenant", "tenant")]
    [InlineData("Public", "public")]
    public void Stored_visibility_names_are_published_as_documented_tokens(
        string storedValue,
        string documentedToken)
    {
        using JsonDocument json = Serialize(Summary(visibility: storedValue));

        Assert.Equal(
            documentedToken,
            json.RootElement.GetProperty("visibility").GetString());
        Assert.Contains(documentedToken, AssetContractVocabulary.Visibilities);
    }

    [Fact]
    public void Rendition_kinds_are_published_as_the_documented_preset_tokens()
    {
        using JsonDocument json = Serialize(Summary());

        string[] kinds = json.RootElement
            .GetProperty("renditions")
            .EnumerateArray()
            .Select(rendition => rendition.GetProperty("kind").GetString()!)
            .ToArray();
        Assert.Equal(["thumb", "grid", "viewer"], kinds);
        Assert.All(
            kinds,
            kind => Assert.Contains(kind, AssetContractVocabulary.RenditionKinds));
    }

    [Fact]
    public void A_ready_private_asset_serializes_to_the_documented_gallery_payload()
    {
        string json = JsonSerializer.Serialize(
            Summary(),
            ResponseJsonOptions);

        Assert.Equal(ExpectedAssetJson, json);
    }

    [Fact]
    public void Every_asset_summary_projection_shares_the_published_vocabulary()
    {
        var summary = Summary();

        using JsonDocument album = Serialize(
            new AlbumItemResponse(summary, 1, Instant));
        using JsonDocument trash = Serialize(
            new TrashAssetResponse(
                summary with { Status = "Trashed" },
                Instant,
                Instant.AddDays(30),
                "user",
                0,
                0,
                1_024));
        using JsonDocument page = Serialize(
            new CursorPage<AssetSummaryResponse>([summary]));

        Assert.Equal(
            "private",
            album.RootElement.GetProperty("asset").GetProperty("visibility").GetString());
        Assert.Equal(
            "trashed",
            trash.RootElement.GetProperty("asset").GetProperty("status").GetString());
        Assert.Equal(
            "ready",
            page.RootElement
                .GetProperty("items")[0]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public void An_album_cover_rendition_uses_the_published_vocabulary()
    {
        using JsonDocument json = Serialize(
            new AlbumSummaryResponse(
                AssetId,
                "Alps",
                null,
                new AssetRenditionResponse(
                    "thumb",
                    "/media/pipeline/source/recipe.webp",
                    512,
                    384,
                    "image/webp"),
                1,
                Instant,
                new ResourceVersion(1)));

        Assert.Equal(
            "thumb",
            json.RootElement.GetProperty("cover").GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("private", "Private")]
    [InlineData("tenant", "Tenant")]
    [InlineData("public", "Public")]
    public void Documented_visibility_tokens_translate_back_to_stored_names(
        string documentedToken,
        string storedValue)
    {
        Assert.True(
            AssetContractVocabulary.TryReadVisibility(
                documentedToken,
                out string translated));
        Assert.Equal(storedValue, translated);
    }

    [Theory]
    [InlineData("Private")]
    [InlineData("PRIVATE")]
    [InlineData("restricted")]
    [InlineData(null)]
    public void Undocumented_visibility_tokens_are_rejected(string? token)
    {
        Assert.False(
            AssetContractVocabulary.TryReadVisibility(token, out string translated));
        Assert.Equal(string.Empty, translated);
    }

    /// <summary>
    /// The same payload is asserted by
    /// <c>src/Vistara.Web/src/features/viewer/responsiveImage.test.ts</c> so the
    /// browser integration is exercised against the real server shape.
    /// </summary>
    private const string ExpectedAssetJson =
        """
        {"id":"01990a2a-bc00-7000-8000-0000000007a1","title":"Alpine lake","description":"A still lake below a snowy ridge","status":"ready","visibility":"private","revisionNumber":1,"contentType":"image/jpeg","format":"jpeg","width":4000,"height":3000,"sizeBytes":2000000,"capturedAt":"2026-08-28T09:00:00+00:00","importedAt":"2026-08-29T09:00:00+00:00","updatedAt":"2026-08-29T09:00:00+00:00","favorite":false,"tags":[],"renditions":[{"kind":"thumb","path":"/media/pipeline/source/thumb-512.webp","width":512,"height":384,"contentType":"image/webp"},{"kind":"grid","path":"/media/pipeline/source/grid-1024.webp","width":1024,"height":768,"contentType":"image/webp"},{"kind":"viewer","path":"/media/pipeline/source/viewer-2400.webp","width":2400,"height":1800,"contentType":"image/webp"}],"version":1}
        """;

    private static AssetSummaryResponse Summary(
        string status = "Ready",
        string visibility = "Private") =>
        new(
            AssetId,
            "Alpine lake",
            "A still lake below a snowy ridge",
            status,
            visibility,
            1,
            "image/jpeg",
            "jpeg",
            4000,
            3000,
            2_000_000,
            Instant.AddDays(-1),
            Instant,
            Instant,
            false,
            [],
            [
                new AssetRenditionResponse(
                    "thumb",
                    "/media/pipeline/source/thumb-512.webp",
                    512,
                    384,
                    "image/webp"),
                new AssetRenditionResponse(
                    "grid",
                    "/media/pipeline/source/grid-1024.webp",
                    1024,
                    768,
                    "image/webp"),
                new AssetRenditionResponse(
                    "viewer",
                    "/media/pipeline/source/viewer-2400.webp",
                    2400,
                    1800,
                    "image/webp"),
            ],
            new ResourceVersion(1));

    private static JsonDocument Serialize<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, ResponseJsonOptions));
}
