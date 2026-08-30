using Microsoft.EntityFrameworkCore;

namespace Vistara.Persistence.Outbox;

public sealed class OutboxDbContext(
    DbContextOptions<OutboxDbContext> options,
    IOutboxTenantContext tenantContext) :
    DbContext(options),
    IOutboxTenantContext
{
    public Guid TenantId => tenantContext.TenantId;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTenantWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantWrites();
        return base.SaveChangesAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(
            new TenantRlsCommandInterceptor(tenantContext));

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        OutboxPersistenceContributor.Configure(modelBuilder, this);

    private void ValidateTenantWrites()
    {
        Guid tenantId = TenantScopeGuard.RequireTenantId(this);
        foreach (var entry in ChangeTracker.Entries()
                     .Where(entry => entry.State is
                         EntityState.Added or
                         EntityState.Modified or
                         EntityState.Deleted))
        {
            Guid rowTenantId = entry.Entity switch
            {
                OutboxMessageRow row => row.TenantId,
                EventLogRow row => row.TenantId,
                OutboxSequenceRow row => row.TenantId,
                _ => tenantId,
            };
            if (rowTenantId != tenantId)
            {
                throw new InvalidOperationException(
                    "Outbox rows can only be changed inside their tenant scope.");
            }
        }
    }
}
