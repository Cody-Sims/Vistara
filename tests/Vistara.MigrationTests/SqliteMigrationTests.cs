using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Vistara.Persistence.Sharing;
using Xunit;

namespace Vistara.MigrationTests;

public sealed class SqliteMigrationTests
{
    [Fact]
    public async Task Empty_database_migration_matches_the_relational_model()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context =
            MigrationTestSupport.CreateSqliteContext(connection);

        await context.Database.MigrateAsync();

        SqliteSchema actual = await SqliteSchema.ReadAsync(connection);
        IRelationalModel expected =
            MigrationTestSupport.GetDesignModel(context).GetRelationalModel();

        Assert.Equal(
            expected.Tables.Select(table => table.Name).Order(StringComparer.Ordinal),
            actual.Tables.Keys.Order(StringComparer.Ordinal));

        foreach (ITable table in expected.Tables)
        {
            AssertTableMatchesModel(table, actual.Tables[table.Name]);
        }

        Assert.Equal(
            MigrationTestSupport.SqliteMigrations,
            await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Sqlite_enforces_check_unique_and_foreign_key_constraints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context =
            MigrationTestSupport.CreateSqliteContext(connection);
        await context.Database.MigrateAsync();

        Assert.Equal(1L, await ExecuteScalarAsync(connection, "PRAGMA foreign_keys;"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            TenantInsert(
                "01991a54-6c00-7000-8000-000000000001",
                "01991a54-6c00-7000-8000-000000000002",
                "invalid")));

        await ExecuteAsync(
            connection,
            TenantInsert(
                "01991a54-6c00-7000-8000-000000000001",
                "01991a54-6c00-7000-8000-000000000001",
                "tenant"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            TenantInsert(
                "01991a54-6c00-7000-8000-000000000003",
                "01991a54-6c00-7000-8000-000000000003",
                "tenant")));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO blobs (
                id, tenant_id, provider, container, object_key, sha256,
                size_bytes, content_type, state, created_at_utc)
            VALUES (
                '01991a54-6c00-7000-8000-000000000010',
                '01991a54-6c00-7000-8000-000000000099',
                'local', 'originals', 'missing', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                1, 'image/jpeg', 'Active', '2026-08-29T00:00:00Z');
            """));
    }

    [Fact]
    public async Task Initial_migration_can_roll_back_and_reapply_repeatably()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context =
            MigrationTestSupport.CreateSqliteContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        SqliteSchema first = await SqliteSchema.ReadAsync(connection);

        await migrator.MigrateAsync(Migration.InitialDatabase);
        Assert.Empty((await SqliteSchema.ReadAsync(connection)).Tables);

        await migrator.MigrateAsync();
        SqliteSchema second = await SqliteSchema.ReadAsync(connection);

        Assert.Equal(first.Describe(), second.Describe());
    }

    [Fact]
    public async Task Initial_baseline_upgrades_to_latest_and_backfills_existing_tenants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context =
            MigrationTestSupport.CreateSqliteContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationTestSupport.InitialMigration);
        await ExecuteAsync(
            connection,
            TenantInsert(
                "01991a54-6c00-7000-8000-000000000001",
                "01991a54-6c00-7000-8000-000000000001",
                "existing"));
        await ExecuteAsync(
            connection,
            TenantInsert(
                "01991a54-6c00-7000-8000-000000000002",
                "01991a54-6c00-7000-8000-000000000002",
                "suspended",
                status: "Suspended",
                version: 7,
                updatedAtUtc: "2026-08-29T01:00:00Z"));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO quota_reservations (
                id, tenant_id, reserved_bytes, reserved_objects,
                reserved_compute_units, state, expires_at_utc)
            VALUES (
                '01991a54-6c00-7000-8000-000000000020',
                '01991a54-6c00-7000-8000-000000000001',
                100, 1, 2, 'Reserved', '2026-08-30T00:00:00Z');
            """);
        Assert.False(await TableExistsAsync(connection, "quota_usage"));

        await migrator.MigrateAsync();

