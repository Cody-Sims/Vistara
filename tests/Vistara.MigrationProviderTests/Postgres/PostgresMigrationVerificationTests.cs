using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Vistara.Migrations.Postgres;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.MigrationProviderTests.Postgres;

public sealed class PostgresMigrationVerificationTests
{
    private const string InitialMigration = "20260829000000_InitialCreate";
    private const string UploadIngestMigration =
        "20260829034648_AddUploadIngestPersistence";
    private const string RuntimeReconciliationMigration =
        "20260829123733_CompleteRuntimeAndUploadReconciliation";
    private const string LegacyDataMigration =
        "20260829130313_NormalizeLegacyRuntimeData";
    private const string LegacyUploadQuotaMigration =
        "20260829183622_ReconcileLegacyUploadJobQuota";
    private const string WorkerTenantCatalogMigration =
        "20260830044748_AddWorkerTenantCatalog";
    private const string LocalCredentialsMigration =
        "20260830233131_AddLocalCredentials";
    private const string PlatformBootstrapMigration =
        "20260831000526_AddPlatformBootstrap";
    private const string UserPreferencesMigration =
        "20260831002849_AddUserPreferences";
    private const string SharingPersistenceMigration =
        "20260830101824_AddSharingPersistence";
    private const string OidcLoginRequestsMigration =
        "20260901000000_AddOidcLoginRequests";
    private const int ReplacedCheckConstraints = 3;
    private const int MigrationBackfillPolicies = 13;

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

