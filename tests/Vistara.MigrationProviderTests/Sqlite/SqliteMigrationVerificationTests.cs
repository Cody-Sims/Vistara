using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Vistara.Migrations.Sqlite;
using Vistara.Persistence;
using Xunit;

namespace Vistara.MigrationProviderTests.Sqlite;

public sealed class SqliteMigrationVerificationTests
{
    private const string InitialMigration = "20260829000000_InitialCreate";
    private const string UploadIngestMigration =
        "20260829034644_AddUploadIngestPersistence";
    private const string RuntimeReconciliationMigration =
        "20260829123724_CompleteRuntimeAndUploadReconciliation";
    private const string LegacyDataMigration =
        "20260829130302_NormalizeLegacyRuntimeData";
    private const string LegacyUploadQuotaMigration =
        "20260829183036_ReconcileLegacyUploadJobQuota";
    private const string WorkerTenantCatalogMigration =
        "20260830044737_AddWorkerTenantCatalog";
    private const string SharingPersistenceMigration =
        "20260830101756_AddSharingPersistence";
    private const string LocalCredentialsMigration =
        "20260830233106_AddLocalCredentials";
    private const string PlatformBootstrapMigration =
        "20260831000511_AddPlatformBootstrap";
    private const string UserPreferencesMigration =
        "20260831002835_AddUserPreferences";
    private const string OidcLoginRequestsMigration =
        "20260901000000_AddOidcLoginRequests";

    private static readonly string[] ExpectedMigrations =
    [
        InitialMigration,
        UploadIngestMigration,
        RuntimeReconciliationMigration,
        LegacyDataMigration,
        LegacyUploadQuotaMigration,
        WorkerTenantCatalogMigration,
        SharingPersistenceMigration,
        LocalCredentialsMigration,
        PlatformBootstrapMigration,
        UserPreferencesMigration,
        OidcLoginRequestsMigration,
    ];

    private static readonly string[] ExpectedTables =
    [
        "album_items",
        "albums",
        "api_keys",
        "asset_favorites",
        "asset_lifecycles",
        "asset_metadata_history",
        "asset_revisions",
        "asset_tags",
        "assets",
        "audit_events",
        "auth_sessions",
        "authentication_routes",
        "blobs",
        "cookie_sessions",
        "deletion_tombstones",
        "delivery_grants",
        "derivative_requests",
        "event_log",
        "external_identities",
        "idempotency_requests",
        "ingest_operations",
        "jobs",
        "local_credentials",
        "local_identities",
        "oidc_login_requests",
        "outbox_messages",
        "outbox_sequences",
        "platform_bootstrap",
        "public_derivative_routes",
        "purge_batch_items",
        "purge_batches",
        "quota_reservations",
        "quota_usage",
        "rate_limit_windows",
        "relationship_snapshots",
        "resource_grants",
        "retention_holds",
        "revoked_tokens",
        "share_assets",
        "share_sessions",
        "shares",
        "sharing_idempotency",
        "sharing_rate_limits",
        "sharing_sessions",
        "sharing_shares",
        "tags",
        "tenant_memberships",
        "tenants",
        "trash_entries",
        "upload_parts",
        "upload_reconciliation_checkpoints",
        "upload_sessions",
        "user_preferences",
        "users",
        "worker_tenant_catalog",
    ];

    [Fact]
    public async Task Initial_migration_applies_to_an_empty_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using VistaraDbContext context = CreateContext(connection);

        await context.Database.MigrateAsync();

