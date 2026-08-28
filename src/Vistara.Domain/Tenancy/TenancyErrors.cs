using Vistara.Domain.Common;

namespace Vistara.Domain.Tenancy;

public static class TenancyErrors
{
    public static readonly ResultError InvalidSlug = ResultError.Validation(
        "tenancy.invalid_slug",
        "The tenant slug is invalid.");

    public static readonly ResultError InvalidName = ResultError.Validation(
        "tenancy.invalid_name",
        "The tenant name is invalid.");

    public static readonly ResultError InvalidRole = ResultError.Validation(
        "tenancy.invalid_role",
        "The tenant role is invalid.");

    public static readonly ResultError StatusUnchanged = ResultError.Conflict(
        "tenancy.status_unchanged",
        "The tenant already has the requested status.");

    public static readonly ResultError InvalidStatusTransition = ResultError.Conflict(
        "tenancy.invalid_status_transition",
        "The tenant status transition is not allowed.");

    public static readonly ResultError RoleUnchanged = ResultError.Conflict(
        "tenancy.role_unchanged",
        "The membership already has the requested role.");

    public static readonly ResultError MembershipRemoved = ResultError.Conflict(
        "tenancy.membership_removed",
        "A removed membership cannot be changed.");

    public static readonly ResultError InvalidMembershipTransition = ResultError.Conflict(
        "tenancy.invalid_membership_transition",
        "The membership status transition is not allowed.");

    public static readonly ResultError TimestampNotUtc = ResultError.Validation(
        "common.timestamp_not_utc",
        "Timestamps must use UTC.");

    public static readonly ResultError TimestampOutOfOrder = ResultError.Conflict(
        "common.timestamp_out_of_order",
        "The timestamp precedes the aggregate's latest change.");
}
