using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Identity;

/// <summary>
/// Reads and writes the tenant-independent identity tables (<c>users</c>,
/// <c>local_identities</c>, and <c>local_credentials</c>). Those tables carry
/// no tenant column and are excluded from row-level security, so a browser
/// sign-in can resolve a principal before a tenant scope exists.
/// </summary>
public sealed class IdentityCatalogDbContext(
    DbContextOptions<IdentityCatalogDbContext> options) : DbContext(options)
{
    public DbSet<UserRow> Users => Set<UserRow>();

    public DbSet<LocalIdentityRow> LocalIdentities => Set<LocalIdentityRow>();

    public DbSet<LocalCredentialRow> LocalCredentials => Set<LocalCredentialRow>();

    public DbSet<UserPreferenceRow> UserPreferences => Set<UserPreferenceRow>();

    /// <summary>
    /// Tenant rows exposed only for the membership directory. PostgreSQL still
    /// enforces row-level security; the identity_directory policy grants the
    /// read strictly inside a transaction that opts in.
    /// </summary>
    public DbSet<TenantRow> Tenants => Set<TenantRow>();

    public DbSet<TenantMembershipRow> TenantMemberships => Set<TenantMembershipRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<UserRow>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.NormalizedEmail).IsUnique();
            entity.Property(row => row.NormalizedEmail).HasMaxLength(320);
            entity.Property(row => row.DisplayName).HasMaxLength(200);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<LocalIdentityRow>(entity =>
        {
            entity.ToTable("local_identities");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.NormalizedLogin).IsUnique();
            entity.Property(row => row.NormalizedLogin).HasMaxLength(320);
        });
        modelBuilder.Entity<LocalCredentialRow>(entity =>
        {
            entity.ToTable("local_credentials");
            entity.HasKey(row => row.LocalIdentityId);
            entity.HasIndex(row => row.UserId);
            entity.Property(row => row.PasswordHash).HasMaxLength(512);
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<UserPreferenceRow>(entity =>
        {
            entity.ToTable("user_preferences");
            entity.HasKey(row => row.UserId);
            entity.Property(row => row.Density).HasMaxLength(16);
            entity.Property(row => row.Locale).HasMaxLength(35);
            entity.Property(row => row.TimeZone).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<TenantRow>(entity =>
        {
            entity.ToTable("tenants");
            entity.Property(row => row.Id)
                .HasConversion<TenantKeyValueConverter>();
            entity.Property(row => row.TenantId)
                .HasConversion<TenantKeyValueConverter>();
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Slug).HasMaxLength(63);
            entity.Property(row => row.Name).HasMaxLength(200);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.SettingsJson).HasColumnType("text");
            entity.Property(row => row.QuotasJson).HasColumnType("text");
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<TenantMembershipRow>(entity =>
        {
            entity.ToTable("tenant_memberships");
            entity.Property(row => row.TenantId)
                .HasConversion<TenantKeyValueConverter>();
            entity.HasKey(row => new { row.TenantId, row.UserId });
            entity.HasIndex(row => row.UserId);
            entity.Property(row => row.Role).HasMaxLength(32);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
        ApplyPortableConventions(modelBuilder);
    }

    private static void ApplyPortableConventions(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var nullableConverter = new ValueConverter<DateTimeOffset?, DateTime?>(
            value => value.HasValue ? value.Value.UtcDateTime : null,
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
                    property.SetValueConverter(converter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableConverter);
                }
                else if (property.ClrType == typeof(TenantKey))
                {
                    property.SetValueConverter(new TenantKeyValueConverter());
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
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
