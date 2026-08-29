using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Auth;

internal static class AuthenticationPersistenceContributor
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthenticationRouteRow>(ConfigureRoute);
        modelBuilder.Entity<CookieSessionRow>(entity =>
        {
            entity.ToTable("cookie_sessions", table =>
            {
                table.HasCheckConstraint(
                    "ck_cookie_sessions_role",
                    "\"role\" IN ('TenantOwner','TenantAdmin','Member','Viewer')");
                table.HasCheckConstraint(
                    "ck_cookie_sessions_versions",
                    "\"user_version\" >= 1 AND \"membership_version\" >= 1 AND \"version\" >= 1");
                table.HasCheckConstraint(
                    "ck_cookie_sessions_expiry",
                    "\"last_seen_at_utc\" >= \"issued_at_utc\" AND " +
                    "\"idle_expires_at_utc\" > \"last_seen_at_utc\" AND " +
                    "\"absolute_expires_at_utc\" > \"issued_at_utc\" AND " +
                    "\"idle_expires_at_utc\" <= \"absolute_expires_at_utc\"");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.SessionTokenDigest })
                .IsUnique();
            entity.HasIndex(row => new { row.TenantId, row.UserId });
            entity.Property(row => row.SessionTokenDigest).HasMaxLength(64);
            entity.Property(row => row.AntiforgeryTokenDigest).HasMaxLength(64);
            entity.Property(row => row.Role).HasMaxLength(32);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeliveryGrantRow>(entity =>
        {
            entity.ToTable("delivery_grants", table =>
            {
                table.HasCheckConstraint(
                    "ck_delivery_grants_identity",
                    "\"subject_id\" IS NOT NULL OR \"share_id\" IS NOT NULL");
                table.HasCheckConstraint(
                    "ck_delivery_grants_share",
                    "(\"share_id\" IS NULL AND \"share_version\" IS NULL) OR " +
                    "(\"share_id\" IS NOT NULL AND \"share_version\" >= 1)");
                table.HasCheckConstraint(
                    "ck_delivery_grants_times",
                    "\"expires_at_utc\" > \"not_before_utc\" AND " +
                    "\"not_before_utc\" >= \"issued_at_utc\"");
                table.HasCheckConstraint(
                    "ck_delivery_grants_version",
                    "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.AssetId, row.RevisionId });
            entity.HasIndex(row => new { row.TenantId, row.ExpiresAtUtc });
            entity.Property(row => row.RenditionKind).HasMaxLength(32);
            entity.Property(row => row.RenditionIdentifier).HasMaxLength(256);
            entity.Property(row => row.Permission).HasMaxLength(32);
            entity.Property(row => row.PepperVersionId).HasMaxLength(8);
            entity.Property(row => row.TokenDigestHex).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AssetRevisionRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.RevisionId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    internal static void ConfigureRoute(
        Microsoft.EntityFrameworkCore.Metadata.Builders
            .EntityTypeBuilder<AuthenticationRouteRow> entity)
    {
        entity.ToTable("authentication_routes", table =>
        {
            table.HasCheckConstraint(
                "ck_authentication_routes_kind",
                "\"kind\" IN ('ApiKey','CookieSession','DeliveryGrant')");
        });
        entity.HasKey(row => row.LookupDigest);
        entity.HasIndex(row => new { row.Kind, row.CredentialId }).IsUnique();
        entity.HasIndex(row => new { row.Kind, row.PrincipalId });
        entity.Property(row => row.LookupDigest).HasMaxLength(64);
        entity.Property(row => row.Kind).HasMaxLength(32);
        entity.Property(row => row.RoutedTenantId).HasColumnName("tenant_id");
    }
}
