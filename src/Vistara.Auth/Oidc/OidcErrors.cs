using Vistara.Domain.Common;

namespace Vistara.Auth.Oidc;

/// <summary>
/// Failure vocabulary for the OpenID Connect authorization-code flow. Every
/// message is deliberately generic: provider error codes, tokens, nonces, and
/// state handles must never reach a browser response, a log, or a trace.
/// </summary>
public static class OidcErrors
{
    public static readonly ResultError InvalidReturnTarget = ResultError.Validation(
        "oidc.invalid_return_target",
        "The requested return target is not an allowed application path.");

    public static readonly ResultError InvalidRequest = ResultError.Validation(
        "oidc.invalid_request",
        "The sign-in request is malformed.");

    public static readonly ResultError InvalidState = ResultError.Unauthorized(
        "oidc.invalid_state",
        "The sign-in request could not be verified.");

    public static readonly ResultError ProviderRejected = ResultError.Unauthorized(
        "oidc.provider_rejected",
        "The identity provider did not complete the sign-in request.");

    public static readonly ResultError MetadataUnavailable = ResultError.Unavailable(
        "oidc.metadata_unavailable",
        "The identity provider metadata is temporarily unavailable.");

    public static readonly ResultError ClientCredentialUnavailable = ResultError.Unavailable(
        "oidc.client_credential_unavailable",
        "The client credential is temporarily unavailable.");

    public static readonly ResultError TokenEndpointUnavailable = ResultError.Unavailable(
        "oidc.token_endpoint_unavailable",
        "The identity provider token endpoint is temporarily unavailable.");

    public static readonly ResultError TokenExchangeFailed = ResultError.Unauthorized(
        "oidc.token_exchange_failed",
        "The authorization code could not be redeemed.");

    public static readonly ResultError InvalidIdToken = ResultError.Unauthorized(
        "oidc.invalid_id_token",
        "The identity token is invalid.");

    public static readonly ResultError TenantNotAllowed = ResultError.Forbidden(
        "oidc.tenant_not_allowed",
        "The identity does not belong to an approved directory tenant.");
}
