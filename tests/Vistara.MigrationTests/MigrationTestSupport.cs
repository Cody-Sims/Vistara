using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Vistara.Migrations.Postgres;
using Vistara.Migrations.Sqlite;
using Vistara.Persistence;

namespace Vistara.MigrationTests;

internal static class MigrationTestSupport
{
    internal const string InitialMigration = "20260829000000_InitialCreate";
    internal const string SqliteUploadIngestMigration =
        "20260829034644_AddUploadIngestPersistence";
    internal const string PostgresUploadIngestMigration =
        "20260829034648_AddUploadIngestPersistence";
    internal const string SqliteRuntimeReconciliationMigration =
        "20260829123724_CompleteRuntimeAndUploadReconciliation";
    internal const string PostgresRuntimeReconciliationMigration =
        "20260829123733_CompleteRuntimeAndUploadReconciliation";
    internal const string SqliteLegacyDataMigration =
        "20260829130302_NormalizeLegacyRuntimeData";
    internal const string PostgresLegacyDataMigration =
        "20260829130313_NormalizeLegacyRuntimeData";

    internal static readonly string[] SqliteMigrations =
    [
        InitialMigration,
        SqliteUploadIngestMigration,
        SqliteRuntimeReconciliationMigration,
        SqliteLegacyDataMigration,
    ];

    internal static readonly string[] PostgresMigrations =
    [
        InitialMigration,
        PostgresUploadIngestMigration,
        PostgresRuntimeReconciliationMigration,
        PostgresLegacyDataMigration,
    ];

    private static readonly Guid TenantId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000001");

    internal static VistaraDbContext CreateSqliteContext(DbConnection connection)
        => CreateSqliteContext(connection, TenantId);

    internal static VistaraDbContext CreateSqliteContext(
        DbConnection connection,
        Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>();
        options.UseSqlite(
            connection,
            sqlite => sqlite.UseVistaraMigrations());
        return CreateContext(options.Options, tenantId);
    }

    internal static VistaraDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>();
        options.UseNpgsql(
            postgres => postgres.UseVistaraMigrations());
        return CreateContext(options.Options, TenantId);
    }

    internal static IModel GetDesignModel(DbContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    internal static string[] DescribeLogicalSchema(IModel model) =>
        model.GetRelationalModel().Tables
            .Where(table => !table.IsExcludedFromMigrations)
            .Select(DescribeTable)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static string[] DescribeStoreTypes(IModel model) =>
        model.GetRelationalModel().Tables
            .SelectMany(
                table => table.Columns.Select(
                    column => $"{table.Name}.{column.Name}:{column.StoreType}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static int Count(string value, string token)
    {
        int count = 0;
        int startIndex = 0;
        while ((startIndex = value.IndexOf(
                   token,
                   startIndex,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += token.Length;
        }

        return count;
    }

    private static VistaraDbContext CreateContext(
        DbContextOptions<VistaraDbContext> options,
        Guid tenantId) =>
        new(options, new FixedTenantScope(tenantId));

    private static string DescribeTable(ITable table)
    {
        string columns = JoinSet(table.Columns.Select(
            column => $"{column.Name}:{column.IsNullable}"));
        string primaryKey = JoinOrdered(
            table.PrimaryKey?.Columns.Select(column => column.Name) ?? []);
        string uniqueConstraints = JoinSet(table.UniqueConstraints
            .Where(constraint => constraint != table.PrimaryKey)
            .Select(constraint => JoinOrdered(
                constraint.Columns.Select(column => column.Name))));
        string indexes = JoinSet(table.Indexes.Select(
            index =>
                $"{index.IsUnique}:" +
                $"{JoinOrdered(index.Columns.Select(column => column.Name))}"));
        string foreignKeys = JoinSet(table.ForeignKeyConstraints.Select(
            foreignKey =>
                $"{JoinOrdered(foreignKey.Columns.Select(column => column.Name))}" +
                $"->{foreignKey.PrincipalTable.Name}(" +
                $"{JoinOrdered(foreignKey.PrincipalColumns.Select(column => column.Name))})" +
                $":{foreignKey.OnDeleteAction}"));
        string checks = JoinSet(table.CheckConstraints.Select(check => check.Name!));

        return $"{table.Name}|C:{columns}|PK:{primaryKey}|U:{uniqueConstraints}" +
               $"|I:{indexes}|FK:{foreignKeys}|CK:{checks}";
    }

    private static string JoinSet(IEnumerable<string> values) =>
        string.Join(",", values.Order(StringComparer.Ordinal));

    private static string JoinOrdered(IEnumerable<string> values) =>
        string.Join(",", values);
}
