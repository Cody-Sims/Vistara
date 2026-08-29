using Vistara.Domain.Common;

namespace Vistara.Auth.ApiKeys;

public static class ApiKeyErrors
{
    public static readonly ResultError InvalidCredentials = ResultError.Unauthorized(
        "api_keys.invalid_credentials",
        "The API key is invalid.");

    public static readonly ResultError Expired = ResultError.Unauthorized(
        "api_keys.expired",
        "The API key has expired.");

    public static readonly ResultError Revoked = ResultError.Unauthorized(
        "api_keys.revoked",
        "The API key has been revoked.");

    public static readonly ResultError InsufficientScope = ResultError.Forbidden(
        "api_keys.insufficient_scope",
        "The API key does not grant the required scope.");

    public static readonly ResultError TenantInactive = ResultError.Forbidden(
        "api_keys.tenant_inactive",
        "The API key tenant is not active.");

    public static readonly ResultError NotFound = ResultError.NotFound(
        "api_keys.not_found",
        "The API key was not found.");
}
