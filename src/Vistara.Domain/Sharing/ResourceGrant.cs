using Vistara.Domain.Common;

namespace Vistara.Domain.Sharing;

public enum ResourceKind
{
    Album,
    Asset,
}

public enum GranteeKind
{
    User,
    Group,
}

public enum GrantRole
{
    Viewer,
    Contributor,
    Curator,
}

public sealed record GrantResourceRef(
    SharingTenantId TenantId,
    ResourceKind Kind,
    Guid ResourceId);

public sealed record GranteeRef(
    GranteeKind Kind,
    Guid GranteeId);

public sealed class ResourceGrant
{
    private ResourceGrant(
        ResourceGrantId id,
        SharingTenantId tenantId,
        GrantResourceRef resource,
        GranteeRef grantee,
        GrantRole role,
        SharingUserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        Resource = resource;
        Grantee = grantee;
        Role = role;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public ResourceGrantId Id { get; }

    public SharingTenantId TenantId { get; }

    public GrantResourceRef Resource { get; }

    public GranteeRef Grantee { get; }

    public GrantRole Role { get; private set; }

    public SharingUserId CreatedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public SharingUserId? RevokedBy { get; private set; }

    public long Version { get; private set; }

    public static Result<ResourceGrant> Create(
        ResourceGrantId id,
        SharingTenantId tenantId,
        GrantResourceRef resource,
        GranteeRef grantee,
        GrantRole role,
        SharingUserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        if (id.Value == Guid.Empty ||
            tenantId.Value == Guid.Empty ||
            !SharingIdGuard.IsUuid7(resource.ResourceId) ||
            !SharingIdGuard.IsUuid7(grantee.GranteeId) ||
            createdBy.Value == Guid.Empty)
        {
            return Result.Failure<ResourceGrant>(SharingErrors.InvalidIdentifier());
        }

        if (!Enum.IsDefined(resource.Kind))
        {
            return Result.Failure<ResourceGrant>(SharingErrors.ResourceKindInvalid());
        }

        if (!Enum.IsDefined(grantee.Kind))
        {
            return Result.Failure<ResourceGrant>(SharingErrors.GranteeKindInvalid());
        }

        if (!Enum.IsDefined(role))
        {
            return Result.Failure<ResourceGrant>(SharingErrors.GrantRoleInvalid());
        }

        if (resource.TenantId != tenantId)
        {
            return Result.Failure<ResourceGrant>(SharingErrors.CrossTenantReference());
        }

        if (!SharingTime.IsUtc(createdAtUtc))
        {
            return Result.Failure<ResourceGrant>(SharingErrors.TimestampMustBeUtc());
        }

        return Result.Success(new ResourceGrant(
            id,
            tenantId,
            resource,
            grantee,
            role,
            createdBy,
            createdAtUtc));
    }

    public Result ChangeRole(GrantRole role, long expectedVersion)
    {
        if (!Enum.IsDefined(role))
        {
            return Result.Failure(SharingErrors.GrantRoleInvalid());
        }

        if (expectedVersion != Version)
        {
            return Result.Failure(SharingErrors.VersionConflict());
        }

        if (Role == role)
        {
            return Result.Success();
        }

        Role = role;
        Version++;
        return Result.Success();
    }

    public Result Revoke(
        SharingUserId revokedBy,
        DateTimeOffset revokedAtUtc,
        long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            return Result.Failure(SharingErrors.VersionConflict());
        }

        if (!SharingTime.IsUtc(revokedAtUtc))
        {
            return Result.Failure(SharingErrors.TimestampMustBeUtc());
        }

        if (revokedBy.Value == Guid.Empty)
        {
            return Result.Failure(SharingErrors.InvalidIdentifier());
        }

        if (RevokedAtUtc.HasValue)
        {
            return Result.Success();
        }

        RevokedBy = revokedBy;
        RevokedAtUtc = revokedAtUtc;
        Version++;
        return Result.Success();
    }
}
