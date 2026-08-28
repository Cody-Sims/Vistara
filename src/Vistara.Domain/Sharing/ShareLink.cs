using Vistara.Domain.Common;

namespace Vistara.Domain.Sharing;

[Flags]
public enum SharePermissions
{
    None = 0,
    View = 1,
    DownloadRenditions = 2,
    DownloadOriginal = 4,
}

public enum ShareVisibility
{
    Active,
    Expired,
    Revoked,
}

public enum ShareTargetKind
{
    Album,
    Snapshot,
}

public sealed record ShareTarget
{
    private ShareTarget(ShareTargetKind kind, SharingAlbumId? albumId)
    {
        Kind = kind;
        AlbumId = albumId;
    }

    public ShareTargetKind Kind { get; }

    public SharingAlbumId? AlbumId { get; }

    public static ShareTarget Album(SharingAlbumId albumId)
    {
        if (albumId.Value == Guid.Empty)
        {
            throw new ArgumentException("Album identifier cannot be empty.", nameof(albumId));
        }

        return new ShareTarget(ShareTargetKind.Album, albumId);
    }

    public static ShareTarget Snapshot() => new(ShareTargetKind.Snapshot, null);
}

public sealed record SharedAssetRef
{
    public SharedAssetRef(SharingTenantId tenantId, SharedAssetId assetId, long revision)
    {
        if (tenantId.Value == Guid.Empty || assetId.Value == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("Shared asset metadata is invalid.");
        }

        TenantId = tenantId;
        AssetId = assetId;
        Revision = revision;
    }

    public SharingTenantId TenantId { get; }

    public SharedAssetId AssetId { get; }

    public long Revision { get; }
}

public sealed class ShareLink
{
    private const SharePermissions AllPermissions =
        SharePermissions.View |
        SharePermissions.DownloadRenditions |
        SharePermissions.DownloadOriginal;

    private readonly List<SharedAssetRef> _snapshotAssets = [];

    private ShareLink(
        ShareId id,
        SharingTenantId tenantId,
        SharingUserId createdBy,
        ShareTokenHash tokenHash,
        ShareTarget target,
        SharePermissions permissions,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc,
        string? passwordHash)
    {
        Id = id;
        TenantId = tenantId;
        CreatedBy = createdBy;
        TokenHash = tokenHash;
        Target = target;
        Permissions = permissions;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        PasswordHash = passwordHash;
        Version = 1;
    }

    public ShareId Id { get; }

    public SharingTenantId TenantId { get; }

    public SharingUserId CreatedBy { get; }

    public ShareTokenHash TokenHash { get; }

    public ShareTarget Target { get; }

