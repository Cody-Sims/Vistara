using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Media;
using Vistara.Persistence.Model;
using Vistara.Persistence.Outbox;
using Vistara.Persistence.Sharing;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence;

public sealed class VistaraDbContext(
    DbContextOptions<VistaraDbContext> options,
    ITenantScope tenantScope) : DbContext(options), IOutboxTenantContext
{
    private readonly ITenantScope _tenantScope =
        tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));

    public Guid TenantId => TenantScopeGuard.RequireTenantId(_tenantScope);

    internal TenantKey CurrentTenantKey => new(TenantId);

    internal void EstablishTenant(Guid tenantId)
    {
        if (_tenantScope is IMutableTenantScope mutable)
        {
            mutable.Establish(tenantId);
        }
    }

    public DbSet<TenantRow> Tenants => Set<TenantRow>();
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<LocalIdentityRow> LocalIdentities => Set<LocalIdentityRow>();
    public DbSet<LocalCredentialRow> LocalCredentials => Set<LocalCredentialRow>();
    public DbSet<PlatformBootstrapRow> PlatformBootstrap => Set<PlatformBootstrapRow>();
    public DbSet<UserPreferenceRow> UserPreferences => Set<UserPreferenceRow>();
    public DbSet<ExternalIdentityRow> ExternalIdentities => Set<ExternalIdentityRow>();
    public DbSet<OidcLoginRequestRow> OidcLoginRequests => Set<OidcLoginRequestRow>();
    public DbSet<TenantMembershipRow> TenantMemberships => Set<TenantMembershipRow>();
    public DbSet<AuthSessionRow> AuthSessions => Set<AuthSessionRow>();
    public DbSet<ApiKeyRow> ApiKeys => Set<ApiKeyRow>();
    public DbSet<RevokedTokenRow> RevokedTokens => Set<RevokedTokenRow>();
    public DbSet<BlobRow> Blobs => Set<BlobRow>();
    public DbSet<AssetRow> Assets => Set<AssetRow>();
    public DbSet<AssetRevisionRow> AssetRevisions => Set<AssetRevisionRow>();
    public DbSet<AssetMetadataHistoryRow> AssetMetadataHistory => Set<AssetMetadataHistoryRow>();
    public DbSet<UploadSessionRow> UploadSessions => Set<UploadSessionRow>();
    public DbSet<UploadPartRow> UploadParts => Set<UploadPartRow>();
    public DbSet<QuotaReservationRow> QuotaReservations => Set<QuotaReservationRow>();
    public DbSet<QuotaUsageRow> QuotaUsage => Set<QuotaUsageRow>();
    public DbSet<UploadReconciliationCheckpointRow> UploadReconciliationCheckpoints =>
        Set<UploadReconciliationCheckpointRow>();
    public DbSet<IdempotencyRequestRow> IdempotencyRequests => Set<IdempotencyRequestRow>();
    public DbSet<IngestOperationRow> IngestOperations => Set<IngestOperationRow>();
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();
    internal DbSet<WorkerTenantCatalogRow> WorkerTenantCatalog =>
        Set<WorkerTenantCatalogRow>();
    public DbSet<AlbumRow> Albums => Set<AlbumRow>();
    public DbSet<AlbumItemRow> AlbumItems => Set<AlbumItemRow>();
    public DbSet<TagRow> Tags => Set<TagRow>();
    public DbSet<AssetTagRow> AssetTags => Set<AssetTagRow>();
    public DbSet<AssetFavoriteRow> AssetFavorites => Set<AssetFavoriteRow>();
    public DbSet<ResourceGrantRow> ResourceGrants => Set<ResourceGrantRow>();
    public DbSet<ShareRow> Shares => Set<ShareRow>();
    public DbSet<ShareAssetRow> ShareAssets => Set<ShareAssetRow>();
    public DbSet<ShareSessionRow> ShareSessions => Set<ShareSessionRow>();
    public DbSet<AssetLifecycleRow> AssetLifecycles => Set<AssetLifecycleRow>();
    public DbSet<TrashEntryRow> TrashEntries => Set<TrashEntryRow>();
    public DbSet<RetentionHoldRow> RetentionHolds => Set<RetentionHoldRow>();
    public DbSet<PurgeBatchRow> PurgeBatches => Set<PurgeBatchRow>();
    public DbSet<PurgeBatchItemRow> PurgeBatchItems => Set<PurgeBatchItemRow>();
    public DbSet<DeletionTombstoneRow> DeletionTombstones => Set<DeletionTombstoneRow>();
    public DbSet<RelationshipSnapshotRow> RelationshipSnapshots => Set<RelationshipSnapshotRow>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTenantWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(
            new TenantRlsCommandInterceptor(_tenantScope));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureIdentity(modelBuilder);
        AuthenticationPersistenceContributor.Configure(modelBuilder);
        RateLimitPersistenceContributor.Configure(modelBuilder);
        ConfigureAssets(modelBuilder);
        DerivativeRequestPersistenceContributor.Configure(modelBuilder);
        MediaPersistenceContributor.Configure(modelBuilder);
        ConfigureUploads(modelBuilder);
        ConfigureIngest(modelBuilder);
        ConfigureJobs(modelBuilder);
        WorkerTenantCatalogPersistenceContributor.Configure(modelBuilder);
        OutboxPersistenceContributor.Configure(modelBuilder, this);
        ConfigureGallery(modelBuilder);
        ConfigureSharing(modelBuilder);
        SharingPersistenceContributor.Configure(modelBuilder);
        ConfigureLifecycle(modelBuilder);
        ApplyTenantFilters(modelBuilder);
        ApplyPortableConventions(modelBuilder);
    }

    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantRow>(entity =>
        {
            entity.ToTable("tenants", table =>
            {
                table.HasCheckConstraint("ck_tenants_identity", "\"id\" = \"tenant_id\"");
                table.HasCheckConstraint(
                    "ck_tenants_status",
                    "\"status\" IN ('Active','Suspended','Deactivated')");
                table.HasCheckConstraint("ck_tenants_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => row.Slug).IsUnique();
            entity.Property(row => row.Slug).HasMaxLength(63);
            entity.Property(row => row.Name).HasMaxLength(200);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.SettingsJson).HasColumnType("text");
            entity.Property(row => row.QuotasJson).HasColumnType("text");
            entity.Property(row => row.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<UserRow>(entity =>
        {
            entity.ToTable("users", table =>
            {
                table.HasCheckConstraint(
                    "ck_users_status",
                    "\"status\" IN ('Active','Suspended','Disabled')");
                table.HasCheckConstraint("ck_users_version", "\"version\" >= 1");
            });
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
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserPreferenceRow>(entity =>
        {
            entity.ToTable("user_preferences", table =>
            {
                table.HasCheckConstraint(
                    "ck_user_preferences_density",
                    "\"density\" IN ('comfortable','compact')");
                table.HasCheckConstraint(
                    "ck_user_preferences_version",
                    "\"version\" >= 1");
            });
            entity.HasKey(row => row.UserId);
            entity.Property(row => row.Density).HasMaxLength(16);
            entity.Property(row => row.Locale).HasMaxLength(35);
            entity.Property(row => row.TimeZone).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<UserRow>()
                .WithOne()
                .HasForeignKey<UserPreferenceRow>(row => row.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlatformBootstrapRow>(entity =>
        {
            entity.ToTable("platform_bootstrap", table =>
            {
                table.HasCheckConstraint(
                    "ck_platform_bootstrap_singleton",
                    "\"id\" = 1");
                table.HasCheckConstraint(
                    "ck_platform_bootstrap_version",
                    "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<LocalCredentialRow>(entity =>
        {
            entity.ToTable("local_credentials", table =>
                table.HasCheckConstraint(
                    "ck_local_credentials_version",
                    "\"version\" >= 1"));
            entity.HasKey(row => row.LocalIdentityId);
            entity.HasIndex(row => row.UserId);
            entity.Property(row => row.PasswordHash).HasMaxLength(512);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<LocalIdentityRow>()
                .WithOne()
                .HasForeignKey<LocalCredentialRow>(row => row.LocalIdentityId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalIdentityRow>(entity =>
        {
            entity.ToTable("external_identities");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.Issuer, row.Subject }).IsUnique();
            entity.Property(row => row.Issuer).HasMaxLength(2048);
            entity.Property(row => row.Subject).HasMaxLength(512);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        OidcLoginRequestPersistenceContributor.Configure(
            modelBuilder.Entity<OidcLoginRequestRow>());

        modelBuilder.Entity<TenantMembershipRow>(entity =>
        {
            entity.ToTable("tenant_memberships", table =>
            {
                table.HasCheckConstraint(
                    "ck_tenant_memberships_role",
                    "\"role\" IN ('TenantOwner','TenantAdmin','Member','Viewer')");
                table.HasCheckConstraint(
                    "ck_tenant_memberships_status",
                    "\"status\" IN ('Invited','Active','Suspended','Removed')");
                table.HasCheckConstraint(
                    "ck_tenant_memberships_version",
                    "\"version\" >= 1");
            });
            entity.HasKey(row => new { row.TenantId, row.UserId });
            entity.HasIndex(row => row.UserId);
            entity.Property(row => row.Role).HasMaxLength(32);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuthSessionRow>(entity =>
        {
            entity.ToTable("auth_sessions", table =>
            {
                table.HasCheckConstraint(
                    "ck_auth_sessions_expiry",
                    "\"expires_at_utc\" > \"created_at_utc\"");
                table.HasCheckConstraint("ck_auth_sessions_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.Digest).IsUnique();
            entity.Property(row => row.Digest).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiKeyRow>(entity =>
        {
            entity.ToTable("api_keys", table =>
            {
                table.HasCheckConstraint("ck_api_keys_scopes", "\"scopes\" > 0 AND \"scopes\" <= 15");
                table.HasCheckConstraint("ck_api_keys_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => row.Prefix).IsUnique();
            entity.HasIndex(row => new { row.TenantId, row.OwnerId });
            entity.Property(row => row.Prefix).HasMaxLength(128);
            entity.Property(row => row.Digest).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RevokedTokenRow>(entity =>
        {
            entity.ToTable("revoked_tokens");
            entity.HasKey(row => new { row.Issuer, row.Jti });
            entity.HasIndex(row => row.ExpiresAtUtc);
            entity.Property(row => row.Issuer).HasMaxLength(2048);
            entity.Property(row => row.Jti).HasMaxLength(512);
            entity.Property(row => row.Reason).HasMaxLength(500);
        });
    }

    private static void ConfigureAssets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BlobRow>(entity =>
        {
            entity.ToTable("blobs", table =>
            {
                table.HasCheckConstraint("ck_blobs_size", "\"size_bytes\" > 0");
                table.HasCheckConstraint("ck_blobs_sha256", "length(\"sha256\") = 64");
                table.HasCheckConstraint(
                    "ck_blobs_state",
                    "\"state\" IN ('Staging','Quarantined','Active','Deleting','Deleted','Missing')");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.Sha256, row.SizeBytes }).IsUnique();
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.Provider,
                row.Container,
                row.ObjectKey,
                row.ProviderVersion,
            }).IsUnique();
            entity.Property(row => row.Provider).HasMaxLength(100);
            entity.Property(row => row.Container).HasMaxLength(255);
            entity.Property(row => row.ObjectKey).HasMaxLength(1024);
            entity.Property(row => row.ProviderVersion).HasMaxLength(512);
            entity.Property(row => row.Sha256).HasMaxLength(64);
            entity.Property(row => row.ProviderChecksum).HasMaxLength(512);
            entity.Property(row => row.ContentType).HasMaxLength(255);
            entity.Property(row => row.State).HasMaxLength(32);
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssetRow>(entity =>
        {
            entity.ToTable("assets", table =>
            {
                table.HasCheckConstraint(
                    "ck_assets_status",
                    "\"status\" IN ('Processing','Ready','Failed','Trashed','Purged')");
                table.HasCheckConstraint(
                    "ck_assets_visibility",
                    "\"visibility\" IN ('Private','Tenant','Public')");
                table.HasCheckConstraint("ck_assets_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.CreatedAtUtc, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.OwnerId });
            entity.Property(row => row.Title).HasMaxLength(500);
            entity.Property(row => row.Description).HasMaxLength(4000);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.Visibility).HasMaxLength(32);
            entity.Property(row => row.CapturedLocal).HasMaxLength(64);
            entity.Property(row => row.CapturePrecision).HasMaxLength(32);
            entity.Property(row => row.CaptureSource).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssetRevisionRow>(entity =>
        {
            entity.ToTable("asset_revisions", table =>
            {
                table.HasCheckConstraint("ck_asset_revisions_number", "\"revision_number\" > 0");
                table.HasCheckConstraint("ck_asset_revisions_dimensions", "\"width\" > 0 AND \"height\" > 0");
                table.HasCheckConstraint("ck_asset_revisions_frames", "\"frame_count\" > 0");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.AssetId, row.RevisionNumber }).IsUnique();
            entity.Property(row => row.DetectedFormat).HasMaxLength(100);
            entity.Property(row => row.DetectedContentType).HasMaxLength(255);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<BlobRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.BlobId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssetRow>()
            .HasOne<AssetRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.CurrentRevisionId })
            .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetMetadataHistoryRow>(entity =>
        {
            entity.ToTable("asset_metadata_history");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.TenantId, row.AssetId, row.ChangedAtUtc });
            entity.Property(row => row.Source).HasMaxLength(64);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.ActorUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureUploads(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UploadSessionRow>(entity =>
        {
            entity.ToTable("upload_sessions", table =>
            {
                table.HasCheckConstraint(
                    "ck_upload_sessions_strategy",
                    "\"strategy\" IN ('Proxy','Direct','Multipart')");
                table.HasCheckConstraint(
                    "ck_upload_sessions_state",
                    "\"state\" IN ('Pending','UploadIssued','Committing','CommitRequested','Verifying','Promoting','Accepted','Aborting','Aborted','Expired','Rejected','OutcomeUnknown','Reconciling')");
                table.HasCheckConstraint("ck_upload_sessions_expected_bytes", "\"expected_bytes\" > 0");
                table.HasCheckConstraint(
                    "ck_upload_sessions_multipart_part_plan_lifetime",
                    "\"multipart_part_plan_lifetime_ticks\" IS NULL OR \"multipart_part_plan_lifetime_ticks\" > 0");
                table.HasCheckConstraint("ck_upload_sessions_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.ActorId, row.State });
            entity.HasIndex(row => row.ExpiresAtUtc);
            entity.HasIndex(row => new { row.TenantId, row.IngestOperationId }).IsUnique();
            entity.Property(row => row.DisplayFileName).HasMaxLength(255);
            entity.Property(row => row.Strategy).HasMaxLength(32);
            entity.Property(row => row.StagingKey).HasMaxLength(1024);
            entity.Property(row => row.StorageProvider).HasMaxLength(100);
            entity.Property(row => row.StorageContainer).HasMaxLength(255);
            entity.Property(row => row.ProviderUploadId).HasMaxLength(1024);
            entity.Property(row => row.MultipartProviderState).HasMaxLength(8192);
            entity.Property(row => row.StagingProviderVersion).HasMaxLength(1024);
            entity.Property(row => row.StagingEntityTag).HasMaxLength(1024);
            entity.Property(row => row.StagingProviderChecksum).HasMaxLength(512);
            entity.Property(row => row.ExpectedSha256).HasMaxLength(64);
            entity.Property(row => row.DeclaredContentType).HasMaxLength(255);
            entity.Property(row => row.State).HasMaxLength(32);
            entity.Property(row => row.LastKnownState).HasMaxLength(32);
            entity.Property(row => row.CommitIdempotencyKey).HasMaxLength(128);
            entity.Property(row => row.CommitRequestHash).HasMaxLength(64);
            entity.Property(row => row.ReconciliationLeaseToken).HasMaxLength(128);
            entity.Property(row => row.RejectionCode).HasMaxLength(100);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.ActorId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ActivatedAssetId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AssetRevisionRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ActivatedRevisionId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BlobRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ActivatedBlobId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UploadPartRow>(entity =>
        {
            entity.ToTable("upload_parts", table =>
            {
                table.HasCheckConstraint("ck_upload_parts_number", "\"part_number\" > 0");
                table.HasCheckConstraint("ck_upload_parts_size", "\"size_bytes\" > 0");
            });
            entity.HasKey(row => new { row.TenantId, row.UploadSessionId, row.PartNumber });
            entity.Property(row => row.EntityTag).HasMaxLength(1024);
            entity.Property(row => row.Checksum).HasMaxLength(512);
            entity.HasOne<UploadSessionRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.UploadSessionId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuotaReservationRow>(entity =>
        {
            entity.ToTable("quota_reservations", table =>
            {
                table.HasCheckConstraint("ck_quota_reservations_uploads", "\"reserved_uploads\" >= 0");
                table.HasCheckConstraint("ck_quota_reservations_bytes", "\"reserved_bytes\" >= 0");
                table.HasCheckConstraint("ck_quota_reservations_objects", "\"reserved_objects\" >= 0");
                table.HasCheckConstraint("ck_quota_reservations_compute", "\"reserved_compute_units\" >= 0");
                table.HasCheckConstraint("ck_quota_reservations_jobs", "\"reserved_jobs\" >= 0");
                table.HasCheckConstraint("ck_quota_reservations_budget", "\"reserved_budget_units\" >= 0");
                table.HasCheckConstraint(
                    "ck_quota_reservations_nonzero",
                    "\"reserved_uploads\" > 0 OR \"reserved_bytes\" > 0 OR \"reserved_objects\" > 0 OR \"reserved_compute_units\" > 0 OR \"reserved_jobs\" > 0 OR \"reserved_budget_units\" > 0");
                table.HasCheckConstraint(
                    "ck_quota_reservations_state",
                    "\"state\" IN ('Reserved','Consumed','Released','Expired')");
                table.HasCheckConstraint("ck_quota_reservations_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => row.ExpiresAtUtc);
            entity.HasIndex(row => new { row.TenantId, row.UploadSessionId }).IsUnique();
            entity.HasIndex(row => new { row.TenantId, row.IdempotencyKey }).IsUnique();
            entity.Property(row => row.IdempotencyKey).HasMaxLength(200);
            entity.Property(row => row.RequestFingerprint).HasMaxLength(256);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<UploadSessionRow>()
                .WithOne()
                .HasForeignKey<QuotaReservationRow>(row => new { row.TenantId, row.UploadSessionId })
                .HasPrincipalKey<UploadSessionRow>(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<IngestOperationRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ConsumedByOperationId })
                .HasPrincipalKey(row => new
                {
                    row.TenantId,
                    OperationId = (Guid?)row.OperationId,
                })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QuotaUsageRow>(entity =>
        {
            entity.ToTable("quota_usage", table =>
            {
                table.HasCheckConstraint(
                    "ck_quota_usage_committed",
                    "\"committed_uploads\" >= 0 AND \"committed_bytes\" >= 0 AND \"committed_objects\" >= 0 AND \"committed_compute_units\" >= 0 AND \"committed_jobs\" >= 0 AND \"committed_budget_units\" >= 0");
                table.HasCheckConstraint(
                    "ck_quota_usage_reserved",
                    "\"reserved_uploads\" >= 0 AND \"reserved_bytes\" >= 0 AND \"reserved_objects\" >= 0 AND \"reserved_compute_units\" >= 0 AND \"reserved_jobs\" >= 0 AND \"reserved_budget_units\" >= 0");
                table.HasCheckConstraint("ck_quota_usage_version", "\"version\" >= 0");
            });
            entity.HasKey(row => row.TenantId);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithOne()
                .HasForeignKey<QuotaUsageRow>(row => row.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IdempotencyRequestRow>(entity =>
        {
            entity.ToTable("idempotency_requests");
            entity.HasKey(row => new { row.TenantId, row.PrincipalId, row.Key });
            entity.HasIndex(row => row.ExpiresAtUtc);
            entity.Property(row => row.Key).HasMaxLength(200);
            entity.Property(row => row.RequestHash).HasMaxLength(64);
            entity.Property(row => row.ResponseReference).HasMaxLength(1024);
            entity.HasOne<UploadSessionRow>()
                .WithOne()
                .HasForeignKey<IdempotencyRequestRow>(row => new { row.TenantId, row.UploadSessionId })
                .HasPrincipalKey<UploadSessionRow>(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UploadReconciliationCheckpointRow>(entity =>
        {
            entity.ToTable("upload_reconciliation_checkpoints");
            entity.HasKey(row => new { row.TenantId, row.RunId });
            entity.Property(row => row.Cursor).HasMaxLength(128);
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureIngest(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IngestOperationRow>(entity =>
        {
            entity.ToTable("ingest_operations", table =>
            {
                table.HasCheckConstraint(
                    "ck_ingest_operations_state",
                    "\"state\" IN ('Fenced','Planned','PromotionOutcomeUnknown','Activated','Rejected','CleanupCompleted')");
                table.HasCheckConstraint(
                    "ck_ingest_operations_promotion_mode",
                    "\"promotion_mode\" IS NULL OR \"promotion_mode\" IN ('PromoteCreateOnly','ExistingExactBlob')");
                table.HasCheckConstraint(
                    "ck_ingest_operations_fence",
                    "\"fenced_upload_version\" > 0");
                table.HasCheckConstraint("ck_ingest_operations_version", "\"version\" >= 1");
            });
            entity.HasKey(row => new { row.TenantId, row.OperationId });
            entity.HasIndex(row => new { row.TenantId, row.UploadSessionId }).IsUnique();
            entity.Property(row => row.State).HasMaxLength(32);
            entity.Property(row => row.PromotionMode).HasMaxLength(32);
            entity.Property(row => row.CanonicalKey).HasMaxLength(1024);
            entity.Property(row => row.StorageProvider).HasMaxLength(100);
            entity.Property(row => row.VerifiedSha256).HasMaxLength(64);
            entity.Property(row => row.DetectedFormat).HasMaxLength(100);
            entity.Property(row => row.DetectedContentType).HasMaxLength(255);
            entity.Property(row => row.RejectionCode).HasMaxLength(100);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<UploadSessionRow>()
                .WithOne()
                .HasForeignKey<IngestOperationRow>(
                    row => new { row.TenantId, row.UploadSessionId })
                .HasPrincipalKey<UploadSessionRow>(
                    row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AssetRevisionRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.RevisionId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BlobRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.BlobId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditEventRow>(entity =>
        {
            entity.ToTable("audit_events", table =>
            {
                table.HasCheckConstraint(
                    "ck_audit_events_actor_kind",
                    "\"actor_kind\" IN ('User','ApiKey','System')");
                table.HasCheckConstraint(
                    "ck_audit_events_outcome",
                    "\"outcome\" IN ('Succeeded','Rejected','Failed')");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.OccurredAtUtc,
                row.Id,
            });
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ResourceType,
                row.ResourceIdentifier,
            });
            entity.Property(row => row.ActorKind).HasMaxLength(32);
            entity.Property(row => row.ActorIdentifier).HasMaxLength(512);
            entity.Property(row => row.Action).HasMaxLength(200);
            entity.Property(row => row.ResourceType).HasMaxLength(100);
            entity.Property(row => row.ResourceIdentifier).HasMaxLength(512);
            entity.Property(row => row.BeforeJson).HasColumnType("text");
            entity.Property(row => row.AfterJson).HasColumnType("text");
            entity.Property(row => row.Outcome).HasMaxLength(32);
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private void ConfigureJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRow>(entity =>
        {
            entity.ToTable("jobs", table =>
            {
                table.HasCheckConstraint(
                    "ck_jobs_state",
                    "\"state\" IN ('Pending','Leased','RetryScheduled','Completed','DeadLettered')");
                table.HasCheckConstraint(
                    "ck_jobs_payload_version",
                    "\"payload_version\" >= 1");
                table.HasCheckConstraint(
                    "ck_jobs_attempts",
                    "\"attempts\" >= 0 AND \"attempts\" <= \"max_attempts\"");
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
            entity.Property(row => row.Type).HasMaxLength(
                Vistara.Domain.Jobs.JobType.MaximumLength);
            entity.Property(row => row.DedupeKey).HasMaxLength(
                Vistara.Domain.Jobs.JobDedupeKey.MaximumLength);
            entity.Property(row => row.State).HasMaxLength(32);
            entity.Property(row => row.LeaseOwner).HasMaxLength(
                Vistara.Domain.Jobs.JobLeaseOwner.MaximumLength);
            entity.Property(row => row.FailureCode).HasMaxLength(128);
            entity.Property(row => row.Payload).HasColumnType("text");
            entity.Property(row => row.TraceParent).HasMaxLength(512);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasQueryFilter(row => row.TenantId == TenantId);
        });
    }

    private static void ConfigureGallery(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlbumRow>(entity =>
        {
            entity.ToTable("albums", table =>
                table.HasCheckConstraint("ck_albums_version", "\"version\" >= 1"));
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.OwnerId });
            entity.Property(row => row.Name).HasMaxLength(500);
            entity.Property(row => row.Description).HasMaxLength(4000);
            entity.Property(row => row.SortMode).HasMaxLength(32);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CoverAssetId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AlbumItemRow>(entity =>
        {
            entity.ToTable("album_items", table =>
                table.HasCheckConstraint("ck_album_items_position", "\"position\" >= 0"));
            entity.HasKey(row => new { row.TenantId, row.AlbumId, row.AssetId });
            entity.HasIndex(row => new { row.TenantId, row.AlbumId, row.Position }).IsUnique();
            entity.HasOne<AlbumRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AlbumId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.AddedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TagRow>(entity =>
        {
            entity.ToTable("tags", table =>
                table.HasCheckConstraint("ck_tags_version", "\"version\" >= 1"));
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.NormalizedName }).IsUnique();
            entity.Property(row => row.NormalizedName).HasMaxLength(500);
            entity.Property(row => row.DisplayName).HasMaxLength(500);
            entity.Property(row => row.Color).HasMaxLength(64);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssetTagRow>(entity =>
        {
            entity.ToTable("asset_tags", table =>
                table.HasCheckConstraint(
                    "ck_asset_tags_source",
                    "\"source\" IN ('user','import','ai-accepted')"));
            entity.HasKey(row => new { row.TenantId, row.AssetId, row.TagId });
            entity.Property(row => row.Source).HasMaxLength(32);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TagRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.TagId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssetFavoriteRow>(entity =>
        {
            entity.ToTable("asset_favorites");
            entity.HasKey(row => new { row.TenantId, row.UserId, row.AssetId });
            entity.HasIndex(row => new { row.TenantId, row.AssetId });
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureSharing(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ResourceGrantRow>(entity =>
        {
            entity.ToTable("resource_grants", table =>
            {
                table.HasCheckConstraint(
                    "ck_resource_grants_resource_kind",
                    "\"resource_kind\" IN ('Album','Asset')");
                table.HasCheckConstraint(
                    "ck_resource_grants_grantee_kind",
                    "\"grantee_kind\" IN ('User','Group')");
                table.HasCheckConstraint(
                    "ck_resource_grants_role",
                    "\"role\" IN ('Viewer','Contributor','Curator')");
                table.HasCheckConstraint("ck_resource_grants_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new
            {
                row.TenantId,
                row.ResourceKind,
                row.ResourceId,
                row.GranteeKind,
                row.GranteeId,
            }).IsUnique();
            entity.Property(row => row.ResourceKind).HasMaxLength(32);
            entity.Property(row => row.GranteeKind).HasMaxLength(32);
            entity.Property(row => row.Role).HasMaxLength(32);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ShareRow>(entity =>
        {
            entity.ToTable("shares", table =>
            {
                table.HasCheckConstraint(
                    "ck_shares_target_kind",
                    "\"target_kind\" IN ('Album','Snapshot')");
                table.HasCheckConstraint(
                    "ck_shares_permissions",
                    "(\"permissions\" & 1) = 1 AND \"permissions\" >= 1 AND \"permissions\" <= 7");
                table.HasCheckConstraint("ck_shares_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => row.TokenHash).IsUnique();
            entity.Property(row => row.TokenHash).HasMaxLength(64);
            entity.Property(row => row.TargetKind).HasMaxLength(32);
            entity.Property(row => row.PasswordHash).HasMaxLength(1024);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<AlbumRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AlbumId })
                .HasPrincipalKey(row => new { row.TenantId, Id = (Guid?)row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ShareAssetRow>(entity =>
        {
            entity.ToTable("share_assets", table =>
                table.HasCheckConstraint("ck_share_assets_revision", "\"revision_number\" > 0"));
            entity.HasKey(row => new { row.TenantId, row.ShareId, row.AssetId });
            entity.HasOne<ShareRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ShareId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AssetRevisionRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.RevisionId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ShareSessionRow>(entity =>
        {
            entity.ToTable("share_sessions");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.Digest).IsUnique();
            entity.HasIndex(row => row.ExpiresAtUtc);
            entity.Property(row => row.Digest).HasMaxLength(64);
            entity.HasOne<ShareRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.ShareId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureLifecycle(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssetLifecycleRow>(entity =>
        {
            entity.ToTable("asset_lifecycles", table =>
            {
                table.HasCheckConstraint(
                    "ck_asset_lifecycles_state",
                    "\"state\" IN ('Ready','Trashed','Purging','Purged')");
                table.HasCheckConstraint("ck_asset_lifecycles_revision", "\"current_revision\" > 0");
                table.HasCheckConstraint("ck_asset_lifecycles_version", "\"version\" >= 1");
            });
            entity.HasKey(row => new { row.TenantId, row.AssetId });
            entity.Property(row => row.State).HasMaxLength(32);
            entity.Property(row => row.PurgeInitiatorKind).HasMaxLength(32);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<AssetRow>()
                .WithOne()
                .HasForeignKey<AssetLifecycleRow>(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey<AssetRow>(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TrashEntryRow>(entity =>
        {
            entity.ToTable("trash_entries", table =>
                table.HasCheckConstraint(
                    "ck_trash_entries_purge_at",
                    "\"purge_at_utc\" > \"deleted_at_utc\""));
            entity.HasKey(row => new { row.TenantId, row.AssetId });
            entity.HasIndex(row => new { row.TenantId, row.PurgeAtUtc });
            entity.Property(row => row.Reason).HasMaxLength(2000);
            entity.HasOne<AssetLifecycleRow>()
                .WithOne()
                .HasForeignKey<TrashEntryRow>(row => new { row.TenantId, row.AssetId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RetentionHoldRow>(entity =>
        {
            entity.ToTable("retention_holds", table =>
                table.HasCheckConstraint("ck_retention_holds_version", "\"version\" >= 1"));
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.TenantId, row.AssetId, row.ReleasedAtUtc });
            entity.Property(row => row.Reason).HasMaxLength(2000);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<AssetRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurgeBatchRow>(entity =>
        {
            entity.ToTable("purge_batches", table =>
            {
                table.HasCheckConstraint(
                    "ck_purge_batches_state",
                    "\"state\" IN ('Draft','DryRunCompleted','Approved','Executing','Completed','Cancelled')");
                table.HasCheckConstraint(
                    "ck_purge_batches_counts",
                    "\"candidate_count\" >= 0 AND \"eligible_count\" >= 0 AND \"eligible_count\" <= \"candidate_count\"");
                table.HasCheckConstraint("ck_purge_batches_version", "\"version\" >= 1");
            });
            entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.State, row.RequestedAtUtc });
            entity.Property(row => row.State).HasMaxLength(32);
            entity.Property(row => row.DryRunHash).HasMaxLength(128);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurgeBatchItemRow>(entity =>
        {
            entity.ToTable("purge_batch_items", table =>
            {
                table.HasCheckConstraint("ck_purge_batch_items_revision", "\"revision\" > 0");
                table.HasCheckConstraint("ck_purge_batch_items_bytes", "\"reclaimed_bytes\" >= 0");
                table.HasCheckConstraint(
                    "ck_purge_batch_items_result",
                    "\"result\" IN ('Purged','Blocked','Failed')");
            });
            entity.HasKey(row => new { row.TenantId, row.PurgeBatchId, row.AssetId });
            entity.Property(row => row.Result).HasMaxLength(32);
            entity.HasOne<PurgeBatchRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.PurgeBatchId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeletionTombstoneRow>(entity =>
        {
            entity.ToTable("deletion_tombstones", table =>
            {
                table.HasCheckConstraint(
                    "ck_deletion_tombstones_backup",
                    "\"backup_expires_at_utc\" >= \"purged_at_utc\"");
                table.HasCheckConstraint(
                    "ck_deletion_tombstones_relationships",
                    "\"relationship_count\" >= 0 AND length(\"relationship_digest\") = 64");
            });
            entity.HasKey(row => new { row.TenantId, row.FormerAssetId });
            entity.HasIndex(row => row.BackupExpiresAtUtc);
            entity.Property(row => row.RelationshipDigest).HasMaxLength(64);
            entity.HasOne<TenantRow>()
                .WithMany()
                .HasForeignKey(row => row.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RelationshipSnapshotRow>(entity =>
        {
            entity.ToTable("relationship_snapshots", table =>
                table.HasCheckConstraint(
                    "ck_relationship_snapshots_kind",
                    "\"kind\" IN ('Album','Tag','Favorite','Share','Grant')"));
            entity.HasKey(row => new { row.TenantId, row.AssetId, row.Kind, row.ResourceId });
            entity.Property(row => row.Kind).HasMaxLength(32);
            entity.HasOne<AssetLifecycleRow>()
                .WithMany()
                .HasForeignKey(row => new { row.TenantId, row.AssetId })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(type =>
                         type.FindProperty(nameof(ITenantOwnedRow.TenantId)) is not null))
        {
            ParameterExpression parameter = Expression.Parameter(entityType.ClrType, "row");
            MemberExpression tenantId = Expression.Property(
                parameter,
                nameof(ITenantOwnedRow.TenantId));
            MemberExpression currentTenant = Expression.Property(
                Expression.Constant(this),
                tenantId.Type == typeof(TenantKey)
                    ? nameof(CurrentTenantKey)
                    : nameof(TenantId));
            LambdaExpression filter = Expression.Lambda(
                Expression.Equal(tenantId, currentTenant),
                parameter);
            entityType.SetQueryFilter(filter);
        }
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

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entityType.GetProperties())
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
                else if (property.ClrType == typeof(TenantKey))
                {
                    property.SetValueConverter(new TenantKeyValueConverter());
                }
            }
        }
    }

    private void ValidateTenantWrites()
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry
                 in ChangeTracker.Entries()
                     .Where(entry => entry.State is
                         EntityState.Added or
                         EntityState.Modified or
                         EntityState.Deleted))
        {
            if (entry.State == EntityState.Added)
            {
                ValidateUuid7PrimaryKey(entry);
            }

            if (entry.Entity is ITenantOwnedRow row)
            {
                if (row.TenantId.Value != TenantId)
                {
                    throw new InvalidOperationException(
                        "Tenant-owned rows can only be changed inside their tenant scope.");
                }

                if (entry.State == EntityState.Modified &&
                    entry.Property(nameof(ITenantOwnedRow.TenantId)).IsModified)
                {
                    throw new InvalidOperationException("Tenant ownership cannot be changed.");
                }
            }
            else if (TryGetTenantId(entry.Entity, out Guid tenantId) &&
                     tenantId != TenantId)
            {
                throw new InvalidOperationException(
                    "Tenant-owned rows can only be changed inside their tenant scope.");
            }
        }
    }

    private static bool TryGetTenantId(object entity, out Guid tenantId)
    {
        tenantId = entity switch
        {
            JobRow row => row.TenantId,
            OutboxMessageRow row => row.TenantId,
            EventLogRow row => row.TenantId,
            OutboxSequenceRow row => row.TenantId,
            SharingShareRow row => row.TenantId,
            SharingSessionRow row => row.TenantId,
            _ => Guid.Empty,
        };
        return tenantId != Guid.Empty;
    }

    private static void ValidateUuid7PrimaryKey(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        IKey? primaryKey = entry.Metadata.FindPrimaryKey();
        if (primaryKey is null)
        {
            return;
        }

        foreach (IProperty property in primaryKey.Properties.Where(
                     property => property.ClrType == typeof(Guid)))
        {
            Guid value = (Guid)(entry.Property(property.Name).CurrentValue ?? Guid.Empty);
            if (value == Guid.Empty || value.Version != 7)
            {
                throw new InvalidOperationException(
                    $"Primary key '{property.Name}' must be a non-empty UUIDv7 value.");
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
