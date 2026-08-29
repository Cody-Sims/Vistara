using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Derivatives;

internal static class DerivativeRequestPersistenceContributor
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DerivativeRequestRow>(entity =>
        {
            entity.ToTable("derivative_requests", table =>
            {
                table.HasCheckConstraint(
                    "ck_derivative_requests_state",
                    "\"state\" IN ('Queued','Processing','Ready','Failed')");
                table.HasCheckConstraint(
                    "ck_derivative_requests_dimensions",
                    "\"width\" > 0 AND \"height\" > 0");
                table.HasCheckConstraint(
                    "ck_derivative_requests_quality",
                    "\"quality\" BETWEEN 1 AND 100");
                table.HasCheckConstraint(
                    "ck_derivative_requests_version",
                    "\"version\" >= 1");
                table.HasCheckConstraint(
                    "ck_derivative_requests_ready",
                    "(\"state\" <> 'Ready') OR " +
                    "(\"representation_storage_key\" IS NOT NULL AND " +
                    "\"representation_content_length\" > 0 AND " +
                    "\"representation_content_type\" IS NOT NULL AND " +
                    "\"representation_sha256\" IS NOT NULL)");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.AssetId,
                row.IdempotencyKey,
            }).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.GenerationIdentity,
            }).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.PipelineId,
                row.SourceSha256,
                row.RecipeSha256,
                row.Extension,
            });
            entity.HasIndex(row => new { row.TenantId, row.AssetId, row.CreatedAtUtc });
            entity.Property(row => row.IdempotencyKey).HasMaxLength(128);
            entity.Property(row => row.RequestHash).HasMaxLength(64);
            entity.Property(row => row.PresetName).HasMaxLength(64);
            entity.Property(row => row.Fit).HasMaxLength(16);
            entity.Property(row => row.Format).HasMaxLength(16);
            entity.Property(row => row.PipelineId).HasMaxLength(64);
            entity.Property(row => row.PipelineFingerprint).HasMaxLength(512);
            entity.Property(row => row.SourceSha256).HasMaxLength(64);
            entity.Property(row => row.RecipeSha256).HasMaxLength(64);
            entity.Property(row => row.GenerationIdentity).HasMaxLength(64);
            entity.Property(row => row.CacheKey).HasMaxLength(1024);
            entity.Property(row => row.Extension).HasMaxLength(8);
            entity.Property(row => row.State).HasMaxLength(32);
            entity.Property(row => row.FailureCode).HasMaxLength(128);
            entity.Property(row => row.RepresentationStorageKey).HasMaxLength(1024);
            entity.Property(row => row.RepresentationContentType).HasMaxLength(128);
            entity.Property(row => row.RepresentationSha256).HasMaxLength(64);
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
            entity.HasOne<JobRow>()
                .WithMany()
                .HasForeignKey(row => row.JobId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
