using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Gallery.Queries;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Queries;
using Vistara.Persistence.Model;

namespace Vistara.PerformanceTests;

internal sealed class ManagementReadFixture : IAsyncDisposable
{
    private static readonly DateTimeOffset Snapshot =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _anchor;
    private readonly DbContextOptions<VistaraDbContext> _options;

    private ManagementReadFixture(
        SqliteConnection anchor,
        DbContextOptions<VistaraDbContext> options)
    {
        _anchor = anchor;
        _options = options;
    }

    internal static async Task<ManagementReadFixture> CreateAsync(int assetCount)
    {
        string databaseName = $"VistaraManagement{Guid.NewGuid():N}";
        string connectionString =
            $"Data Source={databaseName};Mode=Memory;Cache=Shared;Default Timeout=5";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var fixture = new ManagementReadFixture(anchor, options);
        await fixture.SeedAsync(assetCount);
        return fixture;
    }

    internal async Task<(double Milliseconds, double MetadataBrotliKiB)> ReadAsync()
    {
        await using VistaraDbContext context = CreateContext();
        var store = new RelationalAssetQueryStore(context);
        var stopwatch = Stopwatch.StartNew();
        AssetQuerySlice slice = await store.QueryAsync(
            new AssetQueryScope(TestIds.Tenant, TestIds.Actor),
            AssetQueryCriteria.Create(limit: 60, sort: "importedAt", direction: "desc"),
            new AssetQueryWindow(Snapshot, null),
            CancellationToken.None);
        stopwatch.Stop();
        if (slice.Items.Count != 60)
        {
            throw new InvalidOperationException(
                $"Management read returned {slice.Items.Count} items instead of 60.");
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(slice.Items);
        await using var compressed = new MemoryStream();
        await using (var brotli = new BrotliStream(
                         compressed,
                         CompressionLevel.SmallestSize,
                         leaveOpen: true))
        {
            await brotli.WriteAsync(json);
        }

        return (stopwatch.Elapsed.TotalMilliseconds, compressed.Length / 1024d);
    }

    public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();

    private VistaraDbContext CreateContext() =>
        new(_options, new FixedTenantScope(TestIds.Tenant));

    private async Task SeedAsync(int assetCount)
    {
        await using VistaraDbContext context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Tenants.Add(new TenantRow
        {
            Id = TestIds.Tenant,
            TenantId = TestIds.Tenant,
            Slug = "performance",
            Name = "Performance",
            Status = "Active",
            CreatedAtUtc = Snapshot.AddDays(-10),
            UpdatedAtUtc = Snapshot.AddDays(-10),
            Version = 1,
        });
        context.Users.Add(new UserRow
        {
            Id = TestIds.Actor,
            NormalizedEmail = "performance@example.test",
            DisplayName = "Performance",
            Status = "Active",
            CreatedAtUtc = Snapshot.AddDays(-10),
            UpdatedAtUtc = Snapshot.AddDays(-10),
            Version = 1,
        });
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = TestIds.Tenant,
            UserId = TestIds.Actor,
            Role = "Member",
            Status = "Active",
            InvitedAtUtc = Snapshot.AddDays(-10),
            JoinedAtUtc = Snapshot.AddDays(-10),
            UpdatedAtUtc = Snapshot.AddDays(-10),
            Version = 1,
        });
        await context.SaveChangesAsync();

        for (int index = 0; index < assetCount; index++)
        {
            Guid assetId = TestIds.Create(100_000 + index * 3L);
            Guid revisionId = TestIds.Create(100_001 + index * 3L);
            Guid blobId = TestIds.Create(100_002 + index * 3L);
            DateTimeOffset created = Snapshot.AddMinutes(-index - 1);
            var asset = new AssetRow
            {
                Id = assetId,
                TenantId = TestIds.Tenant,
                OwnerId = TestIds.Actor,
                Title = $"Asset {index:D6}",
                Description = index % 5 == 0 ? "Reference gallery asset" : null,
                Status = "Ready",
                Visibility = "Private",
                CapturedAtUtc = created.AddHours(-1),
                CreatedAtUtc = created,
                UpdatedAtUtc = created,
                Version = 1,
            };
            context.Blobs.Add(new BlobRow
            {
                Id = blobId,
                TenantId = TestIds.Tenant,
                Provider = "local",
                Container = "assets",
                ObjectKey = $"tenant/{TestIds.Tenant:D}/{assetId:D}",
                Sha256 = new string('a', 64),
                SizeBytes = 1_500_000 + index,
                ContentType = "image/jpeg",
                State = "Active",
                CreatedAtUtc = created,
            });
            context.Assets.Add(asset);
            if ((index + 1) % 200 == 0)
            {
                await context.SaveChangesAsync();
            }
        }

        await context.SaveChangesAsync();
        for (int index = 0; index < assetCount; index++)
        {
            Guid assetId = TestIds.Create(100_000 + index * 3L);
            Guid revisionId = TestIds.Create(100_001 + index * 3L);
            Guid blobId = TestIds.Create(100_002 + index * 3L);
            DateTimeOffset created = Snapshot.AddMinutes(-index - 1);
            context.AssetRevisions.Add(new AssetRevisionRow
            {
                Id = revisionId,
                TenantId = TestIds.Tenant,
                AssetId = assetId,
                RevisionNumber = 1,
                BlobId = blobId,
                DetectedFormat = "jpeg",
                DetectedContentType = "image/jpeg",
                Width = 2000,
                Height = 1000,
                FrameCount = 1,
                SafeMetadataJson = """{"cameraMake":"Vistara Reference"}""",
                PrivateMetadataJson = "{}",
                CreatedAtUtc = created,
            });
            if ((index + 1) % 200 == 0)
            {
                await context.SaveChangesAsync();
            }
        }

        await context.SaveChangesAsync();
        AssetRow[] assets = await context.Assets.OrderBy(asset => asset.Id).ToArrayAsync();
        var assetsById = assets.ToDictionary(asset => asset.Id);
        for (int index = 0; index < assetCount; index++)
        {
            Guid assetId = TestIds.Create(100_000 + index * 3L);
            AssetRow asset = assetsById[assetId];
            asset.CurrentRevisionId = TestIds.Create(100_001 + index * 3L);
            if ((index + 1) % 200 == 0)
            {
                await context.SaveChangesAsync();
            }
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}
