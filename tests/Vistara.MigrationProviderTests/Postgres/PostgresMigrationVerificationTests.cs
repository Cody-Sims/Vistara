using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Vistara.Migrations.Postgres;
using Vistara.Persistence;
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