        Assert.Equal(ExpectedTables, await ReadApplicationTablesAsync(connection));
        Assert.Equal(
            ExpectedMigrations,
            await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public void Migration_history_matches_the_model_and_generates_portable_sqlite_sql()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using VistaraDbContext context = CreateContext(connection);

        Assert.Equal(ExpectedMigrations, context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());

        string script = context.GetService<IMigrator>().GenerateScript();
        IRelationalModel model =
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        foreach (ITable table in model.Tables)
        {
            Assert.Contains(
                $"CREATE TABLE \"{table.Name}\"",
                script,
                StringComparison.Ordinal);
            foreach (ITableIndex index in table.Indexes)
            {
                Assert.Contains(index.Name, script, StringComparison.Ordinal);
            }

            foreach (IForeignKeyConstraint foreignKey in table.ForeignKeyConstraints)
            {
                Assert.Contains(foreignKey.Name, script, StringComparison.Ordinal);
            }

            foreach (ICheckConstraint check in table.CheckConstraints)
            {
                Assert.Contains(check.Name!, script, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain("ROW LEVEL SECURITY", script, StringComparison.Ordinal);
        Assert.Contains(InitialMigration, script, StringComparison.Ordinal);
        Assert.Contains(UploadIngestMigration, script, StringComparison.Ordinal);
        Assert.Contains(
            RuntimeReconciliationMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(LegacyDataMigration, script, StringComparison.Ordinal);
        Assert.Contains(
            LegacyUploadQuotaMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            WorkerTenantCatalogMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            SharingPersistenceMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            LocalCredentialsMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            PlatformBootstrapMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            UserPreferencesMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            OidcLoginRequestsMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TEMP TABLE legacy_upload_job_decisions",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "reserved_jobs = 5",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "json_valid(tenant.quotas_json)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initial_migration_rolls_back_and_reapplies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using VistaraDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();

        string rollback = migrator.GenerateScript(
            OidcLoginRequestsMigration,
            Migration.InitialDatabase);
        foreach (string table in ExpectedTables)
        {
            Assert.Matches(
                $@"DROP TABLE ""?{Regex.Escape(table)}""?;",
                rollback);
        }

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(Migration.InitialDatabase);
        Assert.Empty(await ReadApplicationTablesAsync(connection));
        await migrator.MigrateAsync();
        Assert.Equal(ExpectedTables, await ReadApplicationTablesAsync(connection));
    }

    /// <summary>
    /// The OIDC login request migration must roll back on its own, so an
    /// operator can revert the hosted sign-in slice without unwinding the whole
    /// identity schema underneath it.
    /// </summary>
    [Fact]
    public async Task Oidc_login_request_migration_rolls_back_independently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using VistaraDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        Assert.Contains(
            "oidc_login_requests",
            await ReadApplicationTablesAsync(connection));

        await migrator.MigrateAsync(UserPreferencesMigration);
        string[] rolledBack = await ReadApplicationTablesAsync(connection);
        Assert.DoesNotContain("oidc_login_requests", rolledBack);
        Assert.Contains("user_preferences", rolledBack);

        await migrator.MigrateAsync();
        Assert.Equal(ExpectedTables, await ReadApplicationTablesAsync(connection));
    }

    /// <summary>
    /// The table is written before any tenant scope exists, so it must carry no
    /// tenant column and no tenant-scoped index that would imply one.
    /// </summary>
    [Fact]
    public async Task Oidc_login_requests_has_the_specified_columns_and_no_tenant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using VistaraDbContext context = CreateContext(connection);
        await context.Database.MigrateAsync();

        (string Name, string Type, bool NotNull, bool PrimaryKey)[] columns =
            await ReadColumnsAsync(connection, "oidc_login_requests");

        Assert.Equal(
            [
                ("code_verifier", "TEXT", true, false),
                ("consumed_at_utc", "TEXT", false, false),
                ("created_at_utc", "TEXT", true, false),
                ("expires_at_utc", "TEXT", true, false),
                ("handle_digest", "BLOB", true, false),
                ("nonce_digest", "BLOB", true, false),
                ("provider_id", "TEXT", true, false),
                ("redirect_uri", "TEXT", true, false),
                ("return_to", "TEXT", true, false),
                ("state_digest", "BLOB", true, true),
            ],
            columns);
        Assert.Equal(
            ["ix_oidc_login_requests_expires_at_utc"],
            await ReadIndexesAsync(connection, "oidc_login_requests"));
    }

    private static VistaraDbContext CreateContext(DbConnection connection)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VistaraDbContext>();
        optionsBuilder.UseSqlite(
            connection,
            sqlite => sqlite.UseVistaraMigrations());
        return new VistaraDbContext(
            optionsBuilder.Options,
            new FixedTenantScope(
                Guid.Parse("01991a54-6c00-7000-8000-000000000001")));
    }

    private static async Task<string[]> ReadApplicationTablesAsync(
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

        var tables = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private static async Task<(string Name, string Type, bool NotNull, bool PrimaryKey)[]>
        ReadColumnsAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name, type, "notnull", pk
            FROM pragma_table_info($table)
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("$table", table);

        var columns = new List<(string, string, bool, bool)>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2) == 1,
                reader.GetInt32(3) == 1));
        }

        return columns.ToArray();
    }

    private static async Task<string[]> ReadIndexesAsync(
        SqliteConnection connection,
        string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index' AND tbl_name = $table AND sql IS NOT NULL
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("$table", table);

        var indexes = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes.ToArray();
    }

    private static int Count(string value, string token)
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
}
