using Microsoft.EntityFrameworkCore;

namespace Vistara.Persistence;

public sealed class TenantDbContextFactory(VistaraPersistenceOptions options)
{
    private readonly VistaraPersistenceOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public VistaraDbContext Create(Guid tenantId)
    {
        var builder = new DbContextOptionsBuilder<VistaraDbContext>();
        switch (_options.Provider)
        {
            case VistaraDatabaseProvider.Sqlite:
                builder.UseSqlite(_options.ConnectionString);
                break;
            case VistaraDatabaseProvider.PostgreSql:
                builder.UseNpgsql(_options.ConnectionString);
                break;
            default:
                throw new InvalidOperationException(
                    "The configured persistence provider is not supported.");
        }

        return new VistaraDbContext(
            builder.Options,
            new FixedTenantScope(tenantId));
    }
}
