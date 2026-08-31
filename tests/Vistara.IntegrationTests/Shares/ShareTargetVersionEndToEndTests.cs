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
using Vistara.Api.Features.Media;
using Vistara.Api.Features.Shares;
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
        string[] paths = [.. publicAsset.GetProperty("renditions")
            .EnumerateArray()
            .Select(rendition => rendition.GetProperty("path").GetString()!)];

        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.StartsWith(
            $"/api/v1/public/shares/{token}/assets/{assetId:D}/renditions/",
            path,
            StringComparison.Ordinal));
        Assert.All(paths, path => Assert.DoesNotContain(
            "/media/",
            path,
            StringComparison.Ordinal));
        Assert.All(paths, path => Assert.DoesNotContain(
            "/delivery/",
            path,
            StringComparison.Ordinal));

        // A recipient fetches the advertised path with no credential at all.
        ApiResponse image = await scenario.SendAsync(HttpMethods.Get, paths[0]);

        Assert.Equal(HttpStatusCode.OK, image.Status);
        Assert.Equal("image/webp", image.ContentType);
        Assert.Equal(ShareVersionScenario.RenditionBytes, image.Bytes);
        Assert.Equal("private,no-store", image.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", image.Headers["X-Content-Type-Options"].ToString());

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

        // Revocation reaches the bytes on the very next request.
        ApiResponse revokedImage = await scenario.SendAsync(
            HttpMethods.Get,
            paths[0]);
        Assert.Equal(HttpStatusCode.Gone, revokedImage.Status);
        Assert.Equal("share_gone", ProblemCode(revokedImage));
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

    /// <summary>
    /// A share link is the only credential a recipient holds, so every other
    /// way of asking for the same bytes must be refused: an expired share, a
    /// password-protected share before its challenge, a share without download
    /// permission asking for the download rendition, a tampered identifier, and
    /// a second share's token.
    /// </summary>
    [Fact]
    public async Task Share_scoped_rendition_delivery_refuses_every_other_caller()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);
        SharePublication open = await scenario.PublishShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-delivery-open");
        string viewPath = open.PathForKind("viewer");
        string downloadPath = open.PathForIdentifier(
            await scenario.ReadRenditionIdentifierAsync(assetId, "download-web"));

        Assert.Equal(
            HttpStatusCode.OK,
            (await scenario.SendAsync(HttpMethods.Get, viewPath)).Status);

        // A view-only share never publishes or serves the download rendition.
        Assert.DoesNotContain(
            "download-web",
            open.RenditionKinds,
            StringComparer.Ordinal);
        ApiResponse withheld = await scenario.SendAsync(
            HttpMethods.Get,
            downloadPath);
        Assert.Equal(HttpStatusCode.NotFound, withheld.Status);
        Assert.Equal("share_rendition_not_found", ProblemCode(withheld));

        ApiResponse tamperedRendition = await scenario.SendAsync(
            HttpMethods.Get,
            open.PathForIdentifier(Guid.CreateVersion7().ToString("D")));
        Assert.Equal(HttpStatusCode.NotFound, tamperedRendition.Status);
        Assert.Equal("share_rendition_not_found", ProblemCode(tamperedRendition));

        ApiResponse tamperedToken = await scenario.SendAsync(
            HttpMethods.Get,
            viewPath.Replace(open.Token, $"{open.Token[..^2]}zz", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, tamperedToken.Status);
        Assert.Equal("share_rendition_not_found", ProblemCode(tamperedToken));

        // A second share of a different asset cannot lend its token to this one.
        Guid otherAssetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions other = await scenario.ReadAssetAsync(otherAssetId);
        SharePublication neighbourShare = await scenario.PublishShareAsync(
            otherAssetId,
            other.Version,
            idempotencyKey: "share-delivery-other");
        ApiResponse crossShare = await scenario.SendAsync(
            HttpMethods.Get,
            viewPath.Replace(open.Token, neighbourShare.Token, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, crossShare.Status);
        Assert.Equal("share_rendition_not_found", ProblemCode(crossShare));
    }

    [Fact]
    public async Task A_download_permitted_share_serves_its_download_rendition()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);
        SharePublication share = await scenario.PublishShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-delivery-download",
            downloadRenditions: true);

        Assert.Contains("download-web", share.RenditionKinds, StringComparer.Ordinal);
        ApiResponse served = await scenario.SendAsync(
            HttpMethods.Get,
            share.PathForKind("download-web"));

        Assert.Equal(HttpStatusCode.OK, served.Status);
        Assert.Equal(ShareVersionScenario.RenditionBytes, served.Bytes);
    }

    [Fact]
    public async Task An_expired_share_stops_serving_its_renditions()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);
        SharePublication share = await scenario.PublishShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-delivery-expiring",
            expiresAtUtc: ShareVersionScenario.ClockUtcNow.AddMinutes(30));
        string path = share.PathForKind("viewer");

        Assert.Equal(
            HttpStatusCode.OK,
            (await scenario.SendAsync(HttpMethods.Get, path)).Status);

        scenario.Advance(TimeSpan.FromHours(1));
        ApiResponse expired = await scenario.SendAsync(HttpMethods.Get, path);

        Assert.Equal(HttpStatusCode.Gone, expired.Status);
        Assert.Equal("share_gone", ProblemCode(expired));
    }

    [Fact]
    public async Task A_password_protected_share_serves_renditions_only_after_its_challenge()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);
        SharePublication share = await scenario.PublishShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-delivery-password",
            password: "correct horse battery staple");

        // The projection withholds every asset until the challenge succeeds.
        Assert.Empty(share.Assets);
        string path = ShareRenditionRoute.Build(
            share.Token,
            assetId,
            await scenario.ReadRenditionIdentifierAsync(assetId, "viewer"));
        ApiResponse locked = await scenario.SendAsync(HttpMethods.Get, path);

        Assert.Equal(HttpStatusCode.NotFound, locked.Status);
        Assert.Equal("share_rendition_not_found", ProblemCode(locked));

        string session = await scenario.ChallengeAsync(
            share.Token,
            "correct horse battery staple");
        ApiResponse unlocked = await scenario.SendAsync(
            HttpMethods.Get,
            path,
            headers: new Dictionary<string, string>
            {
                ["X-Vistara-Share-Session"] = session,
            });

        Assert.Equal(HttpStatusCode.OK, unlocked.Status);
        Assert.Equal(ShareVersionScenario.RenditionBytes, unlocked.Bytes);
    }

    /// <summary>
    /// A share of an asset whose derivatives have not landed would publish an
    /// empty gallery, so creation fails explicitly instead.
    /// </summary>
    [Fact]
    public async Task A_share_of_an_undeliverable_asset_fails_instead_of_succeeding_empty()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        await scenario.HideRenditionsAsync(assetId);
        AssetContractVersions asset = await scenario.ReadAssetRowAsync(assetId);

        ApiResponse rejected = await scenario.CreateSnapshotShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-delivery-empty");

        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            rejected.Status);
        Assert.Equal("share_target_not_deliverable", ProblemCode(rejected));
    }

    /// <summary>
    /// A share link is opened by whoever holds it, including someone already
    /// signed in to a different tenant in the same browser. That identity must
    /// neither break delivery nor learn anything a stranger would not.
    /// </summary>
    [Fact]
    public async Task A_signed_in_visitor_from_another_tenant_still_receives_shared_bytes()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid assetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions asset = await scenario.ReadAssetAsync(assetId);
        SharePublication share = await scenario.PublishShareAsync(
            assetId,
            asset.Version,
            idempotencyKey: "share-delivery-visitor");
        string path = share.PathForKind("viewer");

        ApiResponse visitor = await scenario.SendAsync(
            HttpMethods.Get,
            path,
            apiKey: scenario.NeighbourApiKey);

        Assert.Equal(HttpStatusCode.OK, visitor.Status);
        Assert.Equal(ShareVersionScenario.RenditionBytes, visitor.Bytes);

        // The same identity presenting a bad token learns exactly what an
        // anonymous caller would.
        ApiResponse probed = await scenario.SendAsync(
            HttpMethods.Get,
            path.Replace(share.Token, $"{share.Token[..^2]}zz", StringComparison.Ordinal),
            apiKey: scenario.NeighbourApiKey);

        Assert.Equal(HttpStatusCode.NotFound, probed.Status);
        Assert.Equal("share_rendition_not_found", ProblemCode(probed));
    }

    /// <summary>
    /// Album membership is not chosen asset by asset, so a member still waiting
    /// for its derivatives is dropped instead of failing the whole album.
    /// </summary>
    [Fact]
    public async Task An_album_share_drops_members_that_cannot_be_delivered_yet()
    {
        await using ShareVersionScenario scenario =
            await ShareVersionScenario.CreateAsync();
        Guid readyAssetId = await scenario.IngestReadyAssetAsync();
        Guid pendingAssetId = await scenario.IngestReadyAssetAsync();
        AssetContractVersions ready = await scenario.ReadAssetAsync(readyAssetId);
        AssetContractVersions pending = await scenario.ReadAssetAsync(pendingAssetId);
        Guid albumId = await scenario.CreateAlbumWithAssetsAsync(
            (readyAssetId, ready.Version),
            (pendingAssetId, pending.Version));
        await scenario.HideRenditionsAsync(pendingAssetId);

        ApiResponse created = await scenario.CreateAlbumShareAsync(
            albumId,
            "share-delivery-album-partial");

        Assert.Equal(HttpStatusCode.Created, created.Status);
        using JsonDocument body = JsonDocument.Parse(created.Body);
        JsonElement detail = body.RootElement.GetProperty("share");
        Assert.Equal(
            1,
            detail.GetProperty("share").GetProperty("target")
                .GetProperty("assetCount").GetInt32());
        Assert.Equal(
            readyAssetId,
            detail.GetProperty("snapshotAssets")[0].GetProperty("id").GetGuid());

        await scenario.HideRenditionsAsync(readyAssetId);
        ApiResponse empty = await scenario.CreateAlbumShareAsync(
            albumId,
            "share-delivery-album-empty");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.Status);
        Assert.Equal("share_target_not_deliverable", ProblemCode(empty));
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
    byte[] Bytes,
    string? ContentType,
    IHeaderDictionary Headers);

