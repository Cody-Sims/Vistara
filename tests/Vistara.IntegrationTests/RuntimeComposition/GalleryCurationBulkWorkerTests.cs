using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Favorites;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Curation;
using Vistara.Persistence.Model;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.RuntimeComposition;

/// <summary>
/// Proves the bulk curation work queued by <c>POST /api/v1/assets/bulk</c> is
/// claimed and applied by the durable job worker instead of dead-lettering.
/// </summary>
public sealed class GalleryCurationBulkWorkerTests
{
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

    internal sealed class CurationWorkerDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;
        private readonly ServiceProvider _worker;

        private CurationWorkerDatabase(
            SqliteConnection anchor,
            string connectionString,
            ServiceProvider worker,
            Guid tenantId,
            Guid ownerId,
            Guid firstAssetId,
            Guid secondAssetId,
            Guid tagId)
        {
            _anchor = anchor;
            _connectionString = connectionString;
            _worker = worker;
            TenantId = tenantId;
            OwnerId = ownerId;
            FirstAssetId = firstAssetId;
            SecondAssetId = secondAssetId;
            TagId = tagId;
        }

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

            ServiceCollection services = [];
            services.AddSingleton(NoInvocationProxy.Create<IBlobStore>());
            services.AddSingleton(NoInvocationProxy.Create<IImageProcessor>());
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
                tenantId,
                ownerId,
                firstAssetId,
                secondAssetId,
                tagId);
        }

        internal VistaraDbContext CreateContext() =>
            CreateContext(_connectionString, TenantId);

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
