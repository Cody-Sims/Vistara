using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Vistara.Persistence.Outbox;

public static class OutboxPersistenceContributor
{
    public static void Configure(
        ModelBuilder modelBuilder,
        IOutboxTenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(tenantContext);

        modelBuilder.Entity<OutboxMessageRow>(entity =>
        {
            entity.ToTable("outbox_messages", table =>
            {
                table.HasCheckConstraint("ck_outbox_sequence", "\"sequence\" > 0");
                table.HasCheckConstraint("ck_outbox_event_version", "\"event_version\" > 0");
                table.HasCheckConstraint("ck_outbox_attempts", "\"attempts\" >= 0");
                table.HasCheckConstraint("ck_outbox_version", "\"version\" > 0");
            });
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.TenantId, row.Sequence }).IsUnique();
            entity.HasIndex(row => row.EventId).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.PublishedAtUtc,
                row.AvailableAtUtc,
                row.Sequence,
            });
            entity.Property(row => row.EventType).HasMaxLength(200);
            entity.Property(row => row.ClientPayload).HasColumnType("text");
            entity.Property(row => row.LastErrorCode).HasMaxLength(200);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasQueryFilter(row => row.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<EventLogRow>(entity =>
        {
            entity.ToTable("event_log", table =>
            {
                table.HasCheckConstraint("ck_event_log_sequence", "\"sequence\" > 0");
                table.HasCheckConstraint("ck_event_log_event_version", "\"event_version\" > 0");
            });
            entity.HasKey(row => new { row.TenantId, row.Sequence });
            entity.HasIndex(row => row.EventId).IsUnique();
            entity.HasIndex(row => new { row.TenantId, row.RetainedAtUtc, row.Sequence });
            entity.Property(row => row.EventType).HasMaxLength(200);
            entity.Property(row => row.ClientPayload).HasColumnType("text");
            entity.HasQueryFilter(row => row.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<OutboxSequenceRow>(entity =>
        {
            entity.ToTable("outbox_sequences", table =>
            {
                table.HasCheckConstraint(
                    "ck_outbox_sequences_current",
                    "\"current_sequence\" > 0");
                table.HasCheckConstraint(
                    "ck_outbox_sequences_published",
                    "\"last_published_sequence\" >= 0 AND " +
                    "\"last_published_sequence\" <= \"current_sequence\"");
                table.HasCheckConstraint("ck_outbox_sequences_version", "\"version\" > 0");
            });
            entity.HasKey(row => row.TenantId);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasQueryFilter(row => row.TenantId == tenantContext.TenantId);
        });

        ApplyPortableConventions(modelBuilder);
    }

    private static void ApplyPortableConventions(ModelBuilder modelBuilder)
    {
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, DateTime?>(
            value => value.HasValue ? value.Value.UtcDateTime : null,
            value => value.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
                : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(type => type.ClrType.Namespace == typeof(OutboxMessageRow).Namespace))
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dateTimeOffsetConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableDateTimeOffsetConverter);
                }
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var characters = new List<char>(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                characters.Add('_');
            }

            characters.Add(char.ToLowerInvariant(character));
        }

        return new string([.. characters]);
    }
}