/// <summary>
/// A created share plus the public projection an anonymous recipient sees, so a
/// test can follow exactly the paths the share advertises.
/// </summary>
internal sealed record SharePublication(
    string Token,
    Guid ShareId,
    Guid AssetId,
    IReadOnlyList<PublishedRendition> Assets)
{
    internal IReadOnlyList<string> RenditionKinds =>
        [.. Assets.Select(rendition => rendition.Kind)];

    internal string PathForKind(string kind) =>
        Assets.Single(rendition => rendition.Kind == kind).Path;

    internal string PathForIdentifier(string deliveryIdentifier) =>
        ShareRenditionRoute.Build(Token, AssetId, deliveryIdentifier);
}

internal sealed record PublishedRendition(string Kind, string Path);

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
    private readonly ShareVersionClock _clock;
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
        ShareVersionClock clock,
        string ownerApiKey,
        string neighbourApiKey)
    {
        _clock = clock;
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

    internal static DateTimeOffset ClockUtcNow => Now;

    internal static byte[] RenditionBytes => ReadinessImageProcessor.OutputBytes;

    /// <summary>
    /// Moves every host's clock forward together so share expiry is observed by
    /// the projection, the delivery grant port, and the byte route alike.
    /// </summary>
    internal void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

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
        var clock = new ShareVersionClock(Now);
        workerServices.AddSingleton<IClock>(clock);
        workerServices.AddSingleton<IUuid7Generator>(new Uuid7Generator(clock));
        workerServices.AddVistaraWorkerPlatform(configuration);
        ServiceProvider worker = workerServices.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        ServiceCollection uploadServices = [];
        var ownerContext = new FixedPlatformTenantContext(owner.TenantId);
        uploadServices.AddScoped<ITenantScope>(_ => ownerContext);
        uploadServices.AddScoped<IPlatformTenantContext>(_ => ownerContext);
        uploadServices.AddSingleton<IBlobStore>(store);
        uploadServices.AddSingleton<IClock>(clock);
        uploadServices.AddSingleton<IUuid7Generator>(new Uuid7Generator(clock));
        uploadServices.AddVistaraApiPlatform(configuration);
        uploadServices.AddVistaraApiPersistence(configuration);
        ServiceProvider ownerUploads = uploadServices.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        WebApplicationBuilder apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Configuration.AddInMemoryCollection(settings);
        apiBuilder.Services.AddSingleton<IBlobStore>(store);
        apiBuilder.Services.AddSingleton<IClock>(clock);
        apiBuilder.Services.AddSingleton<IUuid7Generator>(new Uuid7Generator(clock));
        apiBuilder.Services.AddVistaraApiRuntime(apiBuilder.Configuration);
        apiBuilder.Services.AddVistaraApiPlatform(apiBuilder.Configuration);
        apiBuilder.Services.AddVistaraApiPersistence(apiBuilder.Configuration);
        WebApplication api = apiBuilder.Build();
        api.UseVistaraPlatform();
        api.MapVistaraGalleryFeatures();
        api.MapVistaraMedia();

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
            clock,
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
    /// Creates a share and reads back the projection an anonymous recipient
    /// receives, which is the only source of the rendition paths a test may
    /// follow.
    /// </summary>
    internal async ValueTask<SharePublication> PublishShareAsync(
        Guid assetId,
        long version,
        string idempotencyKey,
        bool downloadRenditions = false,
        string? password = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        string expiry = expiresAtUtc is { } expires
            ? $",\n              \"expiresAt\": \"{expires.UtcDateTime:O}\""
            : string.Empty;
        string secret = password is null
            ? string.Empty
            : $",\n              \"password\": \"{password}\"";
        ApiResponse created = await SendAsync(
            HttpMethods.Post,
            "/api/v1/shares",
            apiKey: OwnerApiKey,
            body: $$"""
            {
              "name": "Published share",
              "targetKind": "snapshot",
              "snapshotAssets": [{ "id": "{{assetId:D}}", "version": {{version}} }],
              "permissions": {
                "view": true,
                "downloadRenditions": {{(downloadRenditions ? "true" : "false")}},
                "downloadOriginal": false
              },
              "metadataExposure": "basic"{{expiry}}{{secret}}
            }
            """,
            headers: new Dictionary<string, string>
            {
                ["Idempotency-Key"] = idempotencyKey,
            });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        using JsonDocument body = JsonDocument.Parse(created.Body);
        string token = body.RootElement.GetProperty("publicToken").GetString()!;
        Guid shareId = body.RootElement
            .GetProperty("share").GetProperty("share").GetProperty("id").GetGuid();

        ApiResponse published = await SendAsync(
            HttpMethods.Get,
            $"/api/v1/public/shares/{token}");
        Assert.Equal(HttpStatusCode.OK, published.Status);
        using JsonDocument projection = JsonDocument.Parse(published.Body);
        PublishedRendition[] renditions = projection.RootElement
            .TryGetProperty("assets", out JsonElement assets)
            ? [.. assets.GetProperty("items")
                .EnumerateArray()
                .Where(asset => asset.GetProperty("id").GetGuid() == assetId)
                .SelectMany(asset => asset.GetProperty("renditions").EnumerateArray())
                .Select(rendition => new PublishedRendition(
                    rendition.GetProperty("kind").GetString()!,
                    rendition.GetProperty("path").GetString()!))]
            : [];
        return new SharePublication(token, shareId, assetId, renditions);
    }

    /// <summary>
    /// Answers a share password challenge and returns the session token the
    /// production endpoint issues in its scoped cookie.
    /// </summary>
    internal async ValueTask<string> ChallengeAsync(string token, string password)
    {
        ApiResponse challenged = await SendAsync(
            HttpMethods.Post,
            $"/api/v1/public/shares/{token}/challenge",
            body: $$"""{ "password": "{{password}}" }""");
        Assert.Equal(HttpStatusCode.OK, challenged.Status);
        string cookie = Assert.Single(
            challenged.Headers.SetCookie.ToArray(),
            value => value!.StartsWith("Vistara.ShareSession=", StringComparison.Ordinal))!;
        return cookie["Vistara.ShareSession=".Length..].Split(';')[0];
    }

    internal async ValueTask<string> ReadRenditionIdentifierAsync(
        Guid assetId,
        string preset)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            "SELECT id FROM derivative_requests " +
            "WHERE asset_id = $asset AND preset_name = $preset;";
        command.Parameters.AddWithValue("$asset", assetId);
        command.Parameters.AddWithValue("$preset", preset);
        return Guid.Parse((string)(await command.ExecuteScalarAsync())!)
            .ToString("D");
    }

    /// <summary>
    /// Returns every derivative to a pre-Ready state, which is what a share
    /// created before processing finishes would observe.
    /// </summary>
    internal async ValueTask HideRenditionsAsync(Guid assetId)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            "UPDATE derivative_requests SET state = 'Processing' " +
            "WHERE asset_id = $asset;";
        command.Parameters.AddWithValue("$asset", assetId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Creates an album through the production curation endpoints and adds the
    /// asset with the very same versioned reference contract share creation
    /// uses, which is what makes the two surfaces comparable.
    /// </summary>
    internal ValueTask<Guid> CreateAlbumWithAssetAsync(
        Guid assetId,
        long assetVersion) =>
        CreateAlbumWithAssetsAsync((assetId, assetVersion));

    internal async ValueTask<Guid> CreateAlbumWithAssetsAsync(
        params (Guid AssetId, long Version)[] items)
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
        string references = string.Join(
            ", ",
            items.Select(item =>
                $$"""{ "id": "{{item.AssetId:D}}", "version": {{item.Version}} }"""));

        ApiResponse added = await SendAsync(
            HttpMethods.Post,
            $"/api/v1/albums/{albumId:D}/items",
            apiKey: OwnerApiKey,
            body: $$"""
            { "items": [{{references}}] }
            """,
            headers: new Dictionary<string, string>
            {
                ["If-Match"] = $"\"v{albumVersion}\"",
                ["Idempotency-Key"] = "share-version-album-items",
            });
        Assert.Equal(HttpStatusCode.OK, added.Status);
        return albumId;
    }

    internal Task<ApiResponse> CreateAlbumShareAsync(
        Guid albumId,
        string idempotencyKey) =>
        SendAsync(
            HttpMethods.Post,
            "/api/v1/shares",
            apiKey: OwnerApiKey,
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
                ["Idempotency-Key"] = idempotencyKey,
            });

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
        byte[] bytes = ((MemoryStream)context.Response.Body).ToArray();
        return new ApiResponse(
            (HttpStatusCode)context.Response.StatusCode,
            Encoding.UTF8.GetString(bytes),
            bytes,
            context.Response.ContentType,
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

    private sealed class ShareVersionClock(DateTimeOffset utcNow) : IClock
    {
        private DateTimeOffset _utcNow = utcNow;

        public DateTimeOffset UtcNow => _utcNow;

        internal void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
    }

    private sealed class FixedPlatformTenantContext(Guid tenantId) :
        ITenantScope,
        IPlatformTenantContext
    {
        public Guid TenantId { get; } = tenantId;

        Guid? IPlatformTenantContext.TenantId => TenantId;
    }
}
