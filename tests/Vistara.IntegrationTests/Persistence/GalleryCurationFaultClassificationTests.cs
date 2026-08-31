using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vistara.Application.Gallery;
using Vistara.Persistence;
using Vistara.Persistence.Gallery.Curation;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Persistence;

/// <summary>
/// Separates the two ways a bulk curation item can fail to write: a settled
/// precondition the caller must resolve, and an unavailable database the
/// durable job should retry.
/// </summary>
public sealed class GalleryCurationFaultClassificationTests
{
    private const string FavoriteInsert = "INSERT INTO \"asset_favorites\"";

    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stale_asset_versions_stay_conflicts()
    {
        await using CurationFaultDatabase database =
            await CurationFaultDatabase.CreateAsync();

        IReadOnlyList<BulkCurationItemResult> results =
            await database.ExecuteFavoriteAsync(
                [
                    new BulkCurationTarget(database.FirstAssetId, 1),
                    new BulkCurationTarget(database.SecondAssetId, 99),
                ]);

        Assert.Collection(
            results,
            result => Assert.Equal("succeeded", result.Status),
            result =>
            {
                Assert.Equal("conflict", result.Status);
                Assert.Equal("asset_version_conflict", result.ErrorCode);
            });
        Assert.Single(await database.CreateContext().AssetFavorites.ToListAsync());
    }

    [Fact]
    public async Task Provider_faults_are_reported_as_unavailable_not_conflicts()
    {
        await using CurationFaultDatabase database =
            await CurationFaultDatabase.CreateAsync();
        database.Faults.Arm(
            FavoriteInsert,
            new SqliteException("database is locked", 5));

        IReadOnlyList<BulkCurationItemResult> results =
            await database.ExecuteFavoriteAsync(
                [new BulkCurationTarget(database.FirstAssetId, 1)]);

        BulkCurationItemResult result = Assert.Single(results);
        Assert.Equal("failed", result.Status);
        Assert.Equal("curation_store_unavailable", result.ErrorCode);
        Assert.Null(result.Version);
        Assert.True(database.Faults.Thrown > 0);
        await using VistaraDbContext context = database.CreateContext();
        Assert.Empty(await context.AssetFavorites.ToListAsync());
        Assert.Equal(
            1L,
            (await context.Assets.SingleAsync(
                asset => asset.Id == database.FirstAssetId)).Version);
    }

    [Fact]
    public async Task Unattributed_statement_failures_are_reported_as_unavailable()
    {
        await using CurationFaultDatabase database =
            await CurationFaultDatabase.CreateAsync();
        database.Faults.Arm(
            FavoriteInsert,
            new DbUpdateException("the statement failed"));

        IReadOnlyList<BulkCurationItemResult> results =
            await database.ExecuteFavoriteAsync(
                [new BulkCurationTarget(database.FirstAssetId, 1)]);

        BulkCurationItemResult result = Assert.Single(results);
        Assert.Equal("failed", result.Status);
        Assert.Equal("curation_store_unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task Commit_time_failures_still_surface_to_the_caller()
    {
        await using CurationFaultDatabase database =
            await CurationFaultDatabase.CreateAsync();
        database.Faults.ArmCommit(new SqliteException("disk I/O error", 10));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await database.ExecuteFavoriteAsync(
                [new BulkCurationTarget(database.FirstAssetId, 1)]));

        await using VistaraDbContext context = database.CreateContext();
        Assert.Empty(await context.AssetFavorites.ToListAsync());
    }

    internal sealed class CurationFaultDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;

        private CurationFaultDatabase(
            SqliteConnection anchor,
            string connectionString,
            Guid tenantId,
            Guid ownerId,
            Guid firstAssetId,
            Guid secondAssetId)
        {
            _anchor = anchor;
            _connectionString = connectionString;
            TenantId = tenantId;
            OwnerId = ownerId;
            FirstAssetId = firstAssetId;
            SecondAssetId = secondAssetId;
        }

        internal Guid TenantId { get; }

        internal Guid OwnerId { get; }

        internal Guid FirstAssetId { get; }

        internal Guid SecondAssetId { get; }

        internal FaultInjector Faults { get; } = new();

        internal static async ValueTask<CurationFaultDatabase> CreateAsync()
        {
            string name = $"CurationFault-{Guid.NewGuid():N}";
            string connectionString =
                $"Data Source={name};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            Guid tenantId = Guid.CreateVersion7();
            Guid ownerId = Guid.CreateVersion7();
            Guid firstAssetId = Guid.CreateVersion7();
            Guid secondAssetId = Guid.CreateVersion7();
            var database = new CurationFaultDatabase(
                anchor,
                connectionString,
                tenantId,
                ownerId,
                firstAssetId,
                secondAssetId);
            await using VistaraDbContext seed = database.CreateContext();
            await seed.Database.EnsureCreatedAsync();
            seed.Tenants.Add(new TenantRow
            {
                Id = tenantId,
                TenantId = tenantId,
                Slug = "curation-fault",
                Name = "Curation fault",
                Status = "Active",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
                Version = 1,
            });
            seed.Users.Add(new UserRow
            {
                Id = ownerId,
                NormalizedEmail = "owner@fault.invalid",
                DisplayName = "Owner",
                Status = "Active",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
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
            return database;
        }

        internal VistaraDbContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(Faults)
                    .Options,
                new FixedTenantScope(TenantId));

        internal async ValueTask<IReadOnlyList<BulkCurationItemResult>>
            ExecuteFavoriteAsync(IReadOnlyList<BulkCurationTarget> targets)
        {
            await using VistaraDbContext context = CreateContext();
            var store = new RelationalGalleryCurationStore(context);
            return await store.ExecuteBulkAsync(
                new CurationActor(TenantId, OwnerId, canManageAll: false),
                new BulkCurationRequest(
                    targets,
                    new BulkCurationAction("setFavorite", null, null, true)),
                Now,
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }

    /// <summary>
    /// Injects a provider failure at statement or commit time without touching
    /// the store, so the classification under test is the production one.
    /// </summary>
    internal sealed class FaultInjector : IDbCommandInterceptor, IDbTransactionInterceptor
    {
        private string? _trigger;
        private Exception? _statementFault;
        private Exception? _commitFault;
        private int _thrown;

        internal int Thrown => _thrown;

        internal void Arm(string trigger, Exception fault)
        {
            _trigger = trigger;
            _statementFault = fault;
        }

        internal void ArmCommit(Exception fault) => _commitFault = fault;

        internal void Disarm()
        {
            _trigger = null;
            _statementFault = null;
            _commitFault = null;
        }

        public InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfArmed(command);
            return result;
        }

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return ValueTask.FromResult(result);
        }

        public InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfArmed(command);
            return result;
        }

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return ValueTask.FromResult(result);
        }

        public InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
        {
            ThrowCommitIfArmed();
            return result;
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            ThrowCommitIfArmed();
            return ValueTask.FromResult(result);
        }

        private void ThrowIfArmed(DbCommand command)
        {
            if (_statementFault is null ||
                _trigger is null ||
                !command.CommandText.Contains(_trigger, StringComparison.Ordinal))
            {
                return;
            }

            _ = Interlocked.Increment(ref _thrown);
            throw _statementFault;
        }

        private void ThrowCommitIfArmed()
        {
            if (_commitFault is null)
            {
                return;
            }

            _ = Interlocked.Increment(ref _thrown);
            throw _commitFault;
        }
    }
}
