namespace Vistara.Persistence.Model;

public interface ITenantOwnedRow
{
    TenantKey TenantId { get; set; }
}

public sealed class TenantRow : ITenantOwnedRow
{
    public TenantKey Id { get; set; }
    public TenantKey TenantId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = "{}";
    public string QuotasJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class UserRow
{
    public Guid Id { get; set; }
    public string NormalizedEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class LocalIdentityRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string NormalizedLogin { get; set; } = string.Empty;
    public DateTimeOffset LinkedAtUtc { get; set; }
}

/// <summary>
/// A database-enforced singleton marker proving that first-owner provisioning
/// has already produced a winner. The primary key is pinned to
/// <see cref="SingletonId"/> so concurrent bootstrap attempts with distinct
/// slugs and emails still collide on one unique key.
/// </summary>
public sealed class PlatformBootstrapRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public Guid OwnerTenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public DateTimeOffset ProvisionedAtUtc { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// Account-level presentation preferences. They follow the user across every
/// tenant, so the table carries no tenant column and no row-level security.
/// </summary>
public sealed class UserPreferenceRow
{
    public Guid UserId { get; set; }
    public string Density { get; set; } = "comfortable";
    public bool ReducedMotion { get; set; }
    public bool ScreenReaderPagedMode { get; set; }
    public string? Locale { get; set; }
    public string? TimeZone { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class LocalCredentialRow
{
    public Guid LocalIdentityId { get; set; }
    public Guid UserId { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class ExternalIdentityRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset LinkedAtUtc { get; set; }
}

/// <summary>
/// One in-flight browser OIDC authorization request. The row exists between the
/// redirect to the identity provider and the callback, so it is deliberately
/// tenant-independent: no tenant scope exists before the external identity is
/// resolved. <c>state</c>, <c>nonce</c>, and the browser handle are only ever
/// held as SHA-256 digests, and <see cref="ConsumedAtUtc"/> is the
/// conditional-update target that makes the row single use.
/// </summary>
public sealed class OidcLoginRequestRow
{
    public byte[] StateDigest { get; set; } = [];
    public string ProviderId { get; set; } = string.Empty;
    public byte[] NonceDigest { get; set; } = [];
    public byte[] HandleDigest { get; set; } = [];
    public string CodeVerifier { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string ReturnTo { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}

public sealed class TenantMembershipRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset InvitedAtUtc { get; set; }
    public DateTimeOffset? JoinedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class AuthSessionRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Digest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class ApiKeyRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid OwnerId { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
    public int Scopes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class RevokedTokenRow
{
    public string Issuer { get; set; } = string.Empty;
    public string Jti { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string? Reason { get; set; }
}

public sealed class BlobRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string? ProviderVersion { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string? ProviderChecksum { get; set; }
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string State { get; set; } = "Active";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AssetRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? CurrentRevisionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public DateTimeOffset? CapturedAtUtc { get; set; }
    public string? CapturedLocal { get; set; }
    public int? CapturedOffsetMinutes { get; set; }
    public string? CapturePrecision { get; set; }
    public string? CaptureSource { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class AssetRevisionRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid AssetId { get; set; }
    public long RevisionNumber { get; set; }
    public Guid BlobId { get; set; }
    public string DetectedFormat { get; set; } = string.Empty;
    public string DetectedContentType { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameCount { get; set; }
    public string SafeMetadataJson { get; set; } = "{}";
    public string PrivateMetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AssetMetadataHistoryRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ChangesJson { get; set; } = "{}";
    public DateTimeOffset ChangedAtUtc { get; set; }
}

public sealed class UploadSessionRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string DisplayFileName { get; set; } = "upload";
    public string Strategy { get; set; } = string.Empty;
    public string StagingKey { get; set; } = string.Empty;
    public string? StorageProvider { get; set; }
    public string? StorageContainer { get; set; }
    public string? ProviderUploadId { get; set; }
    public string? MultipartProviderState { get; set; }
    public string? StagingProviderVersion { get; set; }
    public string? StagingEntityTag { get; set; }
    public string? StagingProviderChecksum { get; set; }
    public DateTimeOffset? MultipartExpiresAtUtc { get; set; }
    public long? MultipartPartPlanLifetimeTicks { get; set; }
    public int? MultipartMaxParts { get; set; }
    public long? MultipartMinPartBytes { get; set; }
    public long? MultipartMaxPartBytes { get; set; }
    public long ExpectedBytes { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string DeclaredContentType { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? LastKnownState { get; set; }
    public string? CommitIdempotencyKey { get; set; }
    public string? CommitRequestHash { get; set; }
    public string? ReconciliationLeaseToken { get; set; }
    public DateTimeOffset? ReconciliationLeaseExpiresAtUtc { get; set; }
    public Guid? IngestOperationId { get; set; }
    public Guid? ActivatedAssetId { get; set; }
    public Guid? ActivatedRevisionId { get; set; }
    public Guid? ActivatedBlobId { get; set; }
    public string? RejectionCode { get; set; }
    public DateTimeOffset? RejectedAtUtc { get; set; }
    public DateTimeOffset? CleanupCompletedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class UploadPartRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid UploadSessionId { get; set; }
    public int PartNumber { get; set; }
    public string EntityTag { get; set; } = string.Empty;
    public string? Checksum { get; set; }
    public long SizeBytes { get; set; }
}

public sealed class QuotaReservationRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid? UploadSessionId { get; set; }
    public Guid? JobId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public long ReservedUploads { get; set; }
    public long ReservedBytes { get; set; }
    public long ReservedObjects { get; set; }
    public long ReservedComputeUnits { get; set; }
    public long ReservedJobs { get; set; }
    public long ReservedBudgetUnits { get; set; }
    public string State { get; set; } = string.Empty;
    public Guid? ConsumedByOperationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class IdempotencyRequestRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid PrincipalId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public Guid? UploadSessionId { get; set; }
    public string? ResponseReference { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class AlbumRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? CoverAssetId { get; set; }
    public string SortMode { get; set; } = "Manual";
    public long Version { get; set; }
}

public sealed class AlbumItemRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid AlbumId { get; set; }
    public Guid AssetId { get; set; }
    public long Position { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}

public sealed class TagRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public string NormalizedName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Color { get; set; }
    public long Version { get; set; }
}

public sealed class AssetTagRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid TagId { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class AssetFavoriteRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid AssetId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}

public sealed class ResourceGrantRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public string ResourceKind { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public string GranteeKind { get; set; } = string.Empty;
    public Guid GranteeId { get; set; }
    public string Role { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public long Version { get; set; }
}

public sealed class ShareRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public Guid? AlbumId { get; set; }
    public string? SnapshotJson { get; set; }
    public string? PasswordHash { get; set; }
    public int Permissions { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public long Version { get; set; }
}

public sealed class ShareAssetRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid ShareId { get; set; }
    public Guid AssetId { get; set; }
    public Guid RevisionId { get; set; }
    public long RevisionNumber { get; set; }
}

public sealed class ShareSessionRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid ShareId { get; set; }
    public string Digest { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class AssetLifecycleRow : ITenantOwnedRow
{
    public Guid AssetId { get; set; }
    public TenantKey TenantId { get; set; }
    public long CurrentRevision { get; set; }
    public string State { get; set; } = string.Empty;
    public bool HasBeenTrashed { get; set; }
    public Guid? ActivePurgeBatchId { get; set; }
    public Guid? PurgeRequestedByUserId { get; set; }
    public string? PurgeInitiatorKind { get; set; }
    public DateTimeOffset? PurgeEvaluatedAtUtc { get; set; }
    public long? PurgeObservedRevision { get; set; }
    public bool? PurgeHasBlockingReferences { get; set; }
    public DateTimeOffset? LastRestoredAtUtc { get; set; }
    public Guid? LastRestoredByUserId { get; set; }
    public long Version { get; set; }
}

public sealed class TrashEntryRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid DeletedByUserId { get; set; }
    public DateTimeOffset DeletedAtUtc { get; set; }
    public DateTimeOffset PurgeAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RestorationMetadataJson { get; set; }
}

public sealed class RetentionHoldRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid AssetId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReleasedByUserId { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class PurgeBatchRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public string? DryRunHash { get; set; }
    public DateTimeOffset? DryRunCompletedAtUtc { get; set; }
    public int CandidateCount { get; set; }
    public int EligibleCount { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string State { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class PurgeBatchItemRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid PurgeBatchId { get; set; }
    public Guid AssetId { get; set; }
    public long Revision { get; set; }
    public string Result { get; set; } = string.Empty;
    public long ReclaimedBytes { get; set; }
}

public sealed class DeletionTombstoneRow : ITenantOwnedRow
{
    public Guid FormerAssetId { get; set; }
    public TenantKey TenantId { get; set; }
    public DateTimeOffset PurgedAtUtc { get; set; }
    public DateTimeOffset BackupExpiresAtUtc { get; set; }
    public int RelationshipCount { get; set; }
    public string RelationshipDigest { get; set; } = string.Empty;
}

public sealed class RelationshipSnapshotRow : ITenantOwnedRow
{
    public TenantKey TenantId { get; set; }
    public Guid AssetId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
}
