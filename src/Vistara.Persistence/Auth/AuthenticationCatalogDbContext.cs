using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Auth;

public sealed class AuthenticationCatalogDbContext(
    DbContextOptions<AuthenticationCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<AuthenticationRouteRow> Routes =>
        Set<AuthenticationRouteRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuthenticationPersistenceContributor.ConfigureRoute(
            modelBuilder.Entity<AuthenticationRouteRow>());
        ApplyPortableConventions(modelBuilder);
    }

    private static void ApplyPortableConventions(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        foreach (var property in modelBuilder.Entity<AuthenticationRouteRow>()
                     .Metadata.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(converter);
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

public sealed class JwtRevocationCatalogDbContext(
    DbContextOptions<JwtRevocationCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<RevokedTokenRow> RevokedTokens => Set<RevokedTokenRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RevokedTokenRow>(entity =>
        {
            entity.ToTable("revoked_tokens");
            entity.HasKey(row => new { row.Issuer, row.Jti });
            entity.HasIndex(row => row.ExpiresAtUtc);
            entity.Property(row => row.Issuer).HasMaxLength(2048);
            entity.Property(row => row.Jti).HasMaxLength(512);
            entity.Property(row => row.Reason).HasMaxLength(500);
        });
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        foreach (var property in modelBuilder.Entity<RevokedTokenRow>()
                     .Metadata.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(converter);
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
