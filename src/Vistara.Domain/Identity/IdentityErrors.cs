using Vistara.Domain.Common;

namespace Vistara.Domain.Identity;

public static class IdentityErrors
{
    public static readonly ResultError InvalidEmail = ResultError.Validation(
        "identity.invalid_email",
        "The email address is invalid.");

    public static readonly ResultError InvalidDisplayName = ResultError.Validation(
        "identity.invalid_display_name",
        "The display name is invalid.");

    public static readonly ResultError InvalidLocalLogin = ResultError.Validation(
        "identity.invalid_local_login",
        "The local identity login is invalid.");

    public static readonly ResultError InvalidExternalIssuer = ResultError.Validation(
        "identity.invalid_external_issuer",
        "The external identity issuer is invalid.");

    public static readonly ResultError InvalidExternalSubject = ResultError.Validation(
        "identity.invalid_external_subject",
        "The external identity subject is invalid.");

    public static readonly ResultError LocalIdentityExists = ResultError.Conflict(
        "identity.local_identity_exists",
        "The local identity is already linked.");

    public static readonly ResultError ExternalIdentityExists = ResultError.Conflict(
        "identity.external_identity_exists",
        "The external identity is already linked.");

    public static readonly ResultError StatusUnchanged = ResultError.Conflict(
        "identity.status_unchanged",
        "The user already has the requested status.");

    public static readonly ResultError InvalidStatusTransition = ResultError.Conflict(
        "identity.invalid_status_transition",
        "The user status transition is not allowed.");

    public static readonly ResultError InvalidSessionDigest = ResultError.Validation(
        "identity.invalid_session_digest",
        "The session digest must be a SHA-256 hash.");

    public static readonly ResultError SessionExpiryInvalid = ResultError.Validation(
        "identity.session_expiry_invalid",
        "The session expiry must be later than its creation time.");

    public static readonly ResultError SessionAlreadyRevoked = ResultError.Conflict(
        "identity.session_already_revoked",
        "The session is already revoked.");

    public static readonly ResultError InvalidApiKeyPrefix = ResultError.Validation(
        "identity.invalid_api_key_prefix",
        "The API key prefix is invalid.");

    public static readonly ResultError InvalidApiKeyDigest = ResultError.Validation(
        "identity.invalid_api_key_digest",
        "The API key digest must be an HMAC-SHA-256 hash.");

    public static readonly ResultError ApiKeyScopesRequired = ResultError.Validation(
        "identity.api_key_scopes_required",
        "At least one valid API key scope is required.");

    public static readonly ResultError ApiKeyExpiryInvalid = ResultError.Validation(
        "identity.api_key_expiry_invalid",
        "The API key expiry must be later than its creation time.");

    public static readonly ResultError ApiKeyAlreadyRevoked = ResultError.Conflict(
        "identity.api_key_already_revoked",
        "The API key is already revoked.");

    public static readonly ResultError ApiKeyRevoked = ResultError.Conflict(
        "identity.api_key_revoked",
        "The API key is revoked.");

    public static readonly ResultError ApiKeyExpired = ResultError.Conflict(
        "identity.api_key_expired",
        "The API key is expired.");

    public static readonly ResultError TimestampNotUtc = ResultError.Validation(
        "common.timestamp_not_utc",
        "Timestamps must use UTC.");

    public static readonly ResultError TimestampOutOfOrder = ResultError.Conflict(
        "common.timestamp_out_of_order",
        "The timestamp precedes the aggregate's latest change.");
}
