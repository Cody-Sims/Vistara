using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Vistara.Migrations.Sqlite;

public static class SqliteMigrationConfiguration
{
    public static SqliteDbContextOptionsBuilder UseVistaraMigrations(
        this SqliteDbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.MigrationsAssembly(typeof(SqliteMigrationConfiguration).Assembly.FullName);
        return optionsBuilder;
    }
}
