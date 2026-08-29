using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Vistara.MigrationTests;

public sealed class MigrationHistoryTests
{
    [Fact]
    public void Both_provider_histories_are_discovered_and_match_their_snapshots()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var sqlite = MigrationTestSupport.CreateSqliteContext(connection);
        using var postgres = MigrationTestSupport.CreatePostgresContext();

        Assert.Equal(
            MigrationTestSupport.SqliteMigrations,
            sqlite.Database.GetMigrations());
        Assert.Equal(
            MigrationTestSupport.PostgresMigrations,
            postgres.Database.GetMigrations());

        Assert.False(sqlite.Database.HasPendingModelChanges());
        Assert.False(postgres.Database.HasPendingModelChanges());

        AssertSnapshotOwnedByMigrationAssembly(sqlite);
        AssertSnapshotOwnedByMigrationAssembly(postgres);
    }

    [Fact]
    public void Initial_release_has_repeatable_provider_rollback_scripts()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var sqlite = MigrationTestSupport.CreateSqliteContext(connection);
        using var postgres = MigrationTestSupport.CreatePostgresContext();

        AssertRollbackDropsEveryTable(
            sqlite,
            MigrationTestSupport.SqliteLegacyUploadQuotaMigration);
        AssertRollbackDropsEveryTable(
            postgres,
            MigrationTestSupport.PostgresLegacyUploadQuotaMigration);
    }

    private static void AssertSnapshotOwnedByMigrationAssembly(
        DbContext context)
    {
        IMigrationsAssembly migrationsAssembly =
            context.GetService<IMigrationsAssembly>();

        Assert.NotNull(migrationsAssembly.ModelSnapshot);
        Assert.Equal(
            migrationsAssembly.Assembly,
            migrationsAssembly.ModelSnapshot.GetType().Assembly);
    }

    private static void AssertRollbackDropsEveryTable(
        DbContext context,
        string latestMigration)
    {
        string rollback = context.GetService<IMigrator>().GenerateScript(
            latestMigration,
            Migration.InitialDatabase);
        IRelationalModel model =
            MigrationTestSupport.GetDesignModel(context).GetRelationalModel();

        foreach (ITable table in model.Tables)
        {
            Assert.Matches(
                $@"DROP TABLE ""?{Regex.Escape(table.Name)}""?;",
                rollback);
        }
    }
}
