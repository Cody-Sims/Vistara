using Microsoft.EntityFrameworkCore;

namespace Vistara.Persistence.Media;

internal static class MediaPersistenceContributor
{
    internal static void Configure(ModelBuilder modelBuilder) =>
        ConfigurePublicRoutes(modelBuilder.Entity<PublicDerivativeRouteRow>());

    internal static void ConfigurePublicRoutes(
        Microsoft.EntityFrameworkCore.Metadata.Builders
            .EntityTypeBuilder<PublicDerivativeRouteRow> entity)
    {
        entity.ToTable("public_derivative_routes");
        entity.HasKey(row => row.LookupDigest);
        entity.HasIndex(row => new { row.RoutedTenantId, row.RequestId }).IsUnique();
        entity.Property(row => row.LookupDigest).HasMaxLength(64);
        entity.Property(row => row.RoutedTenantId).HasColumnName("tenant_id");
    }
}
