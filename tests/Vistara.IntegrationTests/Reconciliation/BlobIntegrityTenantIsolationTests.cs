using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Worker.Features.Reconciliation.Storage;
using Xunit;

namespace Vistara.IntegrationTests.Reconciliation;

public sealed class BlobIntegrityTenantIsolationTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tenant_namespaces_cover_every_tenant_owned_key_layout()
    {
        Guid tenantId = Guid.CreateVersion7();
        string shard = tenantId.ToString("N")[..2];
        string dashed = tenantId.ToString("D");
        string compact = tenantId.ToString("N");
        Guid uploadId = Guid.CreateVersion7();

        Assert.True(TenantBlobNamespaces.Contains(
            tenantId,
            $"staging/{shard}/{dashed}/{uploadId:D}"));
        Assert.True(TenantBlobNamespaces.Contains(
            tenantId,
            $"originals/{shard}/{dashed}/{uploadId:D}/1/{uploadId:D}.jpg"));
        Assert.True(TenantBlobNamespaces.Contains(
            tenantId,
            $"staging/derivatives/{compact}/{uploadId:N}/1/abc.webp"));
        Assert.False(TenantBlobNamespaces.Contains(
            tenantId,
            "derivatives/v1/ab/abcdef.webp"));
    }

    [Fact]
    public void Tenant_namespaces_never_overlap_between_tenants()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();
        string firstKey =
            $"originals/{first.ToString("N")[..2]}/{first:D}/x/1/x.jpg";

        Assert.True(TenantBlobNamespaces.Contains(first, firstKey));
        Assert.False(TenantBlobNamespaces.Contains(second, firstKey));
    }

    [Fact]
    public async Task Orphan_cleanup_only_deletes_the_requested_tenant_objects()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenant = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);
        await SeedTenantAsync(dataSource, otherTenant);

        string ownOrphan = Original(tenantId, "own-orphan");
        string ownKnown = Original(tenantId, "own-known");
        string foreignOrphan = Original(otherTenant, "foreign-orphan");
        string foreignKnown = Original(otherTenant, "foreign-known");
        const string SharedDerivative = "derivatives/v1/ab/abcdef012345.webp";
        await SeedBlobAsync(dataSource, tenantId, ownKnown);
        await SeedBlobAsync(dataSource, otherTenant, foreignKnown);

        var store = new ReconciliationBlobStore();
        foreach (string key in new[]
                 {
                     ownOrphan, ownKnown, foreignOrphan, foreignKnown,
                     SharedDerivative,
                 })
        {
            store.Add(key, Created);
        }

        await using ServiceProvider provider = BuildProvider(
            dataSource,
            store,
            new BlobIntegrityOptions { DeleteOrphans = true });

        BlobIntegrityReport report = await RunAsync(provider, tenantId);

        Assert.Equal(1, report.OrphansDetected);
        Assert.Equal(1, report.OrphansDeleted);
        Assert.DoesNotContain(ownOrphan, store.Keys, StringComparer.Ordinal);
        Assert.Contains(ownKnown, store.Keys, StringComparer.Ordinal);
        Assert.Contains(foreignOrphan, store.Keys, StringComparer.Ordinal);
        Assert.Contains(foreignKnown, store.Keys, StringComparer.Ordinal);
        Assert.Contains(SharedDerivative, store.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Foreign_objects_are_never_listed_for_classification()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid otherTenant = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);
        await SeedTenantAsync(dataSource, otherTenant);

        var store = new ReconciliationBlobStore();
        store.Add(Original(otherTenant, "foreign"), Created);
        store.Add(Staging(otherTenant, "foreign-staging"), Created);
        store.Add("derivatives/v1/cd/cdef01234567.webp", Created);

        await using ServiceProvider provider = BuildProvider(
            dataSource,
            store,
            new BlobIntegrityOptions { DeleteOrphans = true });

        BlobIntegrityReport report = await RunAsync(provider, tenantId);

        Assert.Equal(0, report.OrphansDetected);
        Assert.Equal(0, report.OrphansDeleted);
        Assert.Equal(3, store.Keys.Count);
        Assert.All(
            store.ListedPrefixes,
            prefix => Assert.Contains(
                prefix,
                TenantBlobNamespaces.For(tenantId),
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task Orphan_enumeration_is_capped_for_each_tenant_sweep()
    {
        Guid tenantId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);

        var store = new ReconciliationBlobStore();
        for (int index = 0; index < 40; index++)
        {
            store.Add(Original(tenantId, $"orphan-{index:D3}"), Created);
            store.Add(Staging(tenantId, $"staged-{index:D3}"), Created);
        }

        await using ServiceProvider provider = BuildProvider(
            dataSource,
            store,
            new BlobIntegrityOptions
            {
                DeleteOrphans = true,
                MaximumStorageObjects = 10,
            });

        BlobIntegrityReport report = await RunAsync(provider, tenantId);

        Assert.Equal(10, report.OrphansDetected);
        Assert.Equal(10, report.OrphansDeleted);
        Assert.Equal(70, store.Keys.Count);
    }

    [Fact]
    public async Task Young_tenant_objects_are_never_deleted()
    {
        Guid tenantId = Guid.CreateVersion7();
        string dataSource = NewDataSource();
        await using var anchor = new SqliteConnection(dataSource);
        await anchor.OpenAsync(CancellationToken.None);
        await SeedTenantAsync(dataSource, tenantId);

        var store = new ReconciliationBlobStore();
        string fresh = Staging(tenantId, "fresh");
        store.Add(fresh, Now.AddMinutes(-5));

        await using ServiceProvider provider = BuildProvider(
            dataSource,
            store,
            new BlobIntegrityOptions { DeleteOrphans = true });

        BlobIntegrityReport report = await RunAsync(provider, tenantId);

        Assert.Equal(0, report.OrphansDetected);
        Assert.Contains(fresh, store.Keys, StringComparer.Ordinal);
    }

    private static string Original(Guid tenantId, string leaf) =>
        $"originals/{tenantId.ToString("N")[..2]}/{tenantId:D}/{leaf}/1/{leaf}.jpg";

    private static string Staging(Guid tenantId, string leaf) =>
        $"staging/{tenantId.ToString("N")[..2]}/{tenantId:D}/{leaf}";

    private static async Task<BlobIntegrityReport> RunAsync(
        ServiceProvider provider,
        Guid tenantId)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<BlobIntegrityService>()
            .RunAsync(
                new BlobIntegrityRequest(tenantId, Cursor: null, DryRun: false),
                CancellationToken.None);
    }

    private static ServiceProvider BuildProvider(
        string dataSource,
        IBlobStore store,
        BlobIntegrityOptions options)
    {
        ServiceCollection services = [];
        services.AddScoped<ScopedTenant>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<ScopedTenant>());
        services.AddVistaraPersistence(persistence =>
        {
            persistence.Provider = VistaraDatabaseProvider.Sqlite;
            persistence.ConnectionString = dataSource;
        });
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton(store);
        services.AddSingleton(options);
        services.AddVistaraBlobIntegrityReconciliation();
        return services.BuildServiceProvider();
    }

    private static string NewDataSource() =>
        $"Data Source=BlobIsolation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static async Task SeedTenantAsync(string dataSource, Guid tenantId)
    {
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(dataSource)
                .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        await context.Database.EnsureCreatedAsync(CancellationToken.None);
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = tenantId.ToString("N"),
            Name = tenantId.ToString("N"),
            Status = "Active",
            CreatedAtUtc = Created,
            UpdatedAtUtc = Created,
            Version = 1,
        });
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task SeedBlobAsync(
        string dataSource,
        Guid tenantId,
        string objectKey)
    {
        DbContextOptions<VistaraDbContext> options =
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(dataSource)
                .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        context.Blobs.Add(new BlobRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = new TenantKey(tenantId),
            Provider = "local",
            Container = "media",
            ObjectKey = objectKey,
            ProviderVersion = "v1",
            Sha256 = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(objectKey))),
            SizeBytes = 1024,
            ContentType = "image/jpeg",
            State = "Active",
            CreatedAtUtc = Created,
        });
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class ScopedTenant : IMutableTenantScope
    {
        private Guid? _tenantId;

        public Guid TenantId =>
            _tenantId ??
            throw new InvalidOperationException(
                "A tenant scope must be established.");

        public void Establish(Guid tenantId)
        {
            if (_tenantId.HasValue && _tenantId.Value != tenantId)
            {
                throw new InvalidOperationException(
                    "A reconciliation scope cannot switch tenants.");
            }

            _tenantId = tenantId;
        }
    }
}