    public SharePermissions Permissions { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public string? PasswordHash { get; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public SharingUserId? RevokedBy { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<SharedAssetRef> SnapshotAssets => _snapshotAssets.AsReadOnly();

    public static Result<ShareLink> Create(
        ShareId id,
        SharingTenantId tenantId,
        SharingUserId createdBy,
        ShareTokenHash tokenHash,
        ShareTarget target,
        SharePermissions permissions,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc = null,
        string? passwordHash = null)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentNullException.ThrowIfNull(target);

        if (id.Value == Guid.Empty || tenantId.Value == Guid.Empty || createdBy.Value == Guid.Empty)
        {
            return Result.Failure<ShareLink>(SharingErrors.InvalidIdentifier());
        }

        if (!SharingTime.IsUtc(createdAtUtc) ||
            (expiresAtUtc.HasValue && !SharingTime.IsUtc(expiresAtUtc.Value)))
        {
            return Result.Failure<ShareLink>(SharingErrors.TimestampMustBeUtc());
        }

        if (expiresAtUtc <= createdAtUtc)
        {
            return Result.Failure<ShareLink>(SharingErrors.ExpiryInvalid());
        }

        Result permissionValidation = ValidatePermissions(permissions);
        if (permissionValidation.IsFailure)
        {
            return Result.Failure<ShareLink>(permissionValidation.Error!);
        }

        return Result.Success(new ShareLink(
            id,
            tenantId,
            createdBy,
            tokenHash,
            target,
            permissions,
            createdAtUtc,
            expiresAtUtc,
            passwordHash));
    }

    public ShareVisibility VisibilityAt(DateTimeOffset nowUtc)
    {
        if (RevokedAtUtc.HasValue)
        {
            return ShareVisibility.Revoked;
        }

        return ExpiresAtUtc.HasValue && nowUtc >= ExpiresAtUtc.Value
            ? ShareVisibility.Expired
            : ShareVisibility.Active;
    }

    public Result Authorize(DateTimeOffset nowUtc)
    {
        if (!SharingTime.IsUtc(nowUtc))
        {
            return Result.Failure(SharingErrors.TimestampMustBeUtc());
        }

        return VisibilityAt(nowUtc) switch
        {
            ShareVisibility.Active => Result.Success(),
            ShareVisibility.Expired => Result.Failure(SharingErrors.ShareExpired()),
            ShareVisibility.Revoked => Result.Failure(SharingErrors.ShareRevoked()),
            _ => Result.Failure(SharingErrors.ShareUnavailable()),
        };
    }

    public Result Revoke(
        SharingUserId revokedBy,
        DateTimeOffset revokedAtUtc,
        long expectedVersion)
    {
        Result validation = ValidateMutation(revokedAtUtc, expectedVersion);
        if (validation.IsFailure)
        {
            return validation;
        }

        if (RevokedAtUtc.HasValue)
        {
            return Result.Success();
        }

        if (revokedBy.Value == Guid.Empty)
        {
            return Result.Failure(SharingErrors.InvalidIdentifier());
        }

        RevokedBy = revokedBy;
        RevokedAtUtc = revokedAtUtc;
        Version++;
        return Result.Success();
    }

    public Result ChangeAccess(
        SharePermissions permissions,
        DateTimeOffset? expiresAtUtc,
        long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            return Result.Failure(SharingErrors.VersionConflict());
        }

        Result permissionValidation = ValidatePermissions(permissions);
        if (permissionValidation.IsFailure)
        {
            return permissionValidation;
        }

        if (expiresAtUtc.HasValue &&
            (!SharingTime.IsUtc(expiresAtUtc.Value) || expiresAtUtc.Value <= CreatedAtUtc))
        {
            return Result.Failure(SharingErrors.ExpiryInvalid());
        }

        if (Permissions == permissions && ExpiresAtUtc == expiresAtUtc)
        {
            return Result.Success();
        }

        Permissions = permissions;
        ExpiresAtUtc = expiresAtUtc;
        Version++;
        return Result.Success();
    }

    public Result AddSnapshotAsset(SharedAssetRef asset, long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            return Result.Failure(SharingErrors.VersionConflict());
        }

        if (Target.Kind != ShareTargetKind.Snapshot)
        {
            return Result.Failure(SharingErrors.TargetIsNotSnapshot());
        }

        if (asset.TenantId != TenantId)
        {
            return Result.Failure(SharingErrors.CrossTenantReference());
        }

        if (_snapshotAssets.Any(candidate => candidate.AssetId == asset.AssetId))
        {
            return Result.Failure(SharingErrors.DuplicateSnapshotAsset());
        }

        _snapshotAssets.Add(asset);
        Version++;
        return Result.Success();
    }

    private Result ValidateMutation(DateTimeOffset timestampUtc, long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            return Result.Failure(SharingErrors.VersionConflict());
        }

        return SharingTime.IsUtc(timestampUtc)
            ? Result.Success()
            : Result.Failure(SharingErrors.TimestampMustBeUtc());
    }

    private static Result ValidatePermissions(SharePermissions permissions)
    {
        if ((permissions & ~AllPermissions) != SharePermissions.None)
        {
            return Result.Failure(SharingErrors.PermissionsInvalid());
        }

        return (permissions & SharePermissions.View) == SharePermissions.View
            ? Result.Success()
            : Result.Failure(SharingErrors.ViewPermissionRequired());
    }
}