    [Fact]
    public void Initial_and_idempotent_scripts_cover_the_schema_and_fail_closed_rls()
    {
        using VistaraDbContext context = CreatePostgresContext();

        Assert.Equal(ExpectedMigrations, context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());

        IMigrator migrator = context.GetService<IMigrator>();
        IRelationalModel model =
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        string script = migrator.GenerateScript();
        string idempotentScript = migrator.GenerateScript(
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Equal(model.Tables.Count() + 1, Count(script, "CREATE TABLE "));
        Assert.Equal(
            model.Tables.Sum(table => table.Indexes.Count()),
            Count(script, "CREATE INDEX ") + Count(script, "CREATE UNIQUE INDEX "));
        Assert.Equal(
            model.Tables.Sum(table => table.ForeignKeyConstraints.Count()),
            Count(script, "FOREIGN KEY"));
        Assert.Equal(
            model.Tables.Sum(table => table.CheckConstraints.Count()) +
            PostgresTenantRowSecurity.TenantOwnedTables.Count +
            ReplacedCheckConstraints +
            MigrationBackfillPolicies,
            Count(script, "CHECK ("));
        Assert.Equal(
            PostgresTenantRowSecurity.TenantOwnedTables.Count,
            Count(script, "ENABLE ROW LEVEL SECURITY"));
        Assert.Equal(
            PostgresTenantRowSecurity.TenantOwnedTables.Count,
            Count(script, "FORCE ROW LEVEL SECURITY"));
        Assert.Equal(
            PostgresTenantRowSecurity.TenantOwnedTables.Count,
            Count(script, "CREATE POLICY \"tenant_isolation\""));
        Assert.Equal(
            PostgresTenantRowSecurity.TenantOwnedTables.Count * 2,
            Count(script, "current_setting('vistara.tenant_id', true)"));
        Assert.Contains(InitialMigration, idempotentScript, StringComparison.Ordinal);
        Assert.Contains(UploadIngestMigration, idempotentScript, StringComparison.Ordinal);
        Assert.Contains(
            RuntimeReconciliationMigration,
            idempotentScript,
            StringComparison.Ordinal);
        Assert.Contains(
            LegacyDataMigration,
            idempotentScript,
            StringComparison.Ordinal);
        Assert.Contains(
            LegacyUploadQuotaMigration,
            idempotentScript,
            StringComparison.Ordinal);
        Assert.Contains(
            WorkerTenantCatalogMigration,
            idempotentScript,
            StringComparison.Ordinal);
        Assert.Contains(
            SharingPersistenceMigration,
            idempotentScript,
            StringComparison.Ordinal);
        Assert.Contains(
            OidcLoginRequestsMigration,
            idempotentScript,
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
            "tenant.quotas_json IS JSON OBJECT",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE platform_bootstrap",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ck_platform_bootstrap_singleton",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ALTER TABLE \"platform_bootstrap\" ENABLE ROW LEVEL SECURITY",
            script,
            StringComparison.Ordinal);
        foreach (string table in PostgresTenantRowSecurity.IdentityDirectoryTables)
        {
            Assert.Contains(
                $"CREATE POLICY \"identity_directory\" ON \"{table}\"",
                script,
                StringComparison.Ordinal);
        }
        Assert.Equal(
            PostgresTenantRowSecurity.TenantOwnedTables.Count,
            Count(idempotentScript, "CREATE POLICY \"tenant_isolation\""));

        foreach (string table in PostgresTenantRowSecurity.TenantOwnedTables)
        {
            Assert.Contains(
                $"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY",
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                $"CREATE POLICY \"tenant_isolation\" ON \"{table}\"",
                script,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The OIDC login request table is deliberately tenant-independent: it is
    /// written before any tenant scope exists. It must therefore never gain a
    /// tenant column, never be enrolled in row-level security, and never appear
    /// in the tenant-owned table list that drives the fail-closed policies.
    /// </summary>
    [Fact]
    public void Oidc_login_requests_is_created_without_tenant_row_security()
    {
        using VistaraDbContext context = CreatePostgresContext();
        string script = context.GetService<IMigrator>().GenerateScript();
        ITable table = context.GetService<IDesignTimeModel>()
            .Model.GetRelationalModel().Tables
            .Single(candidate => candidate.Name == "oidc_login_requests");

        Assert.Contains(
            "CREATE TABLE oidc_login_requests",
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            [
                "code_verifier",
                "consumed_at_utc",
                "created_at_utc",
                "expires_at_utc",
                "handle_digest",
                "nonce_digest",
                "provider_id",
                "redirect_uri",
                "return_to",
                "state_digest",
            ],
            table.Columns.Select(column => column.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["state_digest"],
            table.PrimaryKey!.Columns.Select(column => column.Name));
        Assert.Equal(
            ["ix_oidc_login_requests_expires_at_utc"],
            table.Indexes.Select(index => index.Name));
        Assert.Empty(table.ForeignKeyConstraints);
        Assert.DoesNotContain("oidc_login_requests", PostgresTenantRowSecurity.TenantOwnedTables);
        Assert.DoesNotContain(
            "oidc_login_requests",
            PostgresTenantRowSecurity.IdentityDirectoryTables);
        Assert.DoesNotContain(
            "ALTER TABLE \"oidc_login_requests\" ENABLE ROW LEVEL SECURITY",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CREATE POLICY \"tenant_isolation\" ON \"oidc_login_requests\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CREATE POLICY \"identity_directory\" ON \"oidc_login_requests\"",
            script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// PostgreSQL must express the single-use claim as one conditional
    /// <c>UPDATE</c> and the opportunistic sweep as one bounded <c>DELETE</c>.
    /// PostgreSQL takes a row lock for an <c>UPDATE</c> and, under READ
    /// COMMITTED, re-evaluates the predicate against the committed row, so the
    /// losing callback sees <c>consumed_at_utc</c> set and updates zero rows.
    /// Reading the row first and updating it afterwards would let two
    /// concurrent callbacks both complete the same authorization, so the
    /// generated statements are asserted directly rather than reviewed.
    /// </summary>
    [Fact]
    public async Task Oidc_login_request_consume_and_sweep_translate_to_locking_statements()
    {
        var interceptor = new CapturingCommandInterceptor();
        var options = new DbContextOptionsBuilder<AuthenticationCatalogDbContext>();
        options
            .UseNpgsql("Host=migration-verification;Database=vistara")
            .AddInterceptors(interceptor, new OfflineConnectionInterceptor());
        await using var catalog = new AuthenticationCatalogDbContext(options.Options);
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        byte[] stateDigest = new byte[32];

        _ = await catalog.Set<OidcLoginRequestRow>()
            .Where(row =>
                row.StateDigest == stateDigest &&
                row.ConsumedAtUtc == null &&
                row.ExpiresAtUtc > now &&
                row.CreatedAtUtc <= now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.ConsumedAtUtc, now),
                CancellationToken.None);
        _ = await catalog.Set<OidcLoginRequestRow>()
            .Where(row => row.ExpiresAtUtc < now)
            .OrderBy(row => row.ExpiresAtUtc)
            .Take(100)
            .ExecuteDeleteAsync(CancellationToken.None);

        Assert.Equal(2, interceptor.Commands.Count);
        string consume = interceptor.Commands[0];
        string sweep = interceptor.Commands[1];

        Assert.StartsWith("UPDATE oidc_login_requests", consume, StringComparison.Ordinal);
        Assert.Contains("SET consumed_at_utc", consume, StringComparison.Ordinal);
        Assert.Contains("consumed_at_utc IS NULL", consume, StringComparison.Ordinal);
        Assert.Contains("expires_at_utc >", consume, StringComparison.Ordinal);
        Assert.Contains("created_at_utc <=", consume, StringComparison.Ordinal);
        Assert.Contains("state_digest =", consume, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", consume, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", consume, StringComparison.Ordinal);

        Assert.StartsWith("DELETE FROM oidc_login_requests", sweep, StringComparison.Ordinal);
        Assert.Contains("expires_at_utc <", sweep, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sweep, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sweep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Captures the SQL PostgreSQL would run and suppresses the round trip, so
    /// the translation is asserted without a reachable database.
    /// </summary>
    private sealed class CapturingCommandInterceptor : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }

    /// <summary>
    /// Keeps the offline command capture from opening a socket.
    /// </summary>
    private sealed class OfflineConnectionInterceptor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    [Fact]
    public void Provider_models_have_identical_logical_schema_coverage()
    {
        using VistaraDbContext sqlite = CreateSqliteContext();
        using VistaraDbContext postgres = CreatePostgresContext();

        Assert.Equal(
            DescribeLogicalSchema(sqlite.GetService<IDesignTimeModel>().Model),
            DescribeLogicalSchema(postgres.GetService<IDesignTimeModel>().Model));

        string[] tenantTables = postgres.GetService<IDesignTimeModel>()
            .Model.GetRelationalModel().Tables
            .Where(table => table.Columns.Any(column => column.Name == "tenant_id"))
            .Select(table => table.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(tenantTables, PostgresTenantRowSecurity.TenantOwnedTables);
    }

    private static VistaraDbContext CreatePostgresContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<VistaraDbContext>();
        optionsBuilder.UseNpgsql(
            postgres => postgres.UseVistaraMigrations());
        return CreateContext(optionsBuilder.Options);
    }

    private static VistaraDbContext CreateSqliteContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<VistaraDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=:memory:",
            sqlite => Vistara.Migrations.Sqlite.SqliteMigrationConfiguration
                .UseVistaraMigrations(sqlite));
        return CreateContext(optionsBuilder.Options);
    }

    private static VistaraDbContext CreateContext(
        DbContextOptions<VistaraDbContext> options) =>
        new(
            options,
            new FixedTenantScope(
                Guid.Parse("01991a54-6c00-7000-8000-000000000001")));

    private static string[] DescribeLogicalSchema(IModel model) =>
        model.GetEntityTypes()
            .Select(DescribeEntity)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string DescribeEntity(IReadOnlyEntityType entity)
    {
        string table = entity.GetTableName()!;
        StoreObjectIdentifier storeObject =
            StoreObjectIdentifier.Table(table, entity.GetSchema());
        string columns = Join(entity.GetProperties().Select(
            property => property.GetColumnName(storeObject)!));
        string keys = Join(entity.GetKeys().Select(
            key => Join(key.Properties.Select(
                property => property.GetColumnName(storeObject)!))));
        string indexes = Join(entity.GetIndexes().Select(
            index =>
                $"{index.IsUnique}:{Join(index.Properties.Select(property => property.GetColumnName(storeObject)!))}"));
        string foreignKeys = Join(entity.GetForeignKeys().Select(
            foreignKey =>
                $"{Join(foreignKey.Properties.Select(property => property.GetColumnName(storeObject)!))}" +
                $"->{foreignKey.PrincipalEntityType.GetTableName()}:" +
                $"{foreignKey.DeleteBehavior}"));
        string checks = Join(entity.GetCheckConstraints().Select(
            check => $"{check.Name}:{check.Sql}"));
        return $"{table}|{columns}|{keys}|{indexes}|{foreignKeys}|{checks}";
    }

    private static string Join(IEnumerable<string> values) =>
        string.Join(",", values.Order(StringComparer.Ordinal));

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
