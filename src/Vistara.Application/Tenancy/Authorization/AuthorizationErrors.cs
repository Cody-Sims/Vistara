using Vistara.Domain.Common;

namespace Vistara.Application.Tenancy.Authorization;

public static class AuthorizationErrors
{
    public static readonly ResultError InactiveMembership = ResultError.Unauthorized(
        "authorization.inactive_membership",
        "An active tenant membership is required.");

    public static readonly ResultError MembershipPrincipalMismatch = ResultError.Unauthorized(
        "authorization.membership_principal_mismatch",
        "The authenticated principal does not match the tenant membership.");

    public static readonly ResultError Forbidden = ResultError.Forbidden(
        "authorization.forbidden",
        "The actor is not permitted to perform this operation.");

    public static readonly ResultError ResourceNotFound = ResultError.NotFound(
        "authorization.resource_not_found",
        "The requested resource was not found.");
}
