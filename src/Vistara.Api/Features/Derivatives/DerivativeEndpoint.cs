using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vistara.Contracts.Derivatives;
using Vistara.Contracts.Errors;
using Vistara.Contracts.Idempotency;

namespace Vistara.Api.Features.Derivatives;

public static class DerivativeEndpoint
{
    private const int MaximumRequestBytes = 16 * 1_024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task ListPresetsAsync(
        HttpContext context,
        IDerivativeAuthorizationPort authorization,
        IDerivativeApplicationPort application,
        CancellationToken cancellationToken)
    {
        DerivativeAccess access =
            await authorization.AuthorizeCatalogAsync(context, cancellationToken);
        if (!await EnsureAuthorizedAsync(context, access, cancellationToken))
        {
            return;
        }

        try
        {
            IReadOnlyList<DerivativePresetDefinition> definitions =
                await application.ListPresetsAsync(access.TenantId!.Value, cancellationToken);
            var response = new DerivativePresetCatalogResponse(
                definitions.Select(ToContract).ToArray());
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                response,
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "derivative_service_unavailable",
                "Derivative service is unavailable",
                cancellationToken);
        }
    }

    public static async Task ListAsync(
        HttpContext context,
        Guid assetId,
        IDerivativeAuthorizationPort authorization,
        IDerivativeApplicationPort application,
        CancellationToken cancellationToken)
    {
        DerivativeAssetScope? scope = await AuthorizeAssetAsync(
            context,
            assetId,
            authorization,
            cancellationToken);
        if (scope is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<DerivativeWorkSnapshot> items =
                await application.ListAsync(scope, cancellationToken);
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new DerivativeCollectionResponse(items.Select(ToContract).ToArray()),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "derivative_service_unavailable",
                "Derivative service is unavailable",
                cancellationToken);
        }
    }

    public static async Task GetStatusAsync(
        HttpContext context,
        Guid assetId,
        Guid requestId,
        IDerivativeAuthorizationPort authorization,
        IDerivativeApplicationPort application,
        CancellationToken cancellationToken)
    {
        DerivativeAssetScope? scope = await AuthorizeAssetAsync(
            context,
            assetId,
            authorization,
            cancellationToken);
        if (scope is null)
        {
            return;
        }

        try
        {
            DerivativeWorkSnapshot? snapshot =
                await application.GetStatusAsync(scope, requestId, cancellationToken);
            if (snapshot is null)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "derivative_not_found",
                    "The derivative request was not found",
                    cancellationToken);
                return;
            }

            SetStatusHeaders(context, snapshot);
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                ToContract(snapshot),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "derivative_service_unavailable",
                "Derivative service is unavailable",
                cancellationToken);
        }
    }

    public static async Task RequestAsync(
        HttpContext context,
        Guid assetId,
        IDerivativeAuthorizationPort authorization,
        IDerivativeApplicationPort application,
        CancellationToken cancellationToken)
    {
        DerivativeAssetScope? scope = await AuthorizeAssetAsync(
            context,
            assetId,
            authorization,
            cancellationToken);
        if (scope is null)
        {
            return;
        }

        if (!TryReadIdempotencyKey(
                context.Request.Headers["Idempotency-Key"],
                out IdempotencyKey idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "A valid Idempotency-Key header is required",
                cancellationToken);
            return;
        }

        DerivativeRequestContract? request =
            await ReadRequestAsync(context, cancellationToken);
        if (request is null)
        {
            return;
        }

        try
        {
            DerivativeCanonicalizationResult canonicalization =
                await application.CanonicalizeAsync(scope, request, cancellationToken);
            if (canonicalization.Status != DerivativeCanonicalizationStatus.Accepted ||
                canonicalization.Request is null)
            {
                (string code, string title) = canonicalization.Status switch
                {
                    DerivativeCanonicalizationStatus.PresetNotFound =>
                        ("derivative_preset_not_allowed", "The derivative preset is not allowed"),
                    DerivativeCanonicalizationStatus.RevisionNotActive =>
                        (
                            "derivative_preset_revision_not_active",
                            "The derivative preset revision is not active"
                        ),
                    DerivativeCanonicalizationStatus.ParametersNotAllowed =>
                        (
                            "derivative_parameters_not_allowed",
                            "The derivative parameters are not allowed by the preset"
                        ),
                    _ => throw new InvalidOperationException(
                        "The derivative canonicalization result is invalid."),
                };
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    code,
                    title,
                    cancellationToken);
                return;
            }

            DerivativeSubmissionResult submission = await application.RequestAsync(
                scope,
                canonicalization.Request,
                idempotencyKey,
                cancellationToken);
            if (submission.Status == DerivativeSubmissionStatus.IdempotencyConflict)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "idempotency_key_conflict",
                    "The Idempotency-Key was already used for a different request",
                    cancellationToken);
                return;
            }

            DerivativeWorkSnapshot snapshot = submission.Snapshot ??
                throw new InvalidOperationException(
                    "A derivative submission must include a status snapshot.");
            SetStatusHeaders(context, snapshot);
            context.Response.Headers.Location =
                $"/api/v1/assets/{scope.AssetId:D}/derivatives/{snapshot.RequestId:D}";
            if (submission.Replayed || submission.ReusedExisting)
            {
                context.Response.Headers["Idempotency-Replayed"] = "true";
            }

            int status = submission.Status == DerivativeSubmissionStatus.Ready
                ? StatusCodes.Status200OK
                : StatusCodes.Status202Accepted;
            await WriteJsonAsync(context, status, ToContract(snapshot), cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "derivative_service_unavailable",
                "Derivative service is unavailable",
                cancellationToken);
        }
    }

    private static async ValueTask<DerivativeAssetScope?> AuthorizeAssetAsync(
        HttpContext context,
        Guid assetId,
        IDerivativeAuthorizationPort authorization,
        CancellationToken cancellationToken)
    {
        DerivativeAccess access =
            await authorization.AuthorizeAssetAsync(context, assetId, cancellationToken);
        if (!await EnsureAuthorizedAsync(context, access, cancellationToken))
        {
            return null;
        }

        if (access.AssetId != assetId)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status404NotFound,
                "derivative_not_found",
                "The requested resource was not found",
                cancellationToken);
            return null;
        }

        return new DerivativeAssetScope(access.TenantId!.Value, assetId);
    }

    private static async ValueTask<bool> EnsureAuthorizedAsync(
        HttpContext context,
        DerivativeAccess access,
        CancellationToken cancellationToken)
    {
        if (access.Status == DerivativeAccessStatus.Authorized &&
            access.TenantId is not null)
        {
            return true;
        }

        (int status, string code, string title) = access.Status switch
        {
            DerivativeAccessStatus.Unauthenticated =>
                (
                    StatusCodes.Status401Unauthorized,
                    "authentication_required",
                    "Authentication is required"
                ),
            DerivativeAccessStatus.Forbidden =>
                (
                    StatusCodes.Status403Forbidden,
                    "derivative_forbidden",
                    "Derivative access is forbidden"
                ),
            DerivativeAccessStatus.Concealed =>
                (
                    StatusCodes.Status404NotFound,
                    "derivative_not_found",
                    "The requested resource was not found"
                ),
            _ => (
                StatusCodes.Status404NotFound,
                "derivative_not_found",
                "The requested resource was not found"
            ),
        };
        await WriteProblemAsync(context, status, code, title, cancellationToken);
        return false;
    }

    private static async ValueTask<DerivativeRequestContract?> ReadRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > MaximumRequestBytes ||
            context.Request.ContentType is null ||
            !context.Request.ContentType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "derivative_request_invalid",
                "The derivative request is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            DerivativeRequestContract? request =
                await JsonSerializer.DeserializeAsync<DerivativeRequestContract>(
                    context.Request.Body,
                    JsonOptions,
                    cancellationToken);
            if (request is null)
            {
                throw new JsonException("A request body is required.");
            }

            return request;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "derivative_request_invalid",
                "The derivative request is invalid",
                cancellationToken);
            return null;
        }
    }

    private static bool TryReadIdempotencyKey(
        StringValues values,
        out IdempotencyKey idempotencyKey)
    {
        idempotencyKey = default;
        if (values.Count != 1)
        {
            return false;
        }

        string? value = values[0];
        if (string.IsNullOrEmpty(value) ||
            value.Length > 128 ||
            !IsIdempotencyCharacter(value[0]) ||
            value.Any(character => !IsIdempotencyCharacter(character)))
        {
            return false;
        }

        idempotencyKey = new IdempotencyKey(value);
        return true;
    }

    private static bool IsIdempotencyCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or ':';

    private static DerivativePresetContract ToContract(
        DerivativePresetDefinition definition) =>
        new(
            definition.Name,
            definition.ActiveRevision,
            definition.Revisions
                .Select(revision => new DerivativePresetRevisionContract(
                    revision.Revision,
                    revision.IsActive,
                    new DerivativeParameterBoundsContract(
                        revision.Parameters.MinimumWidth,
                        revision.Parameters.MaximumWidth,
                        revision.Parameters.MinimumHeight,
                        revision.Parameters.MaximumHeight,
                        revision.Parameters.MinimumQuality,
                        revision.Parameters.MaximumQuality,
                        revision.Parameters.Fits,
                        revision.Parameters.Formats)))
                .ToArray());

    private static DerivativeStatusResponse ToContract(
        DerivativeWorkSnapshot snapshot) =>
        new(
            snapshot.RequestId,
            snapshot.Preset,
            snapshot.Revision,
            new DerivativeParametersResponse(
                snapshot.Parameters.Width,
                snapshot.Parameters.Height,
                snapshot.Parameters.Fit,
                snapshot.Parameters.Format,
                snapshot.Parameters.Quality,
                snapshot.Parameters.FocalPoint is null
                    ? null
                    : new DerivativeFocalPointResponse(
                        snapshot.Parameters.FocalPoint.X,
                        snapshot.Parameters.FocalPoint.Y),
                snapshot.Parameters.Crop is null
                    ? null
                    : new DerivativeCropRectangleResponse(
                        snapshot.Parameters.Crop.X,
                        snapshot.Parameters.Crop.Y,
                        snapshot.Parameters.Crop.Width,
                        snapshot.Parameters.Crop.Height)),
            ToContract(snapshot.State),
            snapshot.Version,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            snapshot.Representation is null
                ? null
                : new DerivativeRepresentationResponse(
                    snapshot.Representation.Width,
                    snapshot.Representation.Height,
                    snapshot.Representation.Format,
                    snapshot.Representation.ContentType,
                    snapshot.Representation.EntityTag),
            ToSafeFailureCode(snapshot));

    private static string ToContract(DerivativeWorkState state) => state switch
    {
        DerivativeWorkState.Queued => "queued",
        DerivativeWorkState.Processing => "processing",
        DerivativeWorkState.Ready => "ready",
        DerivativeWorkState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string? ToSafeFailureCode(DerivativeWorkSnapshot snapshot)
    {
        if (snapshot.State != DerivativeWorkState.Failed)
        {
            return null;
        }

        string? code = snapshot.FailureCode;
        if (string.IsNullOrEmpty(code) ||
            code.Length > 128 ||
            code[0] is < 'a' or > 'z' ||
            code.Skip(1).Any(character =>
                character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9') &&
                character != '_'))
        {
            return "derivative_failed";
        }

        return code;
    }

    private static void SetStatusHeaders(
        HttpContext context,
        DerivativeWorkSnapshot snapshot)
    {
        context.Response.Headers.ETag = $"\"v{snapshot.Version}\"";
        context.Response.Headers.CacheControl = "no-store";
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        CancellationToken cancellationToken)
    {
        var problem = new ApiProblemDetails(
            $"https://vistara.dev/problems/{code.Replace('_', '-')}",
            title,
            status,
            new ErrorCode(code),
            traceId: context.TraceIdentifier);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            JsonOptions,
            cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int status,
        T response,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            JsonOptions,
            cancellationToken);
    }

    private static bool IsDependencyFailure(Exception exception) =>
        exception is InvalidOperationException or IOException or TimeoutException;
}
