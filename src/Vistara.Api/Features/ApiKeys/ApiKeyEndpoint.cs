using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Features.Account;
using Vistara.Contracts.Identity;
using Vistara.Domain.Common;

namespace Vistara.Api.Features.ApiKeys;

/// <summary>
/// Tenant API key administration for <c>/api/v1/api-keys</c>. Secrets are
/// returned exactly once, on creation.
/// </summary>
public static class ApiKeyEndpoint
{
    private const int MaximumScopeCount = 8;

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task ListAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IApiKeyAdministrationPort administration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(administration);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ReadApiKeys,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        IReadOnlyList<ApiKeyView> keys =
            await administration.ListAsync(actor.TenantId, cancellationToken);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new ApiKeyCollectionResponse(
                keys.Select(Map).ToArray()),
            cancellationToken);
    }

    public static async Task CreateAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IApiKeyAdministrationPort administration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(administration);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageApiKeys,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        CreateApiKeyRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CreateApiKeyRequest>(
                context.Request.Body,
                ResponseJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "api_keys.malformed_request",
                "The API key request body could not be parsed.",
                cancellationToken);
            return;
        }

        if (request is null)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "api_keys.malformed_request",
                "The API key request body is required.",
                cancellationToken);
            return;
        }

        if (!TryReadScopes(request.Scopes, out string[] scopes, out string? scopeError))
        {
            await WriteValidationAsync(
                context,
                "scopes",
                scopeError!,
                cancellationToken);
            return;
        }

        if (request.ExpiresAt is { } expiresAt && expiresAt.Offset != TimeSpan.Zero)
        {
            await WriteValidationAsync(
                context,
                "expiresAt",
                "The expiry must be expressed in UTC.",
                cancellationToken);
            return;
        }

        Result<IssuedApiKeyView> issued = await administration.CreateAsync(
            new ApiKeyCreateCommand(
                actor.TenantId,
                actor.UserId,
                scopes,
                request.ExpiresAt),
            cancellationToken);
        if (!issued.TryGetValue(out IssuedApiKeyView? value))
        {
            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                issued.Error!,
                cancellationToken);
            return;
        }

        context.Response.Headers.Location = $"/api/v1/api-keys/{value.Key.Id:D}";
        await WriteJsonAsync(
            context,
            StatusCodes.Status201Created,
            new CreatedApiKeyResponse(Map(value.Key), value.Secret),
            cancellationToken);
    }

    public static async Task RevokeAsync(
        HttpContext context,
        Guid keyId,
        IAccountAuthorizationPort authorization,
        IApiKeyAdministrationPort administration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(administration);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageApiKeys,
            cancellationToken);
        if (access.Actor is not { } actor)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        if (keyId == Guid.Empty || keyId.Version != 7)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        Result revoked = await administration.RevokeAsync(
            actor.TenantId,
            actor.UserId,
            keyId,
            cancellationToken);
        if (revoked.IsFailure)
        {
            if (revoked.Error!.Category == ErrorCategory.NotFound)
            {
                await WriteNotFoundAsync(context, cancellationToken);
                return;
            }

            await ApiProblemWriter.WriteResultErrorAsync(
                context,
                revoked.Error,
                cancellationToken);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    internal static bool TryReadScopes(
        IReadOnlyList<string>? requested,
        out string[] scopes,
        out string? error)
    {
        scopes = [];
        if (requested is null || requested.Count == 0)
        {
            error = "At least one scope is required.";
            return false;
        }

        if (requested.Count > MaximumScopeCount)
        {
            error = "Too many scopes were requested.";
            return false;
        }

        var unique = new List<string>(requested.Count);
        foreach (string scope in requested)
        {
            if (string.IsNullOrWhiteSpace(scope) || !ApiKeyScopeCatalog.IsKnown(scope))
            {
                error = "One or more requested scopes are not supported.";
                return false;
            }

            if (!unique.Contains(scope, StringComparer.Ordinal))
            {
                unique.Add(scope);
            }
        }

        unique.Sort(StringComparer.Ordinal);
        scopes = [.. unique];
        error = null;
        return true;
    }

    private static ApiKeySummaryResponse Map(ApiKeyView key) =>
        new(
            key.Id,
            key.Prefix,
            key.OwnerId,
            key.Scopes,
            key.Status,
            key.CreatedAt,
            key.ExpiresAt,
            key.LastUsedAt,
            key.RevokedAt);

    private static Task DenyAsync(
        HttpContext context,
        AccountAccessStatus status,
        CancellationToken cancellationToken) =>
        status == AccountAccessStatus.Unauthenticated
            ? ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "api_keys.unauthenticated",
                "Authentication is required to administer API keys.",
                cancellationToken)
            : ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "api_keys.forbidden",
                "The caller is not permitted to administer API keys.",
                cancellationToken);

    private static Task WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            "api_keys.not_found",
            "The requested API key was not found.",
            cancellationToken);

    private static Task WriteValidationAsync(
        HttpContext context,
        string field,
        string message,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "api_keys.invalid_request",
            "The API key request is invalid.",
            cancellationToken,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [field] = [message],
            });

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int status,
        T payload,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ResponseJsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }
}
