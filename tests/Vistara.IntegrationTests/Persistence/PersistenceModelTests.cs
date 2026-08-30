using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Assets;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Application.Uploads;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Persistence;

public sealed class PersistenceModelTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Model_creates_the_core_schema_on_sqlite()
    {
        await using PersistenceDatabase database = await PersistenceDatabase.CreateAsync();

        string[] tableNames = database.Context.Model
            .GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(name => name is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "album_items",
                "albums",
                "api_keys",
                "asset_favorites",
                "asset_lifecycles",
                "asset_metadata_history",
                "asset_revisions",
                "asset_tags",
                "assets",
                "audit_events",
                "auth_sessions",
                "authentication_routes",
                "blobs",
                "cookie_sessions",
                "deletion_tombstones",
                "delivery_grants",
                "derivative_requests",
                "event_log",
                "external_identities",
                "idempotency_requests",
                "ingest_operations",
                "jobs",
                "local_credentials",
                "local_identities",
                "outbox_messages",
                "outbox_sequences",
                "public_derivative_routes",
                "purge_batch_items",
                "purge_batches",
                "quota_reservations",
                "quota_usage",
                "rate_limit_windows",
                "relationship_snapshots",
                "resource_grants",
                "retention_holds",
                "revoked_tokens",
                "share_assets",
                "share_sessions",
                "shares",
                "sharing_idempotency",
                "sharing_rate_limits",
                "sharing_sessions",
                "sharing_shares",
                "tags",
                "tenant_memberships",
                "tenants",
                "trash_entries",
                "upload_parts",
                "upload_reconciliation_checkpoints",
                "upload_sessions",
                "users",
                "worker_tenant_catalog",
            ],
            tableNames);
    }

    [Fact]
    public void Model_builds_for_postgresql()
    {
        Guid tenantId = Guid.CreateVersion7();
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseNpgsql("Host=localhost;Database=vistara_model;Username=unused;Password=unused")
            .Options;

        using var context = new VistaraDbContext(options, new FixedTenantScope(tenantId));

        Assert.NotEmpty(context.Model.GetEntityTypes());
        Assert.All(
            context.Model.GetEntityTypes().Where(IsTenantOwned),
            entity => Assert.NotEmpty(entity.GetDeclaredQueryFilters()));
    }

    [Fact]
    public void Registration_configures_provider_context_and_focused_repositories()
    {
        Guid tenantId = Guid.CreateVersion7();
        var services = new ServiceCollection();
        services.AddSingleton<ITenantScope>(new FixedTenantScope(tenantId));
        services.AddVistaraPersistence(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = "Data Source=:memory:";
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        VistaraDbContext context =
            scope.ServiceProvider.GetRequiredService<VistaraDbContext>();

        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", context.Database.ProviderName);
        Assert.IsAssignableFrom<ITenantRepository>(
            scope.ServiceProvider.GetRequiredService<ITenantRepository>());
        Assert.IsAssignableFrom<IUserRepository>(
            scope.ServiceProvider.GetRequiredService<IUserRepository>());
        Assert.IsAssignableFrom<IAssetRepository>(
            scope.ServiceProvider.GetRequiredService<IAssetRepository>());
        Assert.IsAssignableFrom<IUploadSessionRepository>(
            scope.ServiceProvider.GetRequiredService<IUploadSessionRepository>());
    }

    [Fact]
    public async Task Tenant_filter_conceals_cross_tenant_rows_before_lookup()
    {
        Guid tenantOne = Guid.CreateVersion7();
        Guid tenantTwo = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetOne = Guid.CreateVersion7();
        Guid assetTwo = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantOne);

        database.Context.Users.Add(User(ownerId));
        database.Context.Tenants.Add(Tenant(tenantOne, "tenant-one"));
        database.Context.Assets.Add(Asset(tenantOne, assetOne, ownerId, "one"));
        await database.Context.SaveChangesAsync();

        await using (VistaraDbContext tenantTwoContext = database.CreateContext(tenantTwo))
        {
            tenantTwoContext.Tenants.Add(Tenant(tenantTwo, "tenant-two"));
            tenantTwoContext.Assets.Add(Asset(tenantTwo, assetTwo, ownerId, "two"));
            await tenantTwoContext.SaveChangesAsync();
        }

        database.Context.ChangeTracker.Clear();

        Assert.NotNull(await database.Context.Assets.SingleOrDefaultAsync(row => row.Id == assetOne));
        Assert.Null(await database.Context.Assets.SingleOrDefaultAsync(row => row.Id == assetTwo));
        Assert.Single(await database.Context.Assets.ToListAsync());
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_cross_tenant_relationships()
    {
        Guid tenantOne = Guid.CreateVersion7();
        Guid tenantTwo = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantOne);

        database.Context.Users.Add(User(ownerId));
        database.Context.Tenants.Add(Tenant(tenantOne, "tenant-one"));
        database.Context.Albums.Add(Album(tenantOne, albumId, ownerId));
        await database.Context.SaveChangesAsync();

        await using (VistaraDbContext tenantTwoContext = database.CreateContext(tenantTwo))
        {
            tenantTwoContext.Tenants.Add(Tenant(tenantTwo, "tenant-two"));
            tenantTwoContext.Assets.Add(Asset(tenantTwo, assetId, ownerId, "other"));
            await tenantTwoContext.SaveChangesAsync();
        }

        database.Context.AlbumItems.Add(new AlbumItemRow
        {
            TenantId = tenantOne,
            AlbumId = albumId,
            AssetId = assetId,
            Position = 0,
            AddedByUserId = ownerId,
            AddedAtUtc = UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_scoped_uniqueness_allows_same_tag_in_other_tenant()
    {
        Guid tenantOne = Guid.CreateVersion7();
        Guid tenantTwo = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantOne);

        database.Context.Tenants.Add(Tenant(tenantOne, "tenant-one"));
        database.Context.Tags.Add(Tag(tenantOne, "travel"));
        await database.Context.SaveChangesAsync();

        database.Context.Tags.Add(Tag(tenantOne, "travel"));
        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.Context.SaveChangesAsync());

        database.Context.ChangeTracker.Clear();
        await using VistaraDbContext tenantTwoContext = database.CreateContext(tenantTwo);
        tenantTwoContext.Tenants.Add(Tenant(tenantTwo, "tenant-two"));
        tenantTwoContext.Tags.Add(Tag(tenantTwo, "travel"));
        await tenantTwoContext.SaveChangesAsync();
    }

    [Fact]
    public async Task Application_version_is_an_optimistic_concurrency_token()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);

        database.Context.Users.Add(User(ownerId));
        database.Context.Tenants.Add(Tenant(tenantId, "tenant"));
        database.Context.Assets.Add(Asset(tenantId, assetId, ownerId, "original"));
        await database.Context.SaveChangesAsync();

        await using VistaraDbContext first = database.CreateContext(tenantId);
        await using VistaraDbContext second = database.CreateContext(tenantId);
        AssetRow firstRow = await first.Assets.SingleAsync(row => row.Id == assetId);
        AssetRow secondRow = await second.Assets.SingleAsync(row => row.Id == assetId);

        firstRow.Title = "first";
        firstRow.Version++;
        await first.SaveChangesAsync();

        secondRow.Title = "second";
        secondRow.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Album_members_restrict_asset_deletion_and_cascade_with_album()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);

        database.Context.Users.Add(User(ownerId));
        database.Context.Tenants.Add(Tenant(tenantId, "tenant"));
        database.Context.Assets.Add(Asset(tenantId, assetId, ownerId, "asset"));
        database.Context.Albums.Add(Album(tenantId, albumId, ownerId));
        database.Context.AlbumItems.Add(new AlbumItemRow
        {
            TenantId = tenantId,
            AlbumId = albumId,
            AssetId = assetId,
            Position = 0,
            AddedByUserId = ownerId,
            AddedAtUtc = UtcNow,
        });
        await database.Context.SaveChangesAsync();

        database.Context.ChangeTracker.Clear();
        database.Context.Assets.Remove(await database.Context.Assets.SingleAsync());
        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.Context.SaveChangesAsync());

        database.Context.ChangeTracker.Clear();
        database.Context.Albums.Remove(await database.Context.Albums.SingleAsync());
        await database.Context.SaveChangesAsync();

        Assert.NotNull(await database.Context.Assets.SingleOrDefaultAsync());
        Assert.Empty(await database.Context.AlbumItems.ToListAsync());
    }

    [Fact]
    public async Task Date_time_offsets_are_normalized_to_utc_on_round_trip()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        DateTimeOffset nonUtc =
            new(2026, 8, 28, 8, 30, 0, TimeSpan.FromHours(-4));

        TenantRow tenant = Tenant(tenantId, "tenant");
        tenant.CreatedAtUtc = nonUtc;
        tenant.UpdatedAtUtc = nonUtc;
        database.Context.Tenants.Add(tenant);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        TenantRow reloaded = await database.Context.Tenants.SingleAsync();

        Assert.Equal(TimeSpan.Zero, reloaded.CreatedAtUtc.Offset);
        Assert.Equal(nonUtc.ToUniversalTime(), reloaded.CreatedAtUtc);
    }

    [Fact]
    public async Task Gallery_sharing_and_lifecycle_state_round_trips()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid userId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid albumId = Guid.CreateVersion7();
        Guid tagId = Guid.CreateVersion7();
        Guid shareId = Guid.CreateVersion7();
        Guid purgeBatchId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);

        database.Context.AddRange(
            User(userId),
            Tenant(tenantId, "tenant"),
            Asset(tenantId, assetId, userId, "asset"),
            Album(tenantId, albumId, userId),
            new AlbumItemRow
            {
                TenantId = tenantId,
                AlbumId = albumId,
                AssetId = assetId,
                Position = 0,
                AddedByUserId = userId,
                AddedAtUtc = UtcNow,
            },
            new TagRow
            {
                Id = tagId,
                TenantId = tenantId,
                DisplayName = "Travel",
                NormalizedName = "travel",
                Color = "#123456",
                Version = 2,
            },
            new AssetTagRow
            {
                TenantId = tenantId,
                AssetId = assetId,
                TagId = tagId,
                Source = "user",
            },
            new AssetFavoriteRow
            {
                TenantId = tenantId,
                UserId = userId,
                AssetId = assetId,
                AddedAtUtc = UtcNow,
            },
            new ResourceGrantRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ResourceKind = "Asset",
                ResourceId = assetId,
                GranteeKind = "User",
                GranteeId = userId,
                Role = "Viewer",
                CreatedByUserId = userId,
                CreatedAtUtc = UtcNow,
                Version = 1,
            },
            new ShareRow
            {
                Id = shareId,
                TenantId = tenantId,
                CreatedByUserId = userId,
                TokenHash = new string('f', 64),
                TargetKind = "Album",
                AlbumId = albumId,
                Permissions = 1,
                CreatedAtUtc = UtcNow,
                Version = 1,
            },
            new AssetLifecycleRow
            {
                TenantId = tenantId,
                AssetId = assetId,
                CurrentRevision = 1,
                State = "Trashed",
                HasBeenTrashed = true,
                Version = 2,
            },
            new TrashEntryRow
            {
                TenantId = tenantId,
                AssetId = assetId,
                DeletedByUserId = userId,
                DeletedAtUtc = UtcNow,
                PurgeAtUtc = UtcNow.AddDays(30),
                Reason = "cleanup",
            },
            new RetentionHoldRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AssetId = assetId,
                Reason = "legal",
                CreatedByUserId = userId,
                CreatedAtUtc = UtcNow,
                Version = 1,
            },
            new RelationshipSnapshotRow
            {
                TenantId = tenantId,
                AssetId = assetId,
                Kind = "Album",
                ResourceId = albumId,
            },
            new PurgeBatchRow
            {
                Id = purgeBatchId,
                TenantId = tenantId,
                RequestedByUserId = userId,
                RequestedAtUtc = UtcNow,
                CandidateCount = 1,
                EligibleCount = 0,
                State = "DryRunCompleted",
                DryRunHash = new string('a', 64),
                DryRunCompletedAtUtc = UtcNow.AddMinutes(1),
                Version = 2,
            },
            new PurgeBatchItemRow
            {
                TenantId = tenantId,
                PurgeBatchId = purgeBatchId,
                AssetId = assetId,
                Revision = 1,
                Result = "Blocked",
                ReclaimedBytes = 0,
            },
            new DeletionTombstoneRow
            {
                TenantId = tenantId,
                FormerAssetId = Guid.CreateVersion7(),
                PurgedAtUtc = UtcNow,
                BackupExpiresAtUtc = UtcNow.AddDays(30),
                RelationshipCount = 1,
                RelationshipDigest = new string('b', 64),
            });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        Assert.Equal(2, (await database.Context.Tags.SingleAsync()).Version);
        Assert.Equal("Album", (await database.Context.RelationshipSnapshots.SingleAsync()).Kind);
        Assert.Equal("cleanup", (await database.Context.TrashEntries.SingleAsync()).Reason);
        Assert.Equal("DryRunCompleted", (await database.Context.PurgeBatches.SingleAsync()).State);
        Assert.Equal("Blocked", (await database.Context.PurgeBatchItems.SingleAsync()).Result);
        Assert.Single(await database.Context.Shares.ToListAsync());
        Assert.Single(await database.Context.ResourceGrants.ToListAsync());
    }

    [Fact]
    public async Task Invalid_enum_storage_is_rejected_by_check_constraints()
    {
        Guid tenantId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);

        await database.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO tenants (id, tenant_id, slug, name, status, settings_json, quotas_json, created_at_utc, updated_at_utc, version) VALUES ({tenantId}, {tenantId}, {"tenant"}, {"Tenant"}, {"Active"}, {"{}"}, {"{}"}, {UtcNow.UtcDateTime}, {UtcNow.UtcDateTime}, {1L})");

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await database.Context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tenants SET status = {"NotAStatus"} WHERE id = {tenantId}"));
    }

    private static bool IsTenantOwned(IReadOnlyEntityType entity) =>
        entity.FindProperty(nameof(ITenantOwnedRow.TenantId)) is not null;

    private static TenantRow Tenant(Guid id, string slug) => new()
    {
        Id = id,
        TenantId = id,
        Slug = slug,
        Name = slug,
        Status = "Active",
        SettingsJson = "{}",
        QuotasJson = "{}",
        CreatedAtUtc = UtcNow,
        UpdatedAtUtc = UtcNow,
        Version = 1,
    };

    private static UserRow User(Guid id) => new()
    {
        Id = id,
        NormalizedEmail = $"{id:N}@example.test",
        DisplayName = "Owner",
        Status = "Active",
        CreatedAtUtc = UtcNow,
        UpdatedAtUtc = UtcNow,
        Version = 1,
    };

    private static AssetRow Asset(Guid tenantId, Guid id, Guid ownerId, string title) => new()
    {
        Id = id,
        TenantId = tenantId,
        OwnerId = ownerId,
        Title = title,
        Status = "Processing",
        Visibility = "Private",
        CreatedAtUtc = UtcNow,
        UpdatedAtUtc = UtcNow,
        Version = 1,
    };

    private static AlbumRow Album(Guid tenantId, Guid id, Guid ownerId) => new()
    {
        Id = id,
        TenantId = tenantId,
        OwnerId = ownerId,
        Name = "Album",
        SortMode = "Manual",
        Version = 1,
    };

    private static TagRow Tag(Guid tenantId, string normalizedName) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DisplayName = normalizedName,
        NormalizedName = normalizedName,
        Version = 1,
    };
}

internal sealed class PersistenceDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private PersistenceDatabase(
        SqliteConnection connection,
        VistaraDbContext context)
    {
        _connection = connection;
        Context = context;
    }

    internal VistaraDbContext Context { get; }

    internal static async ValueTask<PersistenceDatabase> CreateAsync(Guid? tenantId = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;
        var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId ?? Guid.CreateVersion7()));
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new PersistenceDatabase(connection, context);
    }

    internal VistaraDbContext CreateContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;
        return new VistaraDbContext(options, new FixedTenantScope(tenantId));
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
