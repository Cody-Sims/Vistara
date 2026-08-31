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
            MigrationTestSupport.SqlitePlatformBootstrapMigration);
        AssertRollbackDropsEveryTable(
            postgres,
            MigrationTestSupport.PostgresPlatformBootstrapMigration);
    }

    [Fact]
    public void Canonical_model_contains_the_current_sharing_persistence_schema()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = MigrationTestSupport.CreateSqliteContext(connection);
        IModel model = MigrationTestSupport.GetDesignModel(context);
        IRelationalModel relationalModel = model.GetRelationalModel();

        ITable shares = relationalModel.Tables.Single(
            table => table.Name == "sharing_shares");
        Assert.Equal(
            ["id"],
            shares.PrimaryKey!.Columns.Select(column => column.Name));
        Assert.Contains(
            shares.UniqueConstraints,
            constraint => constraint.Columns
                .Select(column => column.Name)
                .SequenceEqual(["tenant_id", "id"]));
        Assert.Contains(
            shares.Indexes,
            index =>
                index.IsUnique &&
                index.Columns.Select(column => column.Name)
                    .SequenceEqual(["pepper_version_id", "token_digest_hex"]));
        Assert.Contains(
            shares.Indexes,
            index => index.Columns.Select(column => column.Name)
                .SequenceEqual(["tenant_id", "expires_at_utc"]));
        Assert.Contains(
            shares.Indexes,
            index => index.Columns.Select(column => column.Name)
                .SequenceEqual(["tenant_id", "revoked_at_utc"]));

        ITable sessions = relationalModel.Tables.Single(
            table => table.Name == "sharing_sessions");
        Assert.Contains(
            sessions.ForeignKeyConstraints,
            foreignKey =>
                foreignKey.Columns.Select(column => column.Name)
                    .SequenceEqual(["tenant_id", "share_id"]) &&
                foreignKey.PrincipalColumns.Select(column => column.Name)
                    .SequenceEqual(["tenant_id", "id"]));
        Assert.Contains(
            sessions.Indexes,
            index => index.Columns.Select(column => column.Name)
                .SequenceEqual(["tenant_id", "expires_at_utc"]));

        Assert.Contains(
            relationalModel.Tables,
            table => table.Name == "sharing_idempotency");
        Assert.Contains(
            relationalModel.Tables,
            table => table.Name == "sharing_rate_limits");
        Assert.True(IsConcurrencyToken(model, "sharing_shares", "version"));
        Assert.True(IsConcurrencyToken(model, "sharing_rate_limits", "version"));
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

    private static bool IsConcurrencyToken(
        IModel model,
        string tableName,
        string columnName) =>
        model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Any(property =>
                property.IsConcurrencyToken &&
                property.GetColumnName(
                    StoreObjectIdentifier.Table(tableName, schema: null)) ==
                columnName);
}
