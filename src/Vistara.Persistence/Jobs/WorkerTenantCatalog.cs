using Microsoft.EntityFrameworkCore;

namespace Vistara.Persistence.Jobs;

public interface IWorkerTenantCatalog
{
    ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
        CancellationToken cancellationToken);
}

internal sealed class WorkerTenantCatalogDbContext(
    DbContextOptions<WorkerTenantCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<WorkerTenantCatalogRow> TenantRoutes =>
        Set<WorkerTenantCatalogRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkerTenantCatalogRow>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("authentication_routes");
            entity.Property(row => row.TenantId).HasColumnName("routed_tenant_id");
        });
    }
}

internal sealed class RelationalWorkerTenantCatalog(
    WorkerTenantCatalogDbContext context) : IWorkerTenantCatalog
{
    private readonly WorkerTenantCatalogDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
        CancellationToken cancellationToken)
    {
        Guid[] tenantIds = await _context.TenantRoutes
            .AsNoTracking()
            .Select(route => route.TenantId)
            .Distinct()
            .OrderBy(tenantId => tenantId)
            .ToArrayAsync(cancellationToken);
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
    public Guid TenantId { get; set; }
}
