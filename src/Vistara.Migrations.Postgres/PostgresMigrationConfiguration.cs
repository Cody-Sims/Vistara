using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Vistara.Migrations.Postgres;

public static class PostgresMigrationConfiguration
{
    public static NpgsqlDbContextOptionsBuilder UseVistaraMigrations(
        this NpgsqlDbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.MigrationsAssembly(typeof(PostgresMigrationConfiguration).Assembly.FullName);
        return optionsBuilder;
    }
}
