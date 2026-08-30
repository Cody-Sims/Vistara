using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Vistara.Persistence.Sharing;

public sealed class SharingDbContext(
    DbContextOptions<SharingDbContext> options) : DbContext(options)
{
    internal DbSet<SharingShareRow> Shares => Set<SharingShareRow>();

    internal DbSet<SharingIdempotencyRow> Idempotency =>
        Set<SharingIdempotencyRow>();

    internal DbSet<SharingSessionRow> Sessions =>
        Set<SharingSessionRow>();

    internal DbSet<SharingRateLimitRow> RateLimits =>
        Set<SharingRateLimitRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SharingPersistenceContributor.Configure(modelBuilder);
        ApplyPortableConventions(modelBuilder);
    }

    private static void ApplyPortableConventions(ModelBuilder modelBuilder)
    {
        var dateConverter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var nullableDateConverter =
            new ValueConverter<DateTimeOffset?, DateTime?>(
                value => value.HasValue
                    ? value.Value.UtcDateTime
                    : null,
                value => value.HasValue
                    ? new DateTimeOffset(
                        DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
                    : null);
        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dateConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableDateConverter);
                }
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}

internal static class SharingPersistenceContributor
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SharingShareRow>(entity =>
        {
            entity.ToTable("sharing_shares", table =>
            {
                table.HasCheckConstraint(
                    "ck_sharing_shares_version",
                    "\"version\" >= 1");
                table.HasCheckConstraint(
                    "ck_sharing_shares_permissions",
                    "(\"permissions\" & 1) = 1 AND \"permissions\" BETWEEN 1 AND 7");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new
            {
                row.PepperVersionId,
                row.TokenDigestHex,
            }).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.CreatedAtUtc,
                row.Id,
            });
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ExpiresAtUtc,
            });
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.RevokedAtUtc,
            });
            entity.Property(row => row.Name).HasMaxLength(200);
            entity.Property(row => row.TargetType).HasMaxLength(16);
            entity.Property(row => row.MetadataExposure).HasMaxLength(16);
            entity.Property(row => row.PepperVersionId).HasMaxLength(8);
            entity.Property(row => row.TokenDigestHex).HasMaxLength(64);
            entity.Property(row => row.PasswordHash).HasMaxLength(1024);
            entity.Property(row => row.RequestHash).HasMaxLength(64);
            entity.Property(row => row.AssetsJson).HasColumnType("text");
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<SharingIdempotencyRow>(entity =>
        {
            entity.ToTable("sharing_idempotency");
            entity.HasKey(row => row.KeyHash);
            entity.Property(row => row.KeyHash).HasMaxLength(64);
            entity.Property(row => row.RequestHash).HasMaxLength(64);
        });
        modelBuilder.Entity<SharingSessionRow>(entity =>
        {
            entity.ToTable("sharing_sessions", table =>
                table.HasCheckConstraint(
                    "ck_sharing_sessions_version",
                    "\"share_version\" >= 1"));
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new
            {
                row.PepperVersionId,
                row.DigestHex,
            }).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ExpiresAtUtc,
            });
            entity.Property(row => row.PepperVersionId).HasMaxLength(8);
            entity.Property(row => row.DigestHex).HasMaxLength(64);
            entity.HasOne<SharingShareRow>()
                .WithMany()
                .HasForeignKey(row => new
                {
                    row.TenantId,
                    row.ShareId,
                })
                .HasPrincipalKey(row => new
                {
                    row.TenantId,
                    row.Id,
                })
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SharingRateLimitRow>(entity =>
        {
            entity.ToTable("sharing_rate_limits", table =>
            {
                table.HasCheckConstraint(
                    "ck_sharing_rate_limits_count",
                    "\"request_count\" > 0");
                table.HasCheckConstraint(
                    "ck_sharing_rate_limits_version",
                    "\"version\" >= 1");
            });
            entity.HasKey(row => row.KeyHash);
            entity.Property(row => row.KeyHash).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
    }
}

internal sealed class SharingShareRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CreatedByActorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public Guid? AlbumId { get; set; }
    public string AssetsJson { get; set; } = "[]";
    public int Permissions { get; set; }
    public string MetadataExposure { get; set; } = string.Empty;
    public string PepperVersionId { get; set; } = string.Empty;
    public string TokenDigestHex { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? RevokedByActorId { get; set; }
    public long Version { get; set; }
    public string RequestHash { get; set; } = string.Empty;
}

internal sealed class SharingIdempotencyRow
{
    public string KeyHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public Guid ShareId { get; set; }
}

internal sealed class SharingSessionRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ShareId { get; set; }
    public long ShareVersion { get; set; }
    public string PepperVersionId { get; set; } = string.Empty;
    public string DigestHex { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

internal sealed class SharingRateLimitRow
{
    public string KeyHash { get; set; } = string.Empty;
    public DateTimeOffset WindowStartedAtUtc { get; set; }
    public int RequestCount { get; set; }
    public long Version { get; set; }
}
