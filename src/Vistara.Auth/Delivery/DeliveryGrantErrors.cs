using Vistara.Domain.Common;

namespace Vistara.Auth.Delivery;

public static class DeliveryGrantErrors
{
    public static readonly ResultError InvalidRequest = ResultError.Validation(
        "delivery_grants.invalid_request",
        "The delivery grant request is invalid.");

    public static readonly ResultError Forbidden = ResultError.Forbidden(
        "delivery_grants.forbidden",
        "The delivery grant cannot be issued.");

    public static readonly ResultError Concealed = ResultError.NotFound(
        "delivery_grants.not_found",
        "The requested media was not found.");

    public static readonly ResultError InvalidToken = ResultError.Unauthorized(
        "delivery_grants.invalid_token",
        "The delivery grant is invalid.");

    public static readonly ResultError NotYetValid = ResultError.Unauthorized(
        "delivery_grants.not_yet_valid",
        "The delivery grant is not yet valid.");

    public static readonly ResultError Expired = ResultError.Unauthorized(
        "delivery_grants.expired",
        "The delivery grant has expired.");

    public static readonly ResultError Revoked = ResultError.Unauthorized(
        "delivery_grants.revoked",
        "The delivery grant has been revoked.");
}
