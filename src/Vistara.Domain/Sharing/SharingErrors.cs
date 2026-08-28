using Vistara.Domain.Common;

namespace Vistara.Domain.Sharing;

internal static class SharingErrors
{
    public static ResultError InvalidIdentifier() =>
        ResultError.Validation("sharing.identifier_invalid", "The identifier must not be empty.");

    public static ResultError TokenHashInvalid() =>
        ResultError.Validation("sharing.token_hash_invalid", "The share token hash must be a SHA-256 hex digest.");

    public static ResultError TimestampMustBeUtc() =>
        ResultError.Validation("sharing.timestamp_not_utc", "The timestamp must be UTC.");

    public static ResultError ExpiryInvalid() =>
        ResultError.Validation("sharing.expiry_invalid", "The expiry must be UTC and later than creation.");

    public static ResultError ViewPermissionRequired() =>
        ResultError.Validation("sharing.view_permission_required", "A share must allow viewing.");

    public static ResultError PermissionsInvalid() =>
        ResultError.Validation("sharing.permissions_invalid", "The share permissions are invalid.");

    public static ResultError ResourceKindInvalid() =>
        ResultError.Validation("sharing.resource_kind_invalid", "The resource kind is invalid.");

    public static ResultError GranteeKindInvalid() =>
        ResultError.Validation("sharing.grantee_kind_invalid", "The grantee kind is invalid.");

    public static ResultError GrantRoleInvalid() =>
        ResultError.Validation("sharing.grant_role_invalid", "The grant role is invalid.");

    public static ResultError ShareExpired() =>
        ResultError.Unavailable("sharing.share_expired", "The share has expired.");

    public static ResultError ShareRevoked() =>
        ResultError.Unavailable("sharing.share_revoked", "The share has been revoked.");

    public static ResultError ShareUnavailable() =>
        ResultError.Unavailable("sharing.share_unavailable", "The share is unavailable.");

    public static ResultError VersionConflict() =>
        ResultError.Conflict("sharing.version_conflict", "The sharing resource version has changed.");

    public static ResultError TargetIsNotSnapshot() =>
        ResultError.Conflict("sharing.target_not_snapshot", "Assets can only be added to a snapshot share.");

    public static ResultError CrossTenantReference() =>
        ResultError.Forbidden("sharing.cross_tenant_reference", "The referenced resource belongs to another tenant.");

    public static ResultError DuplicateSnapshotAsset() =>
        ResultError.Conflict("sharing.snapshot_asset_duplicate", "The asset is already in the share snapshot.");
}

internal static class SharingTime
{
    public static bool IsUtc(DateTimeOffset timestamp) => timestamp.Offset == TimeSpan.Zero;
}
