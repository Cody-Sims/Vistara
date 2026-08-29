using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;
using Xunit;

namespace Vistara.IntegrationTests.UploadPersistence;

public sealed class UploadPersistenceSchemaTests
{
    private static readonly string[] OperationalTables =
    [
        "audit_events",
        "event_log",
        "ingest_operations",
        "jobs",
        "outbox_messages",
        "outbox_sequences",
        "quota_usage",
        "upload_reconciliation_checkpoints",
    ];

    [Fact]
    public async Task New_operational_tables_are_tenant_filtered_and_relationships_are_composite()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        IModel model = context.Model;

        Assert.All(
            OperationalTables,
            table => Assert.Contains(
                model.GetEntityTypes(),
                entity =>
                    entity.GetTableName() == table &&
                    entity.GetDeclaredQueryFilters().Count > 0));

        IReadOnlyEntityType ingest =
            model.FindEntityType(typeof(IngestOperationRow))!;
        Assert.All(
            ingest.GetForeignKeys(),
            foreignKey => Assert.Contains(
                foreignKey.Properties,
                property => property.Name == nameof(IngestOperationRow.TenantId)));

        IReadOnlyEntityType upload =
            model.FindEntityType(typeof(UploadSessionRow))!;
        Assert.All(
            upload.GetForeignKeys().Where(foreignKey =>
                foreignKey.Properties.Any(property =>
                    property.Name is
                        nameof(UploadSessionRow.ActivatedAssetId) or
                        nameof(UploadSessionRow.ActivatedRevisionId) or
                        nameof(UploadSessionRow.ActivatedBlobId))),
            foreignKey => Assert.Contains(
                foreignKey.Properties,
                property => property.Name == nameof(UploadSessionRow.TenantId)));

        IReadOnlyEntityType usage =
            model.FindEntityType(typeof(QuotaUsageRow))!;
        Assert.Equal(
            [nameof(QuotaUsageRow.TenantId)],
            usage.FindPrimaryKey()!.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Upload_rows_have_no_signed_url_or_credential_columns()
    {
        string[] propertyNames = typeof(UploadSessionRow)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
