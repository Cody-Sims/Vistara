using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Auth;

public sealed class AuthenticationCatalogDbContext(
    DbContextOptions<AuthenticationCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<AuthenticationRouteRow> Routes =>
        Set<AuthenticationRouteRow>();

    internal DbSet<OidcLoginRequestRow> OidcLoginRequests =>
        Set<OidcLoginRequestRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuthenticationPersistenceContributor.ConfigureRoute(
            modelBuilder.Entity<AuthenticationRouteRow>());
        OidcLoginRequestPersistenceContributor.Configure(
            modelBuilder.Entity<OidcLoginRequestRow>());
        PortableColumnConventions.Apply(modelBuilder);
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
        PortableColumnConventions.Apply(modelBuilder);
    }
}

/// <summary>
/// Maps the tenant-independent <c>oidc_login_requests</c> table. The same
/// configuration is applied by <c>VistaraDbContext</c>, which owns the migration
/// history, and by <see cref="AuthenticationCatalogDbContext"/>, which serves
/// the sign-in path before any tenant scope exists, so the two contexts cannot
/// drift apart. The table intentionally has no <c>tenant_id</c> column and
/// therefore no row-level security policy.
/// </summary>
public static class OidcLoginRequestPersistenceContributor
{
    public const string TableName = "oidc_login_requests";
    public const string ExpiryIndexName = "ix_oidc_login_requests_expires_at_utc";

    /// <summary>SHA-256 output length; every stored digest is exactly this.</summary>
    public const int DigestLength = 32;
    public const int ProviderIdMaxLength = 32;
    public const int CodeVerifierMinLength = 43;
    public const int CodeVerifierMaxLength = 128;
    public const int UriMaxLength = 2048;

    public static void Configure(EntityTypeBuilder<OidcLoginRequestRow> entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity.ToTable(TableName, table =>
        {
            table.HasCheckConstraint(
                "ck_oidc_login_requests_lifetime",
                "\"expires_at_utc\" > \"created_at_utc\"");
            table.HasCheckConstraint(
                "ck_oidc_login_requests_consumed",
                "\"consumed_at_utc\" IS NULL OR " +
                "\"consumed_at_utc\" >= \"created_at_utc\"");
        });
        entity.HasKey(row => row.StateDigest);
        entity.Property(row => row.StateDigest)
            .ValueGeneratedNever()
            .HasMaxLength(DigestLength);
        entity.Property(row => row.ProviderId)
            .IsRequired()
            .HasMaxLength(ProviderIdMaxLength);
        entity.Property(row => row.NonceDigest)
            .IsRequired()
            .HasMaxLength(DigestLength);
        entity.Property(row => row.HandleDigest)
            .IsRequired()
            .HasMaxLength(DigestLength);
        entity.Property(row => row.CodeVerifier)
            .IsRequired()
            .HasMaxLength(CodeVerifierMaxLength);
        entity.Property(row => row.RedirectUri)
            .IsRequired()
            .HasMaxLength(UriMaxLength);
        entity.Property(row => row.ReturnTo)
            .IsRequired()
            .HasMaxLength(UriMaxLength);
        entity.HasIndex(row => row.ExpiresAtUtc)
            .HasDatabaseName(ExpiryIndexName);
    }
}

/// <summary>
/// Applies the snake_case column names and the UTC <see cref="DateTimeOffset"/>
/// conversions that keep the catalog contexts byte-compatible with the schema
/// owned by <c>VistaraDbContext</c>'s migrations.
/// </summary>
internal static class PortableColumnConventions
{
    internal static void Apply(ModelBuilder modelBuilder)
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
