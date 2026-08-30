using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Sharing;
using Vistara.Persistence.Sharing;
using Xunit;

namespace Vistara.IntegrationTests.Shares;

public sealed class RelationalShareStoreTests
{
    [Fact]
    public async Task Shares_store_persists_hashes_snapshots_and_tenant_scope_without_plaintext_secrets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SharingDbContext> options =
            new DbContextOptionsBuilder<SharingDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var context = new SharingDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var store = new RelationalShareStore(context);
        DateTimeOffset now = new(2034, 5, 6, 7, 8, 9, TimeSpan.Zero);
        Guid tenantId = Guid.CreateVersion7(now);
        Guid otherTenantId = Guid.CreateVersion7(now.AddMilliseconds(1));
        Guid shareId = Guid.CreateVersion7(now.AddMilliseconds(2));
        Guid actorId = Guid.CreateVersion7(now.AddMilliseconds(3));
        Guid assetId = Guid.CreateVersion7(now.AddMilliseconds(4));
        Guid revisionId = Guid.CreateVersion7(now.AddMilliseconds(5));
        var share = new ShareRecord(
            shareId,
            tenantId,
            actorId,
            "Persisted share",
            ShareTargetType.Snapshot,
            null,
            [
                new ShareAssetSnapshot(
                    assetId,
                    revisionId,
                    7,
                    "Captured title",
                    null,
                    null,
                    640,
                    480,
                    [
                        new ShareRendition(
                            "thumbnail",
                            "/media/safe.webp",
                            320,
                            240,
                            "image/webp",
                            ShareAccess.View),
                    ]),
            ],
            ShareAccess.View,
            ShareMetadataExposure.None,
            "v1",
            new string('a', 64),
            "pbkdf2-sha512$v1$10000$salt$hash",
            now,
            null,
            null,
            null,
            1,
            new string('b', 64));

        ShareAddResult added = await store.AddAsync(
            share,
            new string('c', 64),
            share.RequestHash,
            CancellationToken.None);
        ShareRecord? sameTenant = await store.FindAsync(
            tenantId,
            shareId,
            CancellationToken.None);
        ShareRecord? concealed = await store.FindAsync(
            otherTenantId,
            shareId,
            CancellationToken.None);

        Assert.Equal(ShareAddStatus.Created, added.Status);
        Assert.NotNull(sameTenant);
        Assert.Null(concealed);
        Assert.Equal(7, Assert.Single(sameTenant.Assets).RevisionNumber);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT token_digest_hex, password_hash, assets_json FROM sharing_shares WHERE id = $id";
        command.Parameters.AddWithValue("$id", shareId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        string captured = string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetString(2));
        Assert.DoesNotContain("vsh_", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("storageKey", captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", captured, StringComparison.OrdinalIgnoreCase);
    }
}