        Assert.Equal(
            MigrationTestSupport.SqliteMigrations,
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                connection,
                "SELECT COUNT(*) FROM tenants WHERE slug = 'existing';"));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM worker_tenant_catalog
                WHERE routed_tenant_id =
                          '01991a54-6c00-7000-8000-000000000001'
                  AND worker_enabled = 1
                  AND version = 1
                  AND updated_at_utc = '2026-08-29T00:00:00Z';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM worker_tenant_catalog
                WHERE routed_tenant_id =
                          '01991a54-6c00-7000-8000-000000000002'
                  AND worker_enabled = 0
                  AND version = 7
                  AND updated_at_utc = '2026-08-29T01:00:00Z';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM quota_usage
                WHERE tenant_id = '01991a54-6c00-7000-8000-000000000001'
                  AND committed_uploads = 0
                  AND committed_bytes = 0
                  AND committed_objects = 0
                  AND committed_compute_units = 0
                  AND committed_jobs = 0
                  AND committed_budget_units = 0
                  AND reserved_uploads = 0
                  AND reserved_bytes = 100
                  AND reserved_objects = 1
                  AND reserved_compute_units = 2
                  AND reserved_jobs = 0
                  AND reserved_budget_units = 0
                  AND version = 2;
                """));

        SqliteSchema actual = await SqliteSchema.ReadAsync(connection);
        IRelationalModel expected =
            MigrationTestSupport.GetDesignModel(context).GetRelationalModel();
        Assert.Equal(
            expected.Tables.Select(table => table.Name).Order(StringComparer.Ordinal),
            actual.Tables.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Prior_latest_with_data_upgrades_hydrates_and_rolls_back_sharing_schema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context =
            MigrationTestSupport.CreateSqliteContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(
            MigrationTestSupport.SqliteWorkerTenantCatalogMigration);
        await ExecuteAsync(
            connection,
            TenantInsert(
                "01991a54-6c00-7000-8000-000000000001",
                "01991a54-6c00-7000-8000-000000000001",
                "existing"));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO shares (
                id, tenant_id, created_by_user_id, token_hash, target_kind,
                snapshot_json, permissions, created_at_utc, version)
            VALUES (
                '01991a54-6c00-7000-8000-000000000010',
                '01991a54-6c00-7000-8000-000000000001',
                '01991a54-6c00-7000-8000-000000000011',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'Snapshot', '[]', 1, '2026-08-30T00:00:00Z', 1);
            """);

        await migrator.MigrateAsync();
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                connection,
                "SELECT COUNT(*) FROM shares;"));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO sharing_shares (
                id, tenant_id, created_by_actor_id, name, target_type,
                album_id, assets_json, permissions, metadata_exposure,
                pepper_version_id, token_digest_hex, password_hash,
                created_at_utc, expires_at_utc, revoked_at_utc,
                revoked_by_actor_id, version, request_hash)
            VALUES (
                '01991A54-6C00-7000-8000-000000000020',
                '01991A54-6C00-7000-8000-000000000001',
                '01991A54-6C00-7000-8000-000000000021',
                'Migrated share', 'Snapshot', NULL,
                '[{"assetId":"01991a54-6c00-7000-8000-000000000022","revisionId":"01991a54-6c00-7000-8000-000000000023","revisionNumber":7,"title":"Captured title","description":null,"takenAtUtc":null,"width":640,"height":480,"renditions":[]}]',
                1, 'None', 'v1',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'pbkdf2-sha512$v1$10000$salt$hash',
                '2026-08-30T01:00:00Z', '2026-09-30T01:00:00Z',
                NULL, NULL, 1,
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc');
            """);

        var sharingOptions = new DbContextOptionsBuilder<SharingDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var sharing = new SharingDbContext(sharingOptions))
        {
            var store = new RelationalShareStore(sharing);
            var hydrated = await store.FindAsync(
                Guid.Parse("01991a54-6c00-7000-8000-000000000001"),
                Guid.Parse("01991a54-6c00-7000-8000-000000000020"),
                CancellationToken.None);

            Assert.NotNull(hydrated);
            Assert.Equal("Migrated share", hydrated.Name);
            Assert.Equal(7, Assert.Single(hydrated.Assets).RevisionNumber);
            Assert.NotNull(hydrated.PasswordHash);
        }

        await migrator.MigrateAsync(
            MigrationTestSupport.SqliteWorkerTenantCatalogMigration);
        Assert.False(await TableExistsAsync(connection, "sharing_shares"));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                connection,
                "SELECT COUNT(*) FROM shares;"));

        await migrator.MigrateAsync();
        Assert.True(await TableExistsAsync(connection, "sharing_shares"));
    }

    private static void AssertTableMatchesModel(
        ITable expected,
        SqliteTable actual)
    {
        Assert.Equal(
            expected.Columns
                .Select(column => $"{column.Name}:{column.IsNullable}")
                .Order(StringComparer.Ordinal),
            actual.Columns
                .Select(column => $"{column.Name}:{column.IsNullable}")
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            expected.PrimaryKey?.Columns.Select(column => column.Name) ?? [],
            actual.PrimaryKeyColumns);

        string[] expectedUniqueConstraints = expected.UniqueConstraints
            .Where(constraint => constraint != expected.PrimaryKey)
            .Select(constraint => Join(constraint.Columns.Select(column => column.Name)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedUniqueConstraints, actual.UniqueConstraints);

        string[] expectedIndexes = expected.Indexes
            .Select(index =>
                $"{index.Name}:{index.IsUnique}:{Join(index.Columns.Select(column => column.Name))}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedIndexes, actual.Indexes);

        string[] expectedForeignKeys = expected.ForeignKeyConstraints
            .Select(foreignKey =>
                $"{Join(foreignKey.Columns.Select(column => column.Name))}" +
                $"->{foreignKey.PrincipalTable.Name}(" +
                $"{Join(foreignKey.PrincipalColumns.Select(column => column.Name))})" +
                $":{foreignKey.OnDeleteAction}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedForeignKeys, actual.ForeignKeys);

        foreach (ICheckConstraint check in expected.CheckConstraints)
        {
            Assert.Contains(
                $"CONSTRAINT \"{check.Name}\" CHECK",
                actual.CreateSql,
                StringComparison.Ordinal);
            Assert.Contains(
                check.Sql!,
                actual.CreateSql,
                StringComparison.Ordinal);
        }
        Assert.Equal(
            expected.CheckConstraints.Count(),
            MigrationTestSupport.Count(actual.CreateSql, " CHECK ("));
    }

    private static string TenantInsert(
        string id,
        string tenantId,
        string slug,
        string status = "Active",
        long version = 1,
        string updatedAtUtc = "2026-08-29T00:00:00Z") =>
        $$"""
         INSERT INTO tenants (
             id, tenant_id, slug, name, status, settings_json, quotas_json,
             created_at_utc, updated_at_utc, version)
         VALUES (
             '{{id}}', '{{tenantId}}', '{{slug}}', 'Tenant', '{{status}}', '{}', '{}',
             '2026-08-29T00:00:00Z', '{{updatedAtUtc}}', {{version}});
         """;

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return (long)(await command.ExecuteScalarAsync() ?? 0L) == 1L;
    }

    private static string Join(IEnumerable<string> values) =>
        string.Join(",", values);
}

internal sealed record SqliteColumn(string Name, bool IsNullable, int PrimaryKeyOrder);

internal sealed record SqliteTable(
    string CreateSql,
    SqliteColumn[] Columns,
    string[] PrimaryKeyColumns,
    string[] UniqueConstraints,
    string[] Indexes,
    string[] ForeignKeys);

internal sealed record SqliteSchema(IReadOnlyDictionary<string, SqliteTable> Tables)
{
    internal static async Task<SqliteSchema> ReadAsync(SqliteConnection connection)
    {
        string[] tableNames = await ReadTableNamesAsync(connection);
        var tables = new Dictionary<string, SqliteTable>(StringComparer.Ordinal);
        foreach (string tableName in tableNames)
        {
            tables.Add(tableName, await ReadTableAsync(connection, tableName));
        }

        return new SqliteSchema(tables);
    }

    internal string[] Describe() =>
        Tables.Select(
                pair =>
                    $"{pair.Key}|{pair.Value.CreateSql}|" +
                    $"{string.Join(';', pair.Value.Columns)}|" +
                    $"{string.Join(';', pair.Value.PrimaryKeyColumns)}|" +
                    $"{string.Join(';', pair.Value.UniqueConstraints)}|" +
                    $"{string.Join(';', pair.Value.Indexes)}|" +
                    $"{string.Join(';', pair.Value.ForeignKeys)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static async Task<string[]> ReadTableNamesAsync(
        SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
              AND name NOT LIKE '__EFMigrations%'
            ORDER BY name;
            """;

        var names = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<SqliteTable> ReadTableAsync(
        SqliteConnection connection,
        string tableName)
    {
        string quotedTable = Quote(tableName);
        string createSql = await ReadCreateSqlAsync(connection, tableName);
        SqliteColumn[] columns = await ReadColumnsAsync(connection, quotedTable);
        SqliteIndex[] indexes = await ReadIndexesAsync(connection, quotedTable);
        string[] foreignKeys = await ReadForeignKeysAsync(connection, quotedTable);

        return new SqliteTable(
            createSql,
            columns,
            columns.Where(column => column.PrimaryKeyOrder > 0)
                .OrderBy(column => column.PrimaryKeyOrder)
                .Select(column => column.Name)
                .ToArray(),
            indexes.Where(index => index.Origin == "u")
                .Select(index => string.Join(",", index.Columns))
                .Order(StringComparer.Ordinal)
                .ToArray(),
            indexes.Where(index => index.Origin == "c")
                .Select(index =>
                    $"{index.Name}:{index.IsUnique}:{string.Join(',', index.Columns)}")
                .Order(StringComparer.Ordinal)
                .ToArray(),
            foreignKeys);
    }

    private static async Task<string> ReadCreateSqlAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }

    private static async Task<SqliteColumn[]> ReadColumnsAsync(
        SqliteConnection connection,
        string quotedTable)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({quotedTable});";

        var columns = new List<SqliteColumn>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new SqliteColumn(
                reader.GetString(1),
                reader.GetInt64(3) == 0,
                reader.GetInt32(5)));
        }

        return columns.ToArray();
    }

    private static async Task<SqliteIndex[]> ReadIndexesAsync(
        SqliteConnection connection,
        string quotedTable)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({quotedTable});";

        var indexes = new List<(string Name, bool IsUnique, string Origin)>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexes.Add((
                    reader.GetString(1),
                    reader.GetInt64(2) == 1,
                    reader.GetString(3)));
            }
        }

        var result = new List<SqliteIndex>();
        foreach ((string name, bool unique, string origin) in indexes)
        {
            await using SqliteCommand detail = connection.CreateCommand();
            detail.CommandText = $"PRAGMA index_info({Quote(name)});";
            var columns = new List<(int Order, string Name)>();
            await using SqliteDataReader reader = await detail.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetInt32(0), reader.GetString(2)));
            }

            result.Add(new SqliteIndex(
                name,
                unique,
                origin,
                columns.OrderBy(column => column.Order)
                    .Select(column => column.Name)
                    .ToArray()));
        }

        return result.ToArray();
    }

    private static async Task<string[]> ReadForeignKeysAsync(
        SqliteConnection connection,
        string quotedTable)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({quotedTable});";

        var rows = new List<SqliteForeignKeyRow>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SqliteForeignKeyRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(6)));
        }

        return rows.GroupBy(row => row.Id)
            .Select(group =>
            {
                SqliteForeignKeyRow first = group.First();
                string dependent = string.Join(
                    ",",
                    group.OrderBy(row => row.Sequence).Select(row => row.From));
                string principal = string.Join(
                    ",",
                    group.OrderBy(row => row.Sequence).Select(row => row.To));
                return $"{dependent}->{first.PrincipalTable}({principal}):" +
                       NormalizeDeleteAction(first.OnDelete);
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeDeleteAction(string action) =>
        action.ToUpperInvariant() switch
        {
            "CASCADE" => "Cascade",
            "RESTRICT" => "Restrict",
            "SET NULL" => "SetNull",
            "SET DEFAULT" => "SetDefault",
            _ => "NoAction",
        };

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record SqliteIndex(
        string Name,
        bool IsUnique,
        string Origin,
        string[] Columns);

    private sealed record SqliteForeignKeyRow(
        int Id,
        int Sequence,
        string PrincipalTable,
        string From,
        string To,
        string OnDelete);
}
