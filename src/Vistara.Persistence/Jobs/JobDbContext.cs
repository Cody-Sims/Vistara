using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vistara.Domain.Jobs;

namespace Vistara.Persistence.Jobs;

public sealed class JobDbContext(DbContextOptions<JobDbContext> options) : DbContext(options)
{
    public DbSet<JobRow> Jobs => Set<JobRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRow>(entity =>
        {
            entity.ToTable("jobs", table =>
            {
                table.HasCheckConstraint(
                    "ck_jobs_state",
                    "\"state\" IN ('Pending','Leased','RetryScheduled','Completed','DeadLettered')");
                table.HasCheckConstraint("ck_jobs_payload_version", "\"payload_version\" >= 1");
                table.HasCheckConstraint("ck_jobs_attempts", "\"attempts\" >= 0 AND \"attempts\" <= \"max_attempts\"");
                table.HasCheckConstraint("ck_jobs_max_attempts", "\"max_attempts\" >= 1");
                table.HasCheckConstraint("ck_jobs_version", "\"version\" >= 1");
                table.HasCheckConstraint(
                    "ck_jobs_lease",
                    "(\"state\" = 'Leased' AND \"lease_owner\" IS NOT NULL AND \"lease_acquired_at_utc\" IS NOT NULL AND \"lease_heartbeat_at_utc\" IS NOT NULL AND \"lease_expires_at_utc\" IS NOT NULL) OR (\"state\" <> 'Leased' AND \"lease_owner\" IS NULL AND \"lease_acquired_at_utc\" IS NULL AND \"lease_heartbeat_at_utc\" IS NULL AND \"lease_expires_at_utc\" IS NULL)");
            });
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.TenantId, row.DedupeKey }).IsUnique();
            entity.HasIndex(row => new
            {
                row.State,
                row.AvailableAtUtc,
                row.Priority,
                row.CreatedAtUtc,
            });
            entity.HasIndex(row => row.LeaseExpiresAtUtc);
            entity.Property(row => row.Type).HasMaxLength(JobType.MaximumLength);
            entity.Property(row => row.DedupeKey).HasMaxLength(JobDedupeKey.MaximumLength);
            entity.Property(row => row.State).HasMaxLength(32);
            entity.Property(row => row.LeaseOwner).HasMaxLength(JobLeaseOwner.MaximumLength);
            entity.Property(row => row.FailureCode).HasMaxLength(128);
            entity.Property(row => row.Payload).HasColumnType("text");
            entity.Property(row => row.TraceParent).HasMaxLength(512);
            entity.Property(row => row.Version).IsConcurrencyToken();
        });

        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, DateTime?>(
            value => value.HasValue ? value.Value.UtcDateTime : null,
            value => value.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
                : null);

        foreach (var property in modelBuilder.Entity<JobRow>().Metadata.GetProperties())
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

    private static string ToSnakeCase(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}
