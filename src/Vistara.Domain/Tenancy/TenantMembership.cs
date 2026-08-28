using Vistara.Domain.Common;
using Vistara.Domain.Identity;

namespace Vistara.Domain.Tenancy;

public sealed class TenantMembership
{
    private TenantMembership(
        TenantId tenantId,
        UserId userId,
        TenantRole role,
        DateTimeOffset invitedAt)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
        Status = MembershipStatus.Invited;
        InvitedAt = invitedAt;
        UpdatedAt = invitedAt;
        Version = 1;
    }

    public TenantId TenantId { get; }

    public UserId UserId { get; }

    public TenantRole Role { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset InvitedAt { get; }

    public DateTimeOffset? JoinedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Result<TenantMembership> Invite(
        TenantId tenantId,
        UserId userId,
        TenantRole role,
        DateTimeOffset invitedAt)
    {
        if (!Enum.IsDefined(role))
        {
            return Result.Failure<TenantMembership>(TenancyErrors.InvalidRole);
        }

        if (invitedAt.Offset != TimeSpan.Zero)
        {
            return Result.Failure<TenantMembership>(TenancyErrors.TimestampNotUtc);
        }

        return Result.Success(new TenantMembership(tenantId, userId, role, invitedAt));
    }

    public bool IsForTenant(TenantId tenantId) => TenantId == tenantId;

    public Result ChangeRole(TenantRole role, DateTimeOffset changedAt)
    {
        if (!Enum.IsDefined(role))
        {
            return Result.Failure(TenancyErrors.InvalidRole);
        }

        Result changeResult = CanChange(changedAt);
        if (changeResult.IsFailure)
        {
            return changeResult;
        }

        if (Role == role)
        {
            return Result.Failure(TenancyErrors.RoleUnchanged);
        }

        Role = role;
        MarkChanged(changedAt);
        return Result.Success();
    }

    public Result Activate(DateTimeOffset changedAt)
    {
        Result changeResult = CanChange(changedAt);
        if (changeResult.IsFailure)
        {
            return changeResult;
        }

        if (Status == MembershipStatus.Active)
        {
            return Result.Failure(TenancyErrors.InvalidMembershipTransition);
        }

        if (Status is not (MembershipStatus.Invited or MembershipStatus.Suspended))
        {
            return Result.Failure(TenancyErrors.InvalidMembershipTransition);
        }

        Status = MembershipStatus.Active;
        JoinedAt ??= changedAt;
        MarkChanged(changedAt);
        return Result.Success();
    }

    public Result Suspend(DateTimeOffset changedAt) =>
        TransitionTo(MembershipStatus.Suspended, MembershipStatus.Active, changedAt);

    public Result Remove(DateTimeOffset changedAt)
    {
        Result changeResult = ValidateTimestamp(changedAt);
        if (changeResult.IsFailure)
        {
            return changeResult;
        }

        if (Status == MembershipStatus.Removed)
        {
            return Result.Failure(TenancyErrors.InvalidMembershipTransition);
        }

        Status = MembershipStatus.Removed;
        MarkChanged(changedAt);
        return Result.Success();
    }

    private Result TransitionTo(
        MembershipStatus target,
        MembershipStatus requiredCurrent,
        DateTimeOffset changedAt)
    {
        Result changeResult = CanChange(changedAt);
        if (changeResult.IsFailure)
        {
            return changeResult;
        }

        if (Status != requiredCurrent || Status == target)
        {
            return Result.Failure(TenancyErrors.InvalidMembershipTransition);
        }

        Status = target;
        MarkChanged(changedAt);
        return Result.Success();
    }

    private Result CanChange(DateTimeOffset changedAt)
    {
        Result timestampResult = ValidateTimestamp(changedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        return Status == MembershipStatus.Removed
            ? Result.Failure(TenancyErrors.MembershipRemoved)
            : Result.Success();
    }

    private Result ValidateTimestamp(DateTimeOffset changedAt)
    {
        if (changedAt.Offset != TimeSpan.Zero)
        {
            return Result.Failure(TenancyErrors.TimestampNotUtc);
        }

        return changedAt < UpdatedAt
            ? Result.Failure(TenancyErrors.TimestampOutOfOrder)
            : Result.Success();
    }

    private void MarkChanged(DateTimeOffset changedAt)
    {
        UpdatedAt = changedAt;
        Version++;
    }
}
