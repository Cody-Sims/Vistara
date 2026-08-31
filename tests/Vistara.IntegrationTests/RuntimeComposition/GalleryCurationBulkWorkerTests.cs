using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Favorites;
using Vistara.IntegrationTests.Persistence;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Curation;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Gallery;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.RuntimeComposition;

/// <summary>
/// Proves the bulk curation work queued by <c>POST /api/v1/assets/bulk</c> is
/// claimed and applied by the durable job worker instead of dead-lettering.
/// </summary>
public sealed class GalleryCurationBulkWorkerTests
{
    private const string FavoriteInsert = "INSERT INTO \"asset_favorites\"";

    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Worker_applies_a_queued_bulk_favorite_and_completes_the_job()
    {
        await using CurationWorkerDatabase database =
            await CurationWorkerDatabase.CreateAsync();
        Guid jobId = Guid.CreateVersion7();
        CurationResult<BulkCurationSubmission> queued = await database.QueueAsync(
            jobId,
            new BulkCurationRequest(
                [
                    new BulkCurationTarget(database.FirstAssetId, 1),
                    new BulkCurationTarget(database.SecondAssetId, 1),
                ],
                new BulkCurationAction("setFavorite", null, null, true)),
            "bulk-favorite");

        await database.RunWorkerAsync();

        Assert.True(queued.IsSuccess, queued.Error?.Code);
        await using VistaraDbContext context = database.CreateContext();
        Assert.Equal(
            [database.FirstAssetId, database.SecondAssetId],
            await context.AssetFavorites
                .Where(favorite => favorite.UserId == database.OwnerId)
                .OrderBy(favorite => favorite.AssetId)
                .Select(favorite => favorite.AssetId)
                .ToArrayAsync());
        long[] versions = await context.Assets
            .OrderBy(asset => asset.Id)
            .Select(asset => asset.Version)
            .ToArrayAsync();
        Assert.Equal([2L, 2L], versions);
        Assert.Equal("Completed", await database.ReadJobStateAsync(jobId));
    }

    [Fact]
    public async Task Worker_redelivery_never_duplicates_bulk_tag_effects()
    {
        await using CurationWorkerDatabase database =
            await CurationWorkerDatabase.CreateAsync();
        Guid jobId = Guid.CreateVersion7();
        _ = await database.QueueAsync(
            jobId,
            new BulkCurationRequest(
                [
                    new BulkCurationTarget(database.FirstAssetId, 1),
                    new BulkCurationTarget(database.SecondAssetId, 1),
                ],
                new BulkCurationAction("addTag", database.TagId, null, null)),
            "bulk-tag");

        await database.RunWorkerAsync();
        await database.RedeliverAsync(jobId);
        await database.RunWorkerAsync();

        await using VistaraDbContext context = database.CreateContext();
        Assert.Equal(2, await context.AssetTags.CountAsync());
        long[] versions = await context.Assets
            .OrderBy(asset => asset.Id)
            .Select(asset => asset.Version)
            .ToArrayAsync();
        Assert.Equal([2L, 2L], versions);
        Assert.Equal("Completed", await database.ReadJobStateAsync(jobId));
    }

