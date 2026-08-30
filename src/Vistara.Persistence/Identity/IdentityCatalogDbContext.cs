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
        ApplyPortableConventions(modelBuilder);
    }

    private static void ApplyPortableConventions(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(converter);
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
