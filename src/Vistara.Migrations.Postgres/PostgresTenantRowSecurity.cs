using Microsoft.EntityFrameworkCore.Migrations;

namespace Vistara.Migrations.Postgres;

public static class PostgresTenantRowSecurity
{
    public const string TenantSettingName = "vistara.tenant_id";
    public const string MigrationSettingName = "vistara.migration_id";
    public const string IdentityDirectorySettingName = "vistara.identity_directory";

    /// <summary>
    /// Tables that an authenticated principal may read across tenants strictly
    /// to resolve its own memberships during sign-in and profile reads.
    /// </summary>
    public static IReadOnlyList<string> IdentityDirectoryTables { get; } =
        Array.AsReadOnly<string>(
        [
            "tenant_memberships",
            "tenants",
        ]);

    private static IReadOnlyList<string> InitialTenantOwnedTables { get; } =
        Array.AsReadOnly<string>(
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
            "blobs",
            "deletion_tombstones",
            "idempotency_requests",
            "purge_batch_items",
            "purge_batches",
            "quota_reservations",
            "relationship_snapshots",
            "resource_grants",
            "retention_holds",
            "share_assets",
            "share_sessions",
            "shares",
            "tags",
            "tenant_memberships",
            "tenants",
            "trash_entries",
            "upload_parts",
            "upload_sessions",
        ]);

    public static IReadOnlyList<string> TenantOwnedTables { get; } =
        Array.AsReadOnly<string>(
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
            "blobs",
            "cookie_sessions",
            "deletion_tombstones",
            "delivery_grants",
            "derivative_requests",
            "event_log",
            "idempotency_requests",
            "ingest_operations",
            "jobs",
            "outbox_messages",
            "outbox_sequences",
            "purge_batch_items",
            "purge_batches",
            "quota_reservations",
            "quota_usage",
            "relationship_snapshots",
            "resource_grants",
            "retention_holds",
            "share_assets",
            "share_sessions",
            "shares",
            "sharing_sessions",
            "sharing_shares",
            "tags",
            "tenant_memberships",
            "tenants",
            "trash_entries",
            "upload_parts",
            "upload_reconciliation_checkpoints",
            "upload_sessions",
        ]);

    public static void Enable(MigrationBuilder migrationBuilder)
    {
        Enable(migrationBuilder, InitialTenantOwnedTables);
    }

    public static void Enable(
        MigrationBuilder migrationBuilder,
        IEnumerable<string> tables)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(tables);

        foreach (string table in tables)
        {
            migrationBuilder.Sql(
                $"""
                ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;
                CREATE POLICY "tenant_isolation" ON "{table}"
                    AS PERMISSIVE
                    FOR ALL
                    TO PUBLIC
                    USING (
                        "tenant_id" = NULLIF(
                            current_setting('{TenantSettingName}', true),
                            '')::uuid)
                    WITH CHECK (
                        "tenant_id" = NULLIF(
                            current_setting('{TenantSettingName}', true),
                            '')::uuid);
                """);
        }
    }

    /// <summary>
    /// Adds a permissive, read-only policy that is active only while the
    /// identity-directory setting is set inside the current transaction. It
    /// lets one indexed query resolve a principal's memberships without
    /// probing every tenant, while all other access stays tenant isolated.
    /// </summary>
    public static void EnableIdentityDirectory(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        foreach (string table in IdentityDirectoryTables)
        {
            ValidateIdentifier(table);
            migrationBuilder.Sql(
                $"""
                CREATE POLICY "identity_directory" ON "{table}"
                    AS PERMISSIVE
                    FOR SELECT
                    TO PUBLIC
                    USING (
                        current_setting(
                            '{IdentityDirectorySettingName}',
                            true) = 'on');
                """);
        }
    }

    public static void DisableIdentityDirectory(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        foreach (string table in IdentityDirectoryTables)
        {
            ValidateIdentifier(table);
            migrationBuilder.Sql(
                $"""DROP POLICY IF EXISTS "identity_directory" ON "{table}";""");
        }
    }

    public static void BeginDataMigration(
        MigrationBuilder migrationBuilder,
        string migrationId,
        IEnumerable<string> tables)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        ArgumentNullException.ThrowIfNull(tables);
        ValidateIdentifier(migrationId);
        string policyName = $"vistara_migration_{migrationId}";

        migrationBuilder.Sql(
            $"""
            SET SESSION row_security = on;
            SET SESSION {MigrationSettingName} = '{migrationId}';
            """);
        foreach (string table in tables)
        {
            ValidateIdentifier(table);
            migrationBuilder.Sql(
                $"""
                CREATE POLICY "{policyName}" ON "{table}"
                    AS PERMISSIVE
                    FOR ALL
                    TO CURRENT_USER
                    USING (
                        current_setting(
                            '{MigrationSettingName}',
                            true) = '{migrationId}')
                    WITH CHECK (
                        current_setting(
                            '{MigrationSettingName}',
                            true) = '{migrationId}');
                """);
        }
    }

    public static void EndDataMigration(
        MigrationBuilder migrationBuilder,
        string migrationId,
        IEnumerable<string> tables)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        ArgumentNullException.ThrowIfNull(tables);
        ValidateIdentifier(migrationId);
        string policyName = $"vistara_migration_{migrationId}";

        migrationBuilder.Sql(
            $"SET SESSION {MigrationSettingName} = '';");

        foreach (string table in tables)
        {
            ValidateIdentifier(table);
            migrationBuilder.Sql(
                $"""DROP POLICY "{policyName}" ON "{table}";""");
        }
    }

    private static void ValidateIdentifier(string value)
    {
        if (value.Length > 48 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character != '_'))
        {
            throw new ArgumentException(
                "PostgreSQL migration identifiers must be short ASCII identifiers.",
                nameof(value));
        }
    }
}
