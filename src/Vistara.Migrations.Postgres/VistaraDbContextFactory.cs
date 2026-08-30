using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vistara.Persistence;

namespace Vistara.Migrations.Postgres;

public sealed class VistaraDbContextFactory : IDesignTimeDbContextFactory<VistaraDbContext>
{
    private static readonly Guid DesignTimeTenantId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000001");

    public VistaraDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VistaraDbContext>();
        optionsBuilder.UseNpgsql(
            postgres => postgres.UseVistaraMigrations());
        return new VistaraDbContext(
            optionsBuilder.Options,
            new FixedTenantScope(DesignTimeTenantId));
    }
}