    [Fact]
    public async Task Worker_records_partial_outcomes_and_still_completes_the_job()
    {
        await using CurationWorkerDatabase database =
            await CurationWorkerDatabase.CreateAsync();
        Guid jobId = Guid.CreateVersion7();
        _ = await database.QueueAsync(
            jobId,
            new BulkCurationRequest(
                [
                    new BulkCurationTarget(database.FirstAssetId, 1),
                    new BulkCurationTarget(database.SecondAssetId, 99),
                ],
                new BulkCurationAction("setFavorite", null, null, true)),
            "bulk-partial");

        await database.RunWorkerAsync();

        await using VistaraDbContext context = database.CreateContext();
        Assert.Equal(
            database.FirstAssetId,
            Assert.Single(await context.AssetFavorites.ToListAsync()).AssetId);
        Assert.Equal("Completed", await database.ReadJobStateAsync(jobId));
        AuditEventRow audit = Assert.Single(
            await context.AuditEvents
                .Where(row => row.Action == "gallery.curation.bulk")
                .ToListAsync());
        Assert.Equal(jobId.ToString("D"), audit.ResourceIdentifier);
        Assert.Equal("Failed", audit.Outcome);
        Assert.Contains(
            $"succeeded:v2",
            audit.AfterJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "conflict:asset_version_conflict",
            audit.AfterJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_never_applies_bulk_work_to_another_tenants_asset()
    {
        await using CurationWorkerDatabase database =
            await CurationWorkerDatabase.CreateAsync();
        (Guid foreignTenantId, Guid foreignAssetId) =
            await database.SeedForeignTenantAsync();
        Guid jobId = Guid.CreateVersion7();
        _ = await database.QueueAsync(
            jobId,
            new BulkCurationRequest(
                [
                    new BulkCurationTarget(database.FirstAssetId, 1),
                    new BulkCurationTarget(foreignAssetId, 1),
                ],
                new BulkCurationAction("setFavorite", null, null, true)),
            "bulk-cross-tenant");

        await database.RunWorkerAsync();

        await using VistaraDbContext foreign =
            database.CreateContext(foreignTenantId);
        Assert.Empty(await foreign.AssetFavorites.ToListAsync());
        Assert.Equal(
            1L,
            (await foreign.Assets.SingleAsync(asset => asset.Id == foreignAssetId))
                .Version);
        await using VistaraDbContext context = database.CreateContext();
        Assert.Equal(
            database.FirstAssetId,
            Assert.Single(await context.AssetFavorites.ToListAsync()).AssetId);
        Assert.Equal("Completed", await database.ReadJobStateAsync(jobId));
    }

    [Fact]
    public async Task Worker_retries_a_provider_fault_and_applies_the_batch_once()
    {
        await using CurationWorkerDatabase database =
            await CurationWorkerDatabase.CreateAsync();
        Guid jobId = Guid.CreateVersion7();
        _ = await database.QueueAsync(
            jobId,
            new BulkCurationRequest(
                [new BulkCurationTarget(database.FirstAssetId, 1)],
                new BulkCurationAction("setFavorite", null, null, true)),
            "bulk-transient");
        database.Faults.Arm(
            FavoriteInsert,
            new SqliteException("database is locked", 5));

        await database.RunWorkerAsync();

        JobRow retried = await database.ReadJobAsync(jobId);
        Assert.True(database.Faults.Thrown > 0);
        Assert.Equal("RetryScheduled", retried.State);
        Assert.Equal(1, retried.Attempts);
        Assert.Equal("jobs.provider_unavailable", retried.FailureCode);
        Assert.True(retried.AvailableAtUtc > retried.CreatedAtUtc);
        await using (VistaraDbContext failedRun = database.CreateContext())
        {
            Assert.Empty(await failedRun.AssetFavorites.ToListAsync());
            AuditEventRow audit = Assert.Single(
                await failedRun.AuditEvents
                    .Where(row => row.Action == "gallery.curation.bulk")
                    .ToListAsync());
            Assert.Contains(
                "failed:curation_store_unavailable",
                audit.AfterJson,
                StringComparison.Ordinal);
        }

        database.Faults.Disarm();
        await database.MakeAvailableAsync(jobId);
        await database.RunWorkerAsync();

        JobRow completed = await database.ReadJobAsync(jobId);
        Assert.Equal("Completed", completed.State);
        await using VistaraDbContext context = database.CreateContext();
        Assert.Equal(
            database.FirstAssetId,
            Assert.Single(await context.AssetFavorites.ToListAsync()).AssetId);
        Assert.Equal(
            2L,
            (await context.Assets.SingleAsync(
                asset => asset.Id == database.FirstAssetId)).Version);
    }

    [Fact]
    public void Worker_startup_validation_resolves_the_bulk_curation_handler()
    {
        ServiceCollection services = [];
        services.AddSingleton(NoInvocationProxy.Create<IBlobStore>());
        services.AddSingleton(NoInvocationProxy.Create<IImageProcessor>());
        services.AddVistaraWorkerPlatform(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "Sqlite",
                    ["Persistence:ConnectionString"] = "Data Source=:memory:",
                    ["Worker:InstanceId"] = "curation-bulk-validation",
                })
                .Build());
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        provider.ValidateVistaraWorkerPlatformComposition();

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<IMutableTenantScope>()
            .Establish(Guid.CreateVersion7());
        Assert.Single(
            scope.ServiceProvider.GetServices<IJobHandler>(),
            handler => handler is GalleryCurationBulkJobHandler);
    }

