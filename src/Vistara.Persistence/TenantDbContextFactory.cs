using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Azure;

namespace Vistara.Persistence;

public sealed class TenantDbContextFactory(
    VistaraPersistenceOptions options,
    VistaraNpgsqlDataSourceProvider? dataSources = null)
{
    private readonly VistaraPersistenceOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly VistaraNpgsqlDataSourceProvider? _dataSources = dataSources;

    public VistaraDbContext Create(Guid tenantId)
    {
        var builder = new DbContextOptionsBuilder<VistaraDbContext>();
        builder.UseVistaraDatabase(
            _dataSources,
            _options.Provider,
            _options.ConnectionString);

        return new VistaraDbContext(
            builder.Options,
            new FixedTenantScope(tenantId));
    }
}
