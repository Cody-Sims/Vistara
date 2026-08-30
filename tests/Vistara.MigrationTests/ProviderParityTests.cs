using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Vistara.MigrationTests;

public sealed class ProviderParityTests
{
    [Fact]
    public void Providers_have_equal_logical_relational_coverage()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var sqlite = MigrationTestSupport.CreateSqliteContext(connection);
        using var postgres = MigrationTestSupport.CreatePostgresContext();

        string[] sqliteSchema = MigrationTestSupport.DescribeLogicalSchema(
            MigrationTestSupport.GetDesignModel(sqlite));
        string[] postgresSchema = MigrationTestSupport.DescribeLogicalSchema(
            MigrationTestSupport.GetDesignModel(postgres));

        string[] differences = sqliteSchema
            .Zip(postgresSchema)
            .Where(pair => !string.Equals(
                pair.First,
                pair.Second,
                StringComparison.Ordinal))
            .Select(pair => $"SQLite: {pair.First}{Environment.NewLine}PostgreSQL: {pair.Second}")
            .ToArray();

        Assert.True(
            differences.Length == 0 && sqliteSchema.Length == postgresSchema.Length,
            string.Join(Environment.NewLine, differences));
    }

    [Fact]
    public void Provider_specific_storage_types_are_explicitly_allowed()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var sqlite = MigrationTestSupport.CreateSqliteContext(connection);
        using var postgres = MigrationTestSupport.CreatePostgresContext();

        string[] sqliteTypes = MigrationTestSupport.DescribeStoreTypes(
            MigrationTestSupport.GetDesignModel(sqlite));
        string[] postgresTypes = MigrationTestSupport.DescribeStoreTypes(
            MigrationTestSupport.GetDesignModel(postgres));

        Assert.NotEqual(sqliteTypes, postgresTypes);
        Assert.All(
            sqliteTypes,
            value => Assert.Matches(@"^.+:(TEXT|INTEGER|text)$", value));
        Assert.Contains(postgresTypes, value => value.EndsWith(":uuid", StringComparison.Ordinal));
        Assert.Contains(
            postgresTypes,
            value => value.EndsWith(
                ":timestamp with time zone",
                StringComparison.Ordinal));
        Assert.Contains(postgresTypes, value => value.EndsWith(":boolean", StringComparison.Ordinal));
        Assert.Contains(postgresTypes, value => value.EndsWith(":bigint", StringComparison.Ordinal));
    }

    [Fact]
    public void Postgres_only_truncates_identifiers_that_exceed_its_limit()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var sqlite = MigrationTestSupport.CreateSqliteContext(connection);
        using var postgres = MigrationTestSupport.CreatePostgresContext();

        IRelationalModel sqliteModel =
            MigrationTestSupport.GetDesignModel(sqlite).GetRelationalModel();
        IRelationalModel postgresModel =
            MigrationTestSupport.GetDesignModel(postgres).GetRelationalModel();

        foreach (ITable sqliteTable in sqliteModel.Tables)
        {
            ITable postgresTable = postgresModel.Tables.Single(
                table => table.Name == sqliteTable.Name);

            AssertProviderNames(
                sqliteTable.Indexes.Select(index => (
                    Signature: $"{index.IsUnique}:" +
                               $"{Join(index.Columns.Select(column => column.Name))}",
                    index.Name)),
                postgresTable.Indexes.Select(index => (
                    Signature: $"{index.IsUnique}:" +
                               $"{Join(index.Columns.Select(column => column.Name))}",
                    index.Name)));
            AssertProviderNames(
                sqliteTable.ForeignKeyConstraints.Select(foreignKey => (
                    Signature:
                    $"{Join(foreignKey.Columns.Select(column => column.Name))}" +
                    $"->{foreignKey.PrincipalTable.Name}(" +
                    $"{Join(foreignKey.PrincipalColumns.Select(column => column.Name))})",
                    foreignKey.Name)),
                postgresTable.ForeignKeyConstraints.Select(foreignKey => (
                    Signature:
                    $"{Join(foreignKey.Columns.Select(column => column.Name))}" +
                    $"->{foreignKey.PrincipalTable.Name}(" +
                    $"{Join(foreignKey.PrincipalColumns.Select(column => column.Name))})",
                    foreignKey.Name)));
        }
    }

    private static void AssertProviderNames(
        IEnumerable<(string Signature, string Name)> sqliteObjects,
        IEnumerable<(string Signature, string Name)> postgresObjects)
    {
        Dictionary<string, string> postgresNames =
            postgresObjects.ToDictionary(pair => pair.Signature, pair => pair.Name);

        foreach ((string signature, string sqliteName) in sqliteObjects)
        {
            string postgresName = postgresNames[signature];
            if (sqliteName == postgresName)
            {
                continue;
            }

            Assert.True(sqliteName.Length > 63);
            Assert.Equal(63, postgresName.Length);
            Assert.EndsWith("~", postgresName, StringComparison.Ordinal);
            Assert.StartsWith(
                postgresName[..^1],
                sqliteName,
                StringComparison.Ordinal);
        }
    }

    private static string Join(IEnumerable<string> values) =>
        string.Join(",", values);
}