    internal sealed class CurationWorkerDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;
        private readonly ServiceProvider _worker;

        private CurationWorkerDatabase(
            SqliteConnection anchor,
            string connectionString,
            ServiceProvider worker,
            GalleryCurationFaultClassificationTests.FaultInjector faults,
            Guid tenantId,
            Guid ownerId,
            Guid firstAssetId,
            Guid secondAssetId,
            Guid tagId)
        {
            _anchor = anchor;
            _connectionString = connectionString;
            _worker = worker;
            Faults = faults;
            TenantId = tenantId;
            OwnerId = ownerId;
            FirstAssetId = firstAssetId;
            SecondAssetId = secondAssetId;
            TagId = tagId;
        }

        internal GalleryCurationFaultClassificationTests.FaultInjector Faults { get; }

        internal Guid TenantId { get; }

        internal Guid OwnerId { get; }

        internal Guid FirstAssetId { get; }

        internal Guid SecondAssetId { get; }

        internal Guid TagId { get; }

        internal static async ValueTask<CurationWorkerDatabase> CreateAsync()
        {
            string name = $"GalleryCurationBulk-{Guid.NewGuid():N}";
            string connectionString =
                $"Data Source={name};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            Guid tenantId = Guid.CreateVersion7();
            Guid ownerId = Guid.CreateVersion7();
            Guid firstAssetId = Guid.CreateVersion7();
            Guid secondAssetId = Guid.CreateVersion7();
            Guid tagId = Guid.CreateVersion7();
            if (secondAssetId.CompareTo(firstAssetId) < 0)
            {
                (firstAssetId, secondAssetId) = (secondAssetId, firstAssetId);
            }

            await using (VistaraDbContext seed =
                         CreateContext(connectionString, tenantId))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Tenants.Add(new TenantRow
                {
                    Id = tenantId,
                    TenantId = tenantId,
                    Slug = "curation-bulk",
                    Name = "Curation bulk",
                    Status = "Active",
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now,
                    Version = 1,
                });
                seed.Users.Add(new UserRow
                {
                    Id = ownerId,
                    NormalizedEmail = "owner@curation.invalid",
                    DisplayName = "Owner",
                    Status = "Active",
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now,
                    Version = 1,
                });
                seed.Tags.Add(new TagRow
                {
                    Id = tagId,
                    TenantId = tenantId,
                    DisplayName = "Travel",
                    NormalizedName = "travel",
                    Version = 1,
                });
                foreach (Guid assetId in new[] { firstAssetId, secondAssetId })
                {
                    seed.Assets.Add(new AssetRow
                    {
                        Id = assetId,
                        TenantId = tenantId,
                        OwnerId = ownerId,
                        Title = "Asset",
                        Status = "Ready",
                        Visibility = "Private",
                        CreatedAtUtc = Now,
                        UpdatedAtUtc = Now,
                        Version = 1,
                    });
                }

                await seed.SaveChangesAsync();
                await seed.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO worker_tenant_catalog
                         (routed_tenant_id, worker_enabled, updated_at_utc, version)
                     VALUES ({tenantId}, {true}, {Now}, {1L})
                     """);
            }

            var faults = new GalleryCurationFaultClassificationTests.FaultInjector();
            ServiceCollection services = [];
            services.AddSingleton(NoInvocationProxy.Create<IBlobStore>());
            services.AddSingleton(NoInvocationProxy.Create<IImageProcessor>());
            // Registered before the platform so the fault injector rides the
            // worker's own VistaraDbContext options.
            services.AddDbContext<VistaraDbContext>(options => options
                .UseSqlite(connectionString)
                .AddInterceptors(faults));
            services.AddVistaraWorkerPlatform(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Persistence:Provider"] = "Sqlite",
                        ["Persistence:ConnectionString"] = connectionString,
                        ["Worker:InstanceId"] = "curation-bulk-test",
                    })
                    .Build());
            ServiceProvider worker = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true });
            return new CurationWorkerDatabase(
                anchor,
                connectionString,
                worker,
                faults,
                tenantId,
                ownerId,
                firstAssetId,
                secondAssetId,
                tagId);
        }

        internal VistaraDbContext CreateContext() =>
            CreateContext(_connectionString, TenantId);

        internal VistaraDbContext CreateContext(Guid tenantId) =>
            CreateContext(_connectionString, tenantId);

        internal async ValueTask<(Guid TenantId, Guid AssetId)>
            SeedForeignTenantAsync()
        {
            Guid tenantId = Guid.CreateVersion7();
            Guid ownerId = Guid.CreateVersion7();
            Guid assetId = Guid.CreateVersion7();
            await using VistaraDbContext context = CreateContext(tenantId);
            context.Tenants.Add(new TenantRow
            {
                Id = tenantId,
                TenantId = tenantId,
                Slug = "curation-bulk-foreign",
                Name = "Curation bulk foreign",
                Status = "Active",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
                Version = 1,
            });
            context.Users.Add(new UserRow
            {
                Id = ownerId,
                NormalizedEmail = "foreign@curation.invalid",
                DisplayName = "Foreign owner",
                Status = "Active",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
                Version = 1,
            });
            context.Assets.Add(new AssetRow
            {
                Id = assetId,
                TenantId = tenantId,
                OwnerId = ownerId,
                Title = "Foreign asset",
                Status = "Ready",
                Visibility = "Private",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
                Version = 1,
            });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO worker_tenant_catalog
                     (routed_tenant_id, worker_enabled, updated_at_utc, version)
                 VALUES ({tenantId}, {true}, {Now}, {1L})
                 """);
            return (tenantId, assetId);
        }

        internal async ValueTask<CurationResult<BulkCurationSubmission>> QueueAsync(
            Guid jobId,
            BulkCurationRequest request,
            string idempotencyKey)
        {
            await using VistaraDbContext context = CreateContext();
            var application = new FavoriteApplication(
                new RelationalGalleryCurationStore(context));
            return await application.QueueBulkAsync(
                new CurationActor(TenantId, OwnerId, canManageAll: false),
                jobId,
                request,
                idempotencyKey,
                Now,
                CancellationToken.None);
        }

        internal Task RunWorkerAsync() =>
            _worker
                .GetRequiredService<JobWorkerRuntime>()
                .RunOnceAsync(CancellationToken.None);

        internal async ValueTask<JobRow> ReadJobAsync(Guid jobId)
        {
            await using VistaraDbContext context = CreateContext();
            return await context.Jobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId);
        }

        internal async ValueTask MakeAvailableAsync(Guid jobId)
        {
            await using VistaraDbContext context = CreateContext();
            _ = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE jobs
                 SET available_at_utc = {Now}
                 WHERE id = {jobId}
                 """);
        }

        internal async ValueTask<string?> ReadJobStateAsync(Guid jobId)
        {
            await using VistaraDbContext context = CreateContext();
            return await context.Jobs
                .AsNoTracking()
                .Where(job => job.Id == jobId)
                .Select(job => job.State)
                .SingleOrDefaultAsync();
        }

        internal async ValueTask RedeliverAsync(Guid jobId)
        {
            await using VistaraDbContext context = CreateContext();
            _ = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE jobs
                 SET state = 'Pending',
                     attempts = 0,
                     lease_owner = NULL,
                     lease_acquired_at_utc = NULL,
                     lease_heartbeat_at_utc = NULL,
                     lease_expires_at_utc = NULL,
                     completed_at_utc = NULL,
                     version = version + 1
                 WHERE id = {jobId}
                 """);
        }

        public async ValueTask DisposeAsync()
        {
            await _worker.DisposeAsync();
            await _anchor.DisposeAsync();
        }

        private static VistaraDbContext CreateContext(
            string connectionString,
            Guid tenantId) =>
            new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(connectionString)
                    .Options,
                new FixedTenantScope(tenantId));
    }

    [SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy generates a runtime subclass.")]
    private class NoInvocationProxy : DispatchProxy
    {
        internal static T Create<T>()
            where T : class =>
            DispatchProxy.Create<T, NoInvocationProxy>();

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            throw new InvalidOperationException(
                $"{targetMethod?.Name ?? "Unknown"} should not be invoked.");
    }
}
