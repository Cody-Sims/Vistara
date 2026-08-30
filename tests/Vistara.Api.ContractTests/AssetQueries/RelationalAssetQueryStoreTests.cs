using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vistara.Application.Gallery.Queries;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Queries;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.Api.ContractTests.AssetQueries;

public sealed class RelationalAssetQueryStoreTests : IAsyncLifetime, IDisposable
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000721");
    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000722");
    private static readonly Guid ActorId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000723");
    private static readonly DateTimeOffset Snapshot =
        new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CommandRecorder _commands = new();

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using VistaraDbContext schema = CreateContext(TenantId);
        await schema.Database.EnsureCreatedAsync();
        await SeedTenantAsync(TenantId, "tenant");
        await SeedTenantAsync(OtherTenantId, "other");
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Query_is_tenant_scoped_filtered_sorted_and_uses_stable_null_ordering()
    {
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000731",
            "Captured lake",
            "image/jpeg",
            Snapshot.AddDays(-2),
            Snapshot.AddDays(-4),
            sizeBytes: 200);
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000732",
            "Imported lake",
            "image/jpeg",
            capturedAt: null,
            Snapshot.AddDays(-1),
            sizeBytes: 100);
        await SeedAssetAsync(
            OtherTenantId,
            "01990a2a-bc00-7000-8000-000000000733",
            "Other lake",
            "image/jpeg",
            Snapshot,
            Snapshot,
            sizeBytes: 300);

        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        AssetQueryCriteria criteria = AssetQueryCriteria.Create(
            limit: 10,
            search: "lake",
            statuses: ["Ready"],
            contentTypes: ["image/jpeg"],
            sort: "capturedAt",
            direction: "desc");

        AssetQuerySlice result = await store.QueryAsync(
            new AssetQueryScope(TenantId, ActorId),
            criteria,
            new AssetQueryWindow(Snapshot, Continuation: null),
            CancellationToken.None);

        Assert.Equal(["Captured lake", "Imported lake"], result.Items.Select(item => item.Title));
        Assert.DoesNotContain(result.Items, item => item.Title == "Other lake");
    }

    [Fact]
    public async Task Keyset_pages_are_consistent_when_newer_rows_arrive_after_snapshot()
    {
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000741",
            "First",
            "image/jpeg",
            Snapshot.AddDays(-1),
            Snapshot.AddDays(-1),
            sizeBytes: 100);
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000742",
            "Second",
            "image/jpeg",
            Snapshot.AddDays(-2),
            Snapshot.AddDays(-2),
            sizeBytes: 100);
        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        AssetQueryCriteria criteria = AssetQueryCriteria.Create(
            limit: 1,
            sort: "importedAt",
            direction: "desc");

        AssetQuerySlice first = await store.QueryAsync(
            new AssetQueryScope(TenantId, ActorId),
            criteria,
            new AssetQueryWindow(Snapshot, Continuation: null),
            CancellationToken.None);
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000743",
            "Late insert",
            "image/jpeg",
            Snapshot.AddMinutes(1),
            Snapshot.AddMinutes(1),
            sizeBytes: 100);
        AssetQuerySlice second = await store.QueryAsync(
            new AssetQueryScope(TenantId, ActorId),
            criteria,
            new AssetQueryWindow(Snapshot, first.NextKey),
            CancellationToken.None);

        Assert.Equal("First", Assert.Single(first.Items).Title);
        Assert.Equal("Second", Assert.Single(second.Items).Title);
    }

    [Fact]
    public async Task Snapshot_excludes_rows_modified_after_pagination_started()
    {
        Guid assetId = await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000744",
            "Changed later",
            "image/jpeg",
            Snapshot.AddDays(-2),
            Snapshot.AddDays(-2),
            sizeBytes: 100);
        await using (VistaraDbContext update = CreateContext(TenantId))
        {
            AssetRow asset = await update.Assets.SingleAsync(row => row.Id == assetId);
            asset.UpdatedAtUtc = Snapshot.AddMinutes(1);
            asset.Version++;
            await update.SaveChangesAsync();
        }

        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        AssetQuerySlice result = await store.QueryAsync(
            new AssetQueryScope(TenantId, ActorId),
            AssetQueryCriteria.Create(
                limit: 20,
                sort: "updatedAt",
                direction: "desc"),
            new AssetQueryWindow(Snapshot, Continuation: null),
            CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Title_sort_uses_a_stable_id_tiebreaker_across_keyset_pages()
    {
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000745",
            "Alpha",
            "image/jpeg",
            Snapshot.AddDays(-1),
            Snapshot.AddDays(-1),
            sizeBytes: 100);
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000746",
            "Bravo",
            "image/jpeg",
            Snapshot.AddDays(-1),
            Snapshot.AddDays(-1),
            sizeBytes: 100);
        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        AssetQueryCriteria criteria = AssetQueryCriteria.Create(
            limit: 1,
            sort: "title",
            direction: "asc");

        AssetQuerySlice first = await store.QueryAsync(
            new AssetQueryScope(TenantId, ActorId),
            criteria,
            new AssetQueryWindow(Snapshot, Continuation: null),
            CancellationToken.None);
        AssetQuerySlice second = await store.QueryAsync(
            new AssetQueryScope(TenantId, ActorId),
            criteria,
            new AssetQueryWindow(Snapshot, first.NextKey),
            CancellationToken.None);

        Assert.Equal("Alpha", Assert.Single(first.Items).Title);
        Assert.Equal("Bravo", Assert.Single(second.Items).Title);
    }

    [Fact]
    public async Task Filters_use_relational_fields_and_batch_related_data_with_bounded_queries()
    {
        Guid assetId = await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000751",
            "Tagged",
            "image/webp",
            Snapshot.AddDays(-1),
            Snapshot.AddDays(-1),
            sizeBytes: 100);
        Guid tagId = Guid.Parse("01990a2a-bc00-7000-8000-000000000752");
        await using (VistaraDbContext seed = CreateContext(TenantId))
        {
            seed.Tags.Add(new TagRow
            {
                Id = tagId,
                TenantId = TenantId,
                NormalizedName = "nature",
                DisplayName = "Nature",
                Version = 1,
            });
            seed.AssetTags.Add(new AssetTagRow
            {
                TenantId = TenantId,
                AssetId = assetId,
                TagId = tagId,
                Source = "user",
            });
            seed.AssetFavorites.Add(new AssetFavoriteRow
            {
                TenantId = TenantId,
                AssetId = assetId,
                UserId = ActorId,
                AddedAtUtc = Snapshot,
            });
            await seed.SaveChangesAsync();
        }

        _commands.Reset();
        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        AssetQuerySlice result = await store.QueryAsync(
            new AssetQueryScope(TenantId, ActorId),
            AssetQueryCriteria.Create(
                limit: 20,
                tagIds: [tagId],
                favorite: true,
                sort: "sizeBytes",
                direction: "asc"),
            new AssetQueryWindow(Snapshot, Continuation: null),
            CancellationToken.None);

        AssetQueryItem item = Assert.Single(result.Items);
        Assert.True(item.Favorite);
        Assert.Equal("Nature", Assert.Single(item.Tags).Name);
        Assert.InRange(_commands.Count, 1, 4);
        Assert.DoesNotContain(
            _commands.Commands,
            command => command.Contains("safe_metadata_json", StringComparison.OrdinalIgnoreCase) &&
                command.Contains("WHERE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Facets_count_only_the_current_tenant_and_are_bounded()
    {
        await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000761",
            "One",
            "image/jpeg",
            Snapshot,
            Snapshot,
            sizeBytes: 100);
        await SeedAssetAsync(
            OtherTenantId,
            "01990a2a-bc00-7000-8000-000000000762",
            "Other",
            "image/png",
            Snapshot,
            Snapshot,
            sizeBytes: 100);
        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);

        IReadOnlyList<AssetFacetGroup> groups = await store.GetFacetsAsync(
            new AssetQueryScope(TenantId, ActorId),
            AssetQueryCriteria.Create(limit: 20),
            Snapshot,
            CancellationToken.None);

        AssetFacetGroup contentTypes = Assert.Single(
            groups,
            group => group.Name == "contentType");
        AssetFacetValue value = Assert.Single(contentTypes.Values);
        Assert.Equal("image/jpeg", value.Value);
        Assert.Equal(1, value.Count);
        Assert.All(groups, group => Assert.InRange(group.Values.Count, 0, 100));
    }

    [Fact]
    public async Task Safe_metadata_projection_excludes_private_and_sensitive_properties()
    {
        Guid assetId = await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000771",
            "Metadata",
            "image/jpeg",
            Snapshot,
            Snapshot,
            sizeBytes: 100,
            safeMetadata:
                """{"orientation":"Rotate90Clockwise","cameraMake":"Vistara","gpsLatitude":"1","serialNumber":"secret"}""",
            privateMetadata: """{"gps":"secret","owner":"private"}""");
        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);

        AssetMetadata? metadata = await store.GetMetadataAsync(
            new AssetQueryScope(TenantId, ActorId),
            assetId,
            CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal("Vistara", metadata.CameraMake);
        Assert.True(metadata.RestrictedMetadataAvailable);
        Assert.DoesNotContain(
            metadata.SafeProperties.Keys,
            key => key.Contains("gps", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("serial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Trashed_assets_are_concealed_from_normal_library_detail_and_metadata_reads()
    {
        Guid assetId = await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000772",
            "Trashed",
            "image/jpeg",
            Snapshot,
            Snapshot,
            sizeBytes: 100);
        await using (VistaraDbContext update = CreateContext(TenantId))
        {
            AssetRow asset = await update.Assets.SingleAsync(row => row.Id == assetId);
            asset.Status = "Trashed";
            asset.Version++;
            await update.SaveChangesAsync();
        }

        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        var scope = new AssetQueryScope(TenantId, ActorId);
        AssetQuerySlice list = await store.QueryAsync(
            scope,
            AssetQueryCriteria.Create(),
            new AssetQueryWindow(Snapshot, Continuation: null),
            CancellationToken.None);
        AssetDetail? detail =
            await store.GetAsync(scope, assetId, CancellationToken.None);
        AssetMetadata? metadata =
            await store.GetMetadataAsync(scope, assetId, CancellationToken.None);

        Assert.Empty(list.Items);
        Assert.Null(detail);
        Assert.Null(metadata);
    }

    [Fact]
    public async Task Metadata_update_is_atomic_versioned_and_conceals_other_tenants()
    {
        Guid assetId = await SeedAssetAsync(
            TenantId,
            "01990a2a-bc00-7000-8000-000000000781",
            "Before",
            "image/jpeg",
            Snapshot,
            Snapshot,
            sizeBytes: 100);
        Guid otherAssetId = await SeedAssetAsync(
            OtherTenantId,
            "01990a2a-bc00-7000-8000-000000000782",
            "Other",
            "image/jpeg",
            Snapshot,
            Snapshot,
            sizeBytes: 100);
        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        var patch = new AssetMetadataPatch(
            HasTitle: true,
            Title: "After",
            HasDescription: true,
            Description: null,
            HasVisibility: false,
            Visibility: null,
            HasCapturedAt: false,
            CapturedAt: null);

        AssetUpdateStoreResult updated = await store.UpdateAsync(
            new AssetQueryScope(TenantId, ActorId),
            assetId,
            expectedVersion: 1,
            "update-1",
            patch,
            Snapshot.AddMinutes(1),
            CancellationToken.None);
        AssetUpdateStoreResult stale = await store.UpdateAsync(
            new AssetQueryScope(TenantId, ActorId),
            assetId,
            expectedVersion: 1,
            "update-2",
            patch,
            Snapshot.AddMinutes(2),
            CancellationToken.None);
        AssetUpdateStoreResult replayed = await store.UpdateAsync(
            new AssetQueryScope(TenantId, ActorId),
            assetId,
            expectedVersion: 1,
            "update-1",
            patch,
            Snapshot.AddMinutes(2),
            CancellationToken.None);
        AssetUpdateStoreResult changedReplay = await store.UpdateAsync(
            new AssetQueryScope(TenantId, ActorId),
            assetId,
            expectedVersion: 1,
            "update-1",
            patch with { Title = "Different" },
            Snapshot.AddMinutes(2),
            CancellationToken.None);
        AssetUpdateStoreResult concealed = await store.UpdateAsync(
            new AssetQueryScope(TenantId, ActorId),
            otherAssetId,
            expectedVersion: 1,
            "update-3",
            patch,
            Snapshot.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(AssetUpdateStoreStatus.Updated, updated.Status);
        Assert.Equal(2, updated.Detail?.Asset.Version);
        Assert.Equal(AssetUpdateStoreStatus.VersionConflict, stale.Status);
        Assert.Equal(AssetUpdateStoreStatus.Replayed, replayed.Status);
        Assert.Equal(AssetUpdateStoreStatus.ValidationFailed, changedReplay.Status);
        Assert.Equal(AssetUpdateStoreStatus.NotFound, concealed.Status);
    }

    [Fact]
    public async Task Database_cancellation_is_forwarded()
    {
        await using VistaraDbContext context = CreateContext(TenantId);
        var store = new RelationalAssetQueryStore(context);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.QueryAsync(
                new AssetQueryScope(TenantId, ActorId),
                AssetQueryCriteria.Create(),
                new AssetQueryWindow(Snapshot, Continuation: null),
                cancellation.Token));
    }

    private VistaraDbContext CreateContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_commands)
            .Options;
        return new VistaraDbContext(options, new FixedTenantScope(tenantId));
    }

    private async Task SeedTenantAsync(Guid tenantId, string slug)
    {
        await using VistaraDbContext context = CreateContext(tenantId);
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = slug,
            Name = slug,
            Status = "Active",
            CreatedAtUtc = Snapshot.AddDays(-10),
            UpdatedAtUtc = Snapshot.AddDays(-10),
            Version = 1,
        });
        if (!await context.Users.AnyAsync(user => user.Id == ActorId))
        {
            context.Users.Add(new UserRow
            {
                Id = ActorId,
                NormalizedEmail = "actor@example.test",
                DisplayName = "Actor",
                Status = "Active",
                CreatedAtUtc = Snapshot.AddDays(-10),
                UpdatedAtUtc = Snapshot.AddDays(-10),
                Version = 1,
            });
        }

        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = tenantId,
            UserId = ActorId,
            Role = "Member",
            Status = "Active",
            InvitedAtUtc = Snapshot.AddDays(-10),
            JoinedAtUtc = Snapshot.AddDays(-10),
            UpdatedAtUtc = Snapshot.AddDays(-10),
            Version = 1,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedAssetAsync(
        Guid tenantId,
        string assetIdText,
        string title,
        string contentType,
        DateTimeOffset? capturedAt,
        DateTimeOffset importedAt,
        long sizeBytes,
        string safeMetadata = "{}",
        string privateMetadata = "{}")
    {
        Guid assetId = Guid.Parse(assetIdText);
        Guid revisionId = RelatedId(assetId, 0x55);
        Guid blobId = RelatedId(assetId, 0xAA);
        await using VistaraDbContext context = CreateContext(tenantId);
        var asset = new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = ActorId,
            CurrentRevisionId = null,
            Title = title,
            Status = "Ready",
            Visibility = "Private",
            CapturedAtUtc = capturedAt,
            CreatedAtUtc = importedAt,
            UpdatedAtUtc = importedAt,
            Version = 1,
        };
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = tenantId,
            Provider = "local",
            Container = "assets",
            ObjectKey = $"tenant/{tenantId:D}/{assetId:D}",
            Sha256 = Convert.ToHexStringLower(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{tenantId:D}:{assetId:D}"))),
            SizeBytes = sizeBytes,
            ContentType = contentType,
            State = "Active",
            CreatedAtUtc = importedAt,
        });
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = revisionId,
            TenantId = tenantId,
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = blobId,
            DetectedFormat = contentType["image/".Length..],
            DetectedContentType = contentType,
            Width = 800,
            Height = 600,
            FrameCount = 1,
            SafeMetadataJson = safeMetadata,
            PrivateMetadataJson = privateMetadata,
            CreatedAtUtc = importedAt,
        });
        await context.SaveChangesAsync();
        asset.CurrentRevisionId = revisionId;
        await context.SaveChangesAsync();
        return assetId;
    }

    private static Guid RelatedId(Guid assetId, byte discriminator)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = assetId.TryWriteBytes(bytes);
        bytes[^1] ^= discriminator;
        return new Guid(bytes);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public int Count => Commands.Count;

        public void Reset() => Commands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
