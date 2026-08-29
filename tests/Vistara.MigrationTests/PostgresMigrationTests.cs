using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Vistara.Migrations.Postgres;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.MigrationTests;

public sealed partial class PostgresMigrationTests
{
    [Fact]
    public void Idempotent_script_uses_postgres_types_and_migration_guards()
    {
        using var context = MigrationTestSupport.CreatePostgresContext();
        string script = context.GetService<IMigrator>().GenerateScript(
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains(MigrationTestSupport.InitialMigration, script, StringComparison.Ordinal);
        Assert.Contains(
            MigrationTestSupport.PostgresUploadIngestMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            MigrationTestSupport.PostgresRuntimeReconciliationMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            MigrationTestSupport.PostgresLegacyDataMigration,
            script,
            StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsHistory", script, StringComparison.Ordinal);
        Assert.Contains("DO $EF$", script, StringComparison.Ordinal);
        Assert.Contains("uuid", script, StringComparison.Ordinal);
        Assert.Contains("timestamp with time zone", script, StringComparison.Ordinal);
        Assert.Contains("boolean", script, StringComparison.Ordinal);
        Assert.Contains("bigint", script, StringComparison.Ordinal);
        Assert.Equal(
            PostgresTenantRowSecurity.TenantOwnedTables.Count,
            MigrationTestSupport.Count(script, "CREATE POLICY \"tenant_isolation\""));
        Assert.Equal(
            PostgresTenantRowSecurity.TenantOwnedTables.Count * 2,
            TenantSettingCall().Count(script));
        Assert.DoesNotContain("PRAGMA", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AUTOINCREMENT", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_tenant_table_has_a_forced_fail_closed_rls_policy()
    {
        using var context = MigrationTestSupport.CreatePostgresContext();
        IModel model = MigrationTestSupport.GetDesignModel(context);
        string script = context.GetService<IMigrator>().GenerateScript();

        string[] tenantTables = model.GetRelationalModel().Tables
            .Where(table => table.Columns.Any(column => column.Name == "tenant_id"))
            .Select(table => table.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(tenantTables, PostgresTenantRowSecurity.TenantOwnedTables);
        Assert.Equal(
            tenantTables.Length,
            MigrationTestSupport.Count(script, "ENABLE ROW LEVEL SECURITY"));
        Assert.Equal(
            tenantTables.Length,
            MigrationTestSupport.Count(script, "FORCE ROW LEVEL SECURITY"));
        Assert.Equal(
            tenantTables.Length,
            MigrationTestSupport.Count(script, "CREATE POLICY \"tenant_isolation\""));

        foreach (string table in tenantTables)
        {
            AssertTableHasFailClosedPolicy(script, table);
        }

        string[] nonTenantTables = model.GetRelationalModel().Tables
            .Where(table => table.Columns.All(column => column.Name != "tenant_id"))
            .Select(table => table.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string table in nonTenantTables)
        {
            Assert.DoesNotContain(
                $"CREATE POLICY \"tenant_isolation\" ON \"{table}\"",
                script,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Upgrade_sql_uses_scoped_migration_policies_and_safe_backfills()
    {
        using var context = MigrationTestSupport.CreatePostgresContext();
        string script = context.GetService<IMigrator>().GenerateScript(
            MigrationTestSupport.InitialMigration,
            MigrationTestSupport.PostgresLegacyDataMigration);

        Assert.Contains(
            "CREATE POLICY \"vistara_migration_20260829034648\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE POLICY \"vistara_migration_20260829130313\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("TO CURRENT_USER", script, StringComparison.Ordinal);
        Assert.Contains(
            "SET SESSION vistara.migration_id",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DROP POLICY \"vistara_migration_20260829034648\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DROP POLICY \"vistara_migration_20260829130313\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DISABLE ROW LEVEL SECURITY",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ALTER ROLE",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "BYPASSRLS",
            script,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "'upload:' ||",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "replace(upload.actor_id::text, '-', '')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "request.key",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'legacy-reservation:' ||",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "replace(reservation.id::text, '-', '')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "expires_at_utc - INTERVAL '1 second'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'s3:v1:' || upload.provider_upload_id",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'azure-block:v1:' || upload.provider_upload_id",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "UPDATE upload_sessions AS upload",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO authentication_routes",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "replace(key_row.id::text, '-', '') ||",
            script,
            StringComparison.Ordinal);
        Assert.True(
            MigrationTestSupport.Count(
                script,
                "replace(key_row.id::text, '-', '')") >= 2);

        AssertMigrationPolicySurrounds(
            script,
            "vistara_migration_20260829034648",
            "UPDATE quota_reservations AS reservation");
        AssertMigrationPolicySurrounds(
            script,
            "vistara_migration_20260829130313",
            "INSERT INTO authentication_routes");
    }

    [Fact]
    public void Opaque_catalog_tables_are_not_tenant_content_policies()
    {
        using var context = MigrationTestSupport.CreatePostgresContext();
        IRelationalModel model =
            MigrationTestSupport.GetDesignModel(context).GetRelationalModel();
        string script = context.GetService<IMigrator>().GenerateScript();

        Assert.Equal(
            [
                "created_at_utc",
                "credential_id",
                "kind",
                "lookup_digest",
                "principal_id",
                "routed_tenant_id",
            ],
            model.Tables.Single(table => table.Name == "authentication_routes")
                .Columns.Select(column => column.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "created_at_utc",
                "lookup_digest",
                "request_id",
                "routed_tenant_id",
            ],
            model.Tables.Single(table => table.Name == "public_derivative_routes")
                .Columns.Select(column => column.Name)
                .Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            "CREATE POLICY \"tenant_isolation\" ON \"authentication_routes\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CREATE POLICY \"tenant_isolation\" ON \"public_derivative_routes\"",
            script,
            StringComparison.Ordinal);
    }

    private static void AssertTableHasFailClosedPolicy(string script, string table)
    {
        int start = script.IndexOf(
            $"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY",
            StringComparison.Ordinal);
        Assert.True(start >= 0, $"RLS enable statement missing for {table}.");

        int policyStart = script.IndexOf(
            $"CREATE POLICY \"tenant_isolation\" ON \"{table}\"",
            start,
            StringComparison.Ordinal);
        Assert.True(policyStart >= 0, $"RLS policy missing for {table}.");

        int policyEnd = FindOccurrence(
            script,
            ")::uuid);",
            policyStart,
            occurrence: 1);
        Assert.True(policyEnd >= 0, $"RLS write predicate incomplete for {table}.");
        string policy = script[
            start..(policyEnd + ")::uuid);".Length)];

        Assert.Contains(
            $"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            $"CREATE POLICY \"tenant_isolation\" ON \"{table}\"",
            policy,
            StringComparison.Ordinal);
        Assert.Contains("AS PERMISSIVE", policy, StringComparison.Ordinal);
        Assert.Contains("FOR ALL", policy, StringComparison.Ordinal);
        Assert.Contains("TO PUBLIC", policy, StringComparison.Ordinal);
        Assert.Contains("USING (", policy, StringComparison.Ordinal);
        Assert.Contains("WITH CHECK (", policy, StringComparison.Ordinal);
        Assert.Equal(2, TenantSettingCall().Count(policy));
        Assert.Equal(2, FailClosedTenantPredicate().Count(policy));
        Assert.DoesNotContain("COALESCE", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" OR ", policy, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindOccurrence(
        string value,
        string token,
        int startIndex,
        int occurrence)
    {
        int index = startIndex;
        for (int count = 0; count < occurrence; count++)
        {
            index = value.IndexOf(token, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            index += token.Length;
        }

        return index - token.Length;
    }

    private static void AssertMigrationPolicySurrounds(
        string script,
        string policyName,
        string backfill)
    {
        int created = script.IndexOf(
            $"CREATE POLICY \"{policyName}\"",
            StringComparison.Ordinal);
        int backfillStart = script.IndexOf(
            backfill,
            StringComparison.Ordinal);
        int dropped = script.IndexOf(
            $"DROP POLICY \"{policyName}\"",
            StringComparison.Ordinal);
        int transactionStart = script.LastIndexOf(
            "START TRANSACTION;",
            created,
            StringComparison.Ordinal);
        int commit = script.IndexOf(
            "COMMIT;",
            dropped,
            StringComparison.Ordinal);

        Assert.True(created >= 0);
        Assert.True(transactionStart >= 0);
        Assert.True(backfillStart > created);
        Assert.True(dropped > backfillStart);
        Assert.True(commit > dropped);
    }

    [GeneratedRegex(
        @"current_setting\('vistara\.tenant_id', true\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TenantSettingCall();

    [GeneratedRegex(
        @"""tenant_id""\s*=\s*NULLIF\(\s*current_setting\('vistara\.tenant_id', true\),\s*''\s*\)::uuid",
        RegexOptions.CultureInvariant)]
    private static partial Regex FailClosedTenantPredicate();
}
