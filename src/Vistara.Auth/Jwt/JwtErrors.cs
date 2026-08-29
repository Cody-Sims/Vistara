using Vistara.Domain.Common;

namespace Vistara.Auth.Jwt;

public static class JwtErrors
{
    public static readonly ResultError InvalidToken = ResultError.Unauthorized(
        "jwt.invalid_token",
        "The bearer token is invalid.");

    public static readonly ResultError Revoked = ResultError.Unauthorized(
        "jwt.revoked",
        "The bearer token has been revoked.");

    public static readonly ResultError TenantAccessDenied = ResultError.Forbidden(
        "jwt.tenant_access_denied",
        "The bearer token does not grant access to an active tenant membership.");

    public static readonly ResultError ValidationUnavailable = ResultError.Unavailable(
        "jwt.validation_unavailable",
        "Bearer token validation is temporarily unavailable.");
}
