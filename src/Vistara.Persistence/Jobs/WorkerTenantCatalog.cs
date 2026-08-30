using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Jobs;

public interface IWorkerTenantCatalog
{
    public const int MaximumBatchSize = 256;

    ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
        CancellationToken cancellationToken);

    async ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
        Guid? afterTenantId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ValidatePage(afterTenantId, maximumCount);
        IReadOnlyList<Guid> tenantIds =
            await ListTenantIdsAsync(cancellationToken);
        return tenantIds
            .Where(tenantId =>
                !afterTenantId.HasValue ||
                tenantId.CompareTo(afterTenantId.Value) > 0)
            .Order()
            .Take(maximumCount)
            .ToArray();
    }

    internal static void ValidatePage(Guid? afterTenantId, int maximumCount)
    {
        if (maximumCount is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                $"Worker tenant batches must contain between 1 and " +
                $"{MaximumBatchSize} IDs.");
        }

        if (afterTenantId is Guid cursor &&
            (cursor == Guid.Empty || cursor.Version != 7))
        {
            throw new ArgumentException(
                "The worker tenant cursor must be a UUIDv7 tenant ID.",
                nameof(afterTenantId));
        }
    }
}

internal sealed class WorkerTenantCatalogDbContext(
    DbContextOptions<WorkerTenantCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<WorkerTenantCatalogRow> TenantRoutes =>
        Set<WorkerTenantCatalogRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => WorkerTenantCatalogPersistenceContributor.ConfigureReader(modelBuilder);
}

internal sealed class RelationalWorkerTenantCatalog(
    WorkerTenantCatalogDbContext context) : IWorkerTenantCatalog
{
    private readonly WorkerTenantCatalogDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
        CancellationToken cancellationToken)
    {
        var tenantIds = new List<Guid>();
        Guid? cursor = null;
        while (true)
        {
            IReadOnlyList<Guid> batch = await ListTenantIdsAsync(
                cursor,
                IWorkerTenantCatalog.MaximumBatchSize,
                cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            tenantIds.AddRange(batch);
            if (batch.Count < IWorkerTenantCatalog.MaximumBatchSize)
            {
                break;
            }

            cursor = batch[^1];
        }

        return tenantIds;
    }

    public async ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
        Guid? afterTenantId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        IWorkerTenantCatalog.ValidatePage(afterTenantId, maximumCount);
        IQueryable<WorkerTenantCatalogRow> query;
        if (afterTenantId is Guid cursor)
        {
            query = _context.TenantRoutes.FromSqlInterpolated(
                $"""
                 SELECT
                     routed_tenant_id,
                     worker_enabled,
                     updated_at_utc,
                     version
                 FROM worker_tenant_catalog
                 WHERE worker_enabled = {true}
                   AND routed_tenant_id > {cursor}
                 ORDER BY routed_tenant_id
                 LIMIT {maximumCount}
                 """);
        }
        else
        {
            query = _context.TenantRoutes.FromSqlInterpolated(
                $"""
                 SELECT
                     routed_tenant_id,
                     worker_enabled,
                     updated_at_utc,
                     version
                 FROM worker_tenant_catalog
                 WHERE worker_enabled = {true}
                 ORDER BY routed_tenant_id
                 LIMIT {maximumCount}
                 """);
        }

        WorkerTenantCatalogRow[] routes = await query
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        Guid[] tenantIds = routes
            .Select(route => route.RoutedTenantId.Value)
            .ToArray();
        if (tenantIds.Any(tenantId =>
                tenantId == Guid.Empty || tenantId.Version != 7))
        {
            throw new InvalidOperationException(
                "The worker tenant catalog contains an invalid tenant ID.");
        }

        return tenantIds;
    }
}

internal sealed class WorkerTenantCatalogRow
{
    public TenantKey RoutedTenantId { get; set; }
    public bool WorkerEnabled { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

internal static class WorkerTenantCatalogPersistenceContributor
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<WorkerTenantCatalogRow> entity =
            modelBuilder.Entity<WorkerTenantCatalogRow>();
        ConfigureCore(entity);
        entity.HasOne<TenantRow>()
            .WithOne()
            .HasForeignKey<WorkerTenantCatalogRow>(
                row => row.RoutedTenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    internal static void ConfigureReader(ModelBuilder modelBuilder) =>
        ConfigureCore(modelBuilder.Entity<WorkerTenantCatalogRow>());

    private static void ConfigureCore(
        EntityTypeBuilder<WorkerTenantCatalogRow> entity)
    {
        entity.ToTable("worker_tenant_catalog", table =>
            table.HasCheckConstraint(
                "ck_worker_tenant_catalog_version",
                "\"version\" >= 1"));
        entity.HasKey(row => row.RoutedTenantId);
        entity.HasIndex(row => new
        {
            row.WorkerEnabled,
            row.RoutedTenantId,
        });
        entity.Property(row => row.RoutedTenantId)
            .HasColumnName("routed_tenant_id")
            .HasConversion<TenantKeyValueConverter>();
        entity.Property(row => row.WorkerEnabled)
            .HasColumnName("worker_enabled");
        entity.Property(row => row.UpdatedAtUtc)
            .HasColumnName("updated_at_utc");
        entity.Property(row => row.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
    }
}

internal sealed class WorkerTenantCatalogWriter(VistaraDbContext context)
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    internal void Add(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _context.WorkerTenantCatalog.Add(ToRow(tenant));
    }

    internal async ValueTask UpdateAsync(
        Tenant tenant,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        TenantKey tenantKey = tenant.Id.Value;
        WorkerTenantCatalogRow row = await _context.WorkerTenantCatalog
            .SingleOrDefaultAsync(
                candidate => candidate.RoutedTenantId == tenantKey,
                cancellationToken)
            ?? throw new DbUpdateConcurrencyException(
                "The worker tenant catalog entry no longer exists.");
        if (row.Version != expectedVersion)
        {
            throw new DbUpdateConcurrencyException(
                $"The persisted worker tenant catalog version {row.Version} " +
                $"does not match expected version {expectedVersion}.");
        }

        row.WorkerEnabled = IsWorkerEnabled(tenant);
        row.UpdatedAtUtc = tenant.UpdatedAt;
        row.Version = tenant.Version;
        _context.Entry(row)
            .Property(candidate => candidate.Version)
            .OriginalValue = expectedVersion;
    }

    private static WorkerTenantCatalogRow ToRow(Tenant tenant) => new()
    {
        RoutedTenantId = tenant.Id.Value,
        WorkerEnabled = IsWorkerEnabled(tenant),
        UpdatedAtUtc = tenant.UpdatedAt,
        Version = tenant.Version,
    };

    private static bool IsWorkerEnabled(Tenant tenant) =>
        tenant.Status == TenantStatus.Active;
}
