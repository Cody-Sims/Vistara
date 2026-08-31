using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vistara.Application.Sharing;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Errors;
using Vistara.Contracts.Pagination;
using Vistara.Contracts.Sharing;

namespace Vistara.Api.Features.Shares;

public static class ShareEndpoint
{
    private const int MaximumRequestBytes = 32 * 1_024;
    private const string SessionCookieName = "Vistara.ShareSession";
    private const string SessionHeaderName = "X-Vistara-Share-Session";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task ListAsync(
        HttpContext context,
        IShareAuthorizationPort authorization,
        ShareService service,
        CancellationToken cancellationToken)
    {
        ShareActor? actor = await AuthorizeAsync(
            context,
            authorization,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        int? limit = await ReadLimitAsync(context, cancellationToken);
        if (!limit.HasValue)
        {
            return;
        }

        string? status = context.Request.Query["status"];
        string? cursor = ReadCursor(context.Request.Query["cursor"]);
        SharePageResult<ShareRecord> result = await service.ListAsync(
            actor,
            limit.Value,
            status,
            cursor,
            cancellationToken);
        if (result.Status == SharePageStatus.InvalidCursor)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "share_cursor_invalid",
                "The share cursor is invalid",
                cancellationToken);
            return;
        }

        if (result.Status == SharePageStatus.InvalidQuery)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "share_query_invalid",
                "The share query is invalid",
                cancellationToken);
            return;
        }

        SharePage<ShareRecord> page = result.Page!;
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new CursorPage<ShareSummaryResponse>(
                page.Items.Select(ToSummary).ToArray(),
                page.NextCursor is null
                    ? null
                    : new SignedCursor(page.NextCursor)),
            cancellationToken);
    }

    public static async Task CreateAsync(
        HttpContext context,
        IShareAuthorizationPort authorization,
        ShareService service,
        CancellationToken cancellationToken)
    {
        ShareActor? actor = await AuthorizeAsync(
            context,
            authorization,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        if (!TryReadIdempotencyKey(
                context.Request.Headers["Idempotency-Key"],
                out string idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "A valid Idempotency-Key header is required",
                cancellationToken);
            return;
        }

        CreateShareRequest? request =
            await ReadJsonAsync<CreateShareRequest>(
                context,
                cancellationToken);
        if (request is null)
        {
            return;
        }

        ShareCreateCommand? command;
        try
        {
            command = ToCommand(request);
        }
        catch (ArgumentException)
        {
            command = null;
        }

        if (command is null)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "share_request_invalid",
                "The share request is invalid",
                cancellationToken);
            return;
        }

        ShareCreateResult result = await service.CreateAsync(
            actor,
            command,
            idempotencyKey,
            cancellationToken);
        switch (result.Status)
        {
            case ShareCreateStatus.Created:
                SetShareHeaders(context, result.Share!);
                context.Response.Headers.Location =
                    $"/api/v1/shares/{result.Share!.Id:D}";
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status201Created,
                    new CreatedShareResponse(
                        ToDetail(result.Share),
                        result.PublicToken!),
                    cancellationToken);
                return;
            case ShareCreateStatus.TokenAlreadyIssued:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "share_token_already_issued",
                    "The share was already created and its token cannot be returned again",
                    cancellationToken);
                return;
            case ShareCreateStatus.IdempotencyConflict:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "idempotency_key_conflict",
                    "The Idempotency-Key was already used for a different request",
                    cancellationToken);
                return;
            case ShareCreateStatus.NotFound:
                // Every absent, stale, trashed, purged, or foreign target
                // reports the same problem so a share request cannot probe
                // which assets exist.
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "share_target_not_found",
                    "The share target was not found",
                    cancellationToken);
                return;
            default:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    result.ErrorCode ?? "share_request_invalid",
                    "The share request is invalid",
                    cancellationToken);
                return;
        }
    }

    public static async Task GetAsync(
        HttpContext context,
        Guid shareId,
        IShareAuthorizationPort authorization,
        ShareService service,
        CancellationToken cancellationToken)
    {
        ShareActor? actor = await AuthorizeAsync(
            context,
            authorization,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        ShareReadResult result = await service.GetAsync(
            actor,
            shareId,
            cancellationToken);
        if (result.Status == ShareReadStatus.NotFound)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        SetShareHeaders(context, result.Share!);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            ToDetail(result.Share!),
            cancellationToken);
    }

    public static async Task UpdateAsync(
        HttpContext context,
        Guid shareId,
        IShareAuthorizationPort authorization,
        ShareService service,
        CancellationToken cancellationToken)
    {
        ShareActor? actor = await AuthorizeAsync(
            context,
            authorization,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        long? expectedVersion = await ReadExpectedVersionAsync(
            context,
            cancellationToken);
        if (!expectedVersion.HasValue)
        {
            return;
        }

        if (!TryReadIdempotencyKey(
                context.Request.Headers["Idempotency-Key"],
                out string idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "A valid Idempotency-Key header is required",
                cancellationToken);
            return;
        }

        UpdateShareEnvelope? envelope =
            await ReadUpdateAsync(context, cancellationToken);
        if (envelope is null)
        {
            return;
        }

        UpdateShareRequest request = envelope.Request;
        ShareAccess? access = request.Permissions is null
            ? null
            : ToAccess(request.Permissions);
        ShareMetadataExposure exposure = default;
        if (request.MetadataExposure is not null &&
            !TryMetadataExposure(
                request.MetadataExposure,
                out exposure))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "share_update_invalid",
                "The share update is invalid",
                cancellationToken);
            return;
        }

        ShareMutationResult result = await service.UpdateAsync(
            actor,
            shareId,
            expectedVersion.Value,
            new ShareUpdateCommand(
                request.Name,
                access,
                request.MetadataExposure is null ? null : exposure,
                request.ExpiresAt,
                envelope.HasExpiry),
            idempotencyKey,
            cancellationToken);
        await WriteMutationAsync(
            context,
            result,
            includeBody: true,
            cancellationToken);
    }

    public static async Task RevokeAsync(
        HttpContext context,
        Guid shareId,
        IShareAuthorizationPort authorization,
        ShareService service,
        CancellationToken cancellationToken)
    {
        ShareActor? actor = await AuthorizeAsync(
            context,
            authorization,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        long? expectedVersion = await ReadExpectedVersionAsync(
            context,
            cancellationToken);
        if (!expectedVersion.HasValue)
        {
            return;
        }

        if (!TryReadIdempotencyKey(
                context.Request.Headers["Idempotency-Key"],
                out string idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "A valid Idempotency-Key header is required",
                cancellationToken);
            return;
        }

        ShareMutationResult result = await service.RevokeAsync(
            actor,
            shareId,
            expectedVersion.Value,
            idempotencyKey,
            cancellationToken);
        if (result.Status is ShareMutationStatus.Updated or
            ShareMutationStatus.Unchanged or
            ShareMutationStatus.Replayed)
        {
            if (result.Status == ShareMutationStatus.Replayed)
            {
                context.Response.Headers["Idempotency-Replayed"] = "true";
            }

            SetShareHeaders(context, result.Share!);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await WriteMutationAsync(
            context,
            result,
            includeBody: false,
            cancellationToken);
    }

    public static async Task GetPublicAsync(
        HttpContext context,
        string publicToken,
        ShareService service,
        CancellationToken cancellationToken)
    {
        int? limit = await ReadLimitAsync(context, cancellationToken);
        if (!limit.HasValue)
        {
            return;
        }

        string? sessionToken = ReadSessionToken(context);
        string? cursor = ReadCursor(context.Request.Query["cursor"]);
        SharePublicResult result = await service.GetPublicAsync(
            publicToken,
            sessionToken,
            limit.Value,
            cursor,
            cancellationToken);
        switch (result.Status)
        {
            case SharePublicStatus.Available:
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status200OK,
                    ToPublic(result.Share!, publicToken),
                    cancellationToken);
                return;
            case SharePublicStatus.Gone:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status410Gone,
                    "share_gone",
                    "The share has expired or been revoked",
                    cancellationToken);
                return;
            default:
                await WriteNotFoundAsync(context, cancellationToken);
                return;
        }
    }

    public static async Task ChallengeAsync(
        HttpContext context,
        string publicToken,
        ShareService service,
        CancellationToken cancellationToken)
    {
        ShareChallengeRequest? request =
            await ReadJsonAsync<ShareChallengeRequest>(
                context,
                cancellationToken);
        if (request is null)
        {
            await service.AuditChallengeRejectionAsync(
                publicToken,
                "share_challenge_request_invalid",
                cancellationToken);
            return;
        }

        if (string.IsNullOrEmpty(request.Password) ||
            request.Password.Length > 256)
        {
            await service.AuditChallengeRejectionAsync(
                publicToken,
                "share_challenge_request_invalid",
                cancellationToken);
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "share_request_invalid",
                "The share request is invalid",
                cancellationToken);
            return;
        }

        string partition =
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        ShareChallengeResult result = await service.ChallengeAsync(
            publicToken,
            request.Password,
            partition,
            cancellationToken);
        switch (result.Status)
        {
            case ShareChallengeStatus.Authenticated:
                context.Response.Cookies.Append(
                    SessionCookieName,
                    result.SessionToken!,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax,
                        Path = "/api/v1/public/shares",
                        Expires = result.ExpiresAtUtc,
                    });
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status200OK,
                    new ShareChallengeResponse(
                        true,
                        result.ExpiresAtUtc!.Value),
                    cancellationToken);
                return;
            case ShareChallengeStatus.InvalidPassword:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "share_password_invalid",
                    "The share password is invalid",
                    cancellationToken);
                return;
            case ShareChallengeStatus.Gone:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status410Gone,
                    "share_gone",
                    "The share has expired or been revoked",
                    cancellationToken);
                return;
            case ShareChallengeStatus.RateLimited:
                if (result.RetryAfter is { } retryAfter)
                {
                    context.Response.Headers.RetryAfter =
                        Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds))
                            .ToString(CultureInfo.InvariantCulture);
                }

                await WriteProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "share_challenge_rate_limited",
                    "Too many share password attempts",
                    cancellationToken);
                return;
            default:
                await WriteNotFoundAsync(context, cancellationToken);
                return;
        }
    }

    private static async ValueTask<ShareActor?> AuthorizeAsync(
        HttpContext context,
        IShareAuthorizationPort authorization,
        CancellationToken cancellationToken)
    {
        ShareAccessDecision decision = await authorization.AuthorizeAsync(
            context,
            cancellationToken);
        if (decision.Status == ShareAccessDecisionStatus.Authorized)
        {
            return new ShareActor(
                decision.TenantId!.Value,
                decision.ActorId!.Value);
        }

        (int Status, string Code, string Title) problem =
            decision.Status switch
            {
                ShareAccessDecisionStatus.Unauthenticated =>
                    (
                        StatusCodes.Status401Unauthorized,
                        "authentication_required",
                        "Authentication is required"
                    ),
                ShareAccessDecisionStatus.Forbidden =>
                    (
                        StatusCodes.Status403Forbidden,
                        "share_forbidden",
                        "The share operation is forbidden"
                    ),
                _ =>
                    (
                        StatusCodes.Status404NotFound,
                        "share_not_found",
                        "The share was not found"
                    ),
            };
        await WriteProblemAsync(
            context,
            problem.Status,
            problem.Code,
            problem.Title,
            cancellationToken);
        return null;
    }

    private static ShareCreateCommand? ToCommand(CreateShareRequest request)
    {
        if (!TryTargetType(request.TargetKind, out ShareTargetType target) ||
            !TryMetadataExposure(
                request.MetadataExposure,
                out ShareMetadataExposure exposure))
        {
            return null;
        }

        return new ShareCreateCommand(
            request.Name,
            target,
            request.AlbumId,
            request.SnapshotAssets
                .Select(item => new ShareAssetReference(
                    item.Id,
                    item.Version.Value))
                .ToArray(),
            ToAccess(request.Permissions),
            exposure,
            request.ExpiresAt,
            request.Password);
    }

    private static ShareAccess ToAccess(
        SharePermissionsResponse permissions)
    {
        ShareAccess access = ShareAccess.View;
        if (permissions.DownloadRenditions)
        {
            access |= ShareAccess.DownloadRenditions;
        }

        if (permissions.DownloadOriginal)
        {
            access |= ShareAccess.DownloadOriginal;
        }

        return access;
    }

    private static bool TryTargetType(
        string value,
        out ShareTargetType target)
    {
        target = value switch
        {
            "album" => ShareTargetType.Album,
            "snapshot" => ShareTargetType.Snapshot,
            _ => default,
        };
        return value is "album" or "snapshot";
    }

    private static bool TryMetadataExposure(
        string value,
        out ShareMetadataExposure exposure)
    {
        exposure = value switch
        {
            "none" => ShareMetadataExposure.None,
            "basic" => ShareMetadataExposure.Basic,
            _ => default,
        };
        return value is "none" or "basic";
    }

    private static ShareSummaryResponse ToSummary(ShareRecord share) =>
        new(
            share.Id,
            share.Name,
            StatusText(share.StatusAt(DateTimeOffset.UtcNow)),
            new ShareTargetResponse(
                share.TargetType == ShareTargetType.Album
                    ? "album"
                    : "snapshot",
                share.AlbumId,
                share.Assets.Count),
            ToPermissions(share.Permissions),
            share.MetadataExposure == ShareMetadataExposure.Basic
                ? "basic"
                : "none",
            share.PasswordProtected,
            share.CreatedAtUtc,
            share.ExpiresAtUtc,
            share.RevokedAtUtc,
            new ResourceVersion(share.Version));

    private static ShareDetailResponse ToDetail(ShareRecord share) =>
        new(
            ToSummary(share),
            share.Assets
                .Select(asset => new VersionedAssetReference(
                    asset.AssetId,
                    new ResourceVersion(asset.AssetVersion)))
                .ToArray());

    private static PublicShareResponse ToPublic(
        SharePublicProjection share,
        string publicToken) =>
        new(
            share.Name,
            StatusText(share.Status),
            ToPermissions(share.Permissions),
            share.MetadataExposure == ShareMetadataExposure.Basic
                ? "basic"
                : "none",
            share.PasswordRequired,
            share.ExpiresAtUtc,
            share.PasswordRequired
                ? null
                : new CursorPage<PublicSharedAssetResponse>(
                    share.Assets
                        .Select(asset => ToPublicAsset(asset, publicToken))
                        .ToArray(),
                    share.NextCursor is null
                        ? null
                        : new SignedCursor(share.NextCursor)));

    /// <summary>
    /// Publishes the path a recipient can actually fetch. A public derivative
    /// keeps its immutable media path, while a privately captured rendition is
    /// published as the share-scoped delivery URL bound to the presented token,
    /// which never exposes the storage pipeline, source hash, or recipe hash.
    /// </summary>
    private static PublicSharedAssetResponse ToPublicAsset(
        ShareAssetSnapshot asset,
        string publicToken) =>
        new(
            asset.AssetId,
            asset.Title,
            asset.Description,
            asset.CapturedAtUtc,
            asset.Width,
            asset.Height,
            asset.Renditions.Select(rendition =>
                new AssetRenditionResponse(
                    rendition.Kind,
                    rendition.DeliveryIdentifier is { } identifier
                        ? ShareRenditionRoute.Build(
                            publicToken,
                            asset.AssetId,
                            identifier)
                        : rendition.Path,
                    rendition.Width,
                    rendition.Height,
                    rendition.ContentType)).ToArray());

    private static SharePermissionsResponse ToPermissions(
        ShareAccess access) =>
        new(
            true,
            access.HasFlag(ShareAccess.DownloadRenditions),
            access.HasFlag(ShareAccess.DownloadOriginal));

    private static string StatusText(ShareLifecycleStatus status) =>
        status switch
        {
            ShareLifecycleStatus.Active => "active",
            ShareLifecycleStatus.Expired => "expired",
            ShareLifecycleStatus.Revoked => "revoked",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static async ValueTask WriteMutationAsync(
        HttpContext context,
        ShareMutationResult result,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        switch (result.Status)
        {
            case ShareMutationStatus.Updated:
            case ShareMutationStatus.Unchanged:
            case ShareMutationStatus.Replayed:
                SetShareHeaders(context, result.Share!);
                if (result.Status == ShareMutationStatus.Replayed)
                {
                    context.Response.Headers["Idempotency-Replayed"] = "true";
                }

                if (includeBody)
                {
                    await WriteJsonAsync(
                        context,
                        StatusCodes.Status200OK,
                        ToDetail(result.Share!),
                        cancellationToken);
                }
                else
                {
                    context.Response.StatusCode =
                        StatusCodes.Status204NoContent;
                }

                return;
            case ShareMutationStatus.NotFound:
                await WriteNotFoundAsync(context, cancellationToken);
                return;
            case ShareMutationStatus.VersionConflict:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status412PreconditionFailed,
                    "share_version_conflict",
                    "The share version does not match",
                    cancellationToken);
                return;
            case ShareMutationStatus.IdempotencyConflict:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "idempotency_key_conflict",
                    "The Idempotency-Key was already used for a different request",
                    cancellationToken);
                return;
            default:
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    result.ErrorCode ?? "share_update_invalid",
                    "The share update is invalid",
                    cancellationToken);
                return;
        }
    }

    private static async ValueTask<long?> ReadExpectedVersionAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        StringValues values = context.Request.Headers.IfMatch;
        if (values.Count == 0)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status428PreconditionRequired,
                "if_match_required",
                "An If-Match header is required",
                cancellationToken);
            return null;
        }

        string? value = values.Count == 1 ? values[0] : null;
        if (value is null ||
            value.Length < 4 ||
            value[0] != '"' ||
            value[1] != 'v' ||
            value[^1] != '"' ||
            !long.TryParse(
                value.AsSpan(2, value.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long version) ||
            version < 1 ||
            !string.Equals(
                value,
                $"\"v{version.ToString(CultureInfo.InvariantCulture)}\"",
                StringComparison.Ordinal))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "share_version_conflict",
                "The share version does not match",
                cancellationToken);
            return null;
        }

        return version;
    }

    internal static string? ReadSessionToken(HttpContext context)
    {
        StringValues header = context.Request.Headers[SessionHeaderName];
        if (header.Count == 1 && !string.IsNullOrEmpty(header[0]))
        {
            return header[0];
        }

        return context.Request.Cookies.TryGetValue(
            SessionCookieName,
            out string? cookie)
            ? cookie
            : null;
    }

    private static string? ReadCursor(StringValues values) =>
        values.Count == 1 && !string.IsNullOrWhiteSpace(values[0])
            ? values[0]
            : null;

    private static async ValueTask<int?> ReadLimitAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        StringValues values = context.Request.Query["limit"];
        if (values.Count == 0)
        {
            return CursorPageRequest.DefaultLimit;
        }

        if (values.Count == 1 &&
            int.TryParse(
                values[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit) &&
            limit is >= 1 and <= 200)
        {
            return limit;
        }

        await WriteProblemAsync(
            context,
            StatusCodes.Status400BadRequest,
            "share_query_invalid",
            "The share query is invalid",
            cancellationToken);
        return null;
    }

    private static bool TryReadIdempotencyKey(
        StringValues values,
        out string idempotencyKey)
    {
        idempotencyKey = string.Empty;
        if (values.Count != 1)
        {
            return false;
        }

        string? value = values[0];
        if (string.IsNullOrEmpty(value) ||
            value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':')))
        {
            return false;
        }

        idempotencyKey = value;
        return true;
    }

    private static async ValueTask<T?> ReadJsonAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is > MaximumRequestBytes ||
            context.Request.ContentType is null ||
            !context.Request.ContentType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteProblemAsync(
                context,
                context.Request.ContentLength is > MaximumRequestBytes
                    ? StatusCodes.Status413PayloadTooLarge
                    : StatusCodes.Status400BadRequest,
                "share_request_invalid",
                "The share request is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            using var bounded = new MaximumLengthReadStream(
                context.Request.Body,
                MaximumRequestBytes);
            T? request = await JsonSerializer.DeserializeAsync<T>(
                bounded,
                JsonOptions,
                cancellationToken);
            if (request is null)
            {
                throw new JsonException();
            }

            return request;
        }
        catch (ShareRequestTooLargeException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "share_request_too_large",
                "The share request is too large",
                cancellationToken);
            return null;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "share_request_invalid",
                "The share request is invalid",
                cancellationToken);
            return null;
        }
    }

    private static async ValueTask<UpdateShareEnvelope?> ReadUpdateAsync(
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
                context.Request.ContentLength is > MaximumRequestBytes
                    ? StatusCodes.Status413PayloadTooLarge
                    : StatusCodes.Status400BadRequest,
                "share_request_invalid",
                "The share request is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            using var bounded = new MaximumLengthReadStream(
                context.Request.Body,
                MaximumRequestBytes);
            using JsonDocument document = await JsonDocument.ParseAsync(
                bounded,
                cancellationToken: cancellationToken);
            UpdateShareRequest request =
                document.RootElement.Deserialize<UpdateShareRequest>(
                    JsonOptions) ??
                throw new JsonException();
            return new UpdateShareEnvelope(
                request,
                document.RootElement.TryGetProperty("expiresAt", out _));
        }
        catch (ShareRequestTooLargeException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "share_request_too_large",
                "The share request is too large",
                cancellationToken);
            return null;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "share_request_invalid",
                "The share request is invalid",
                cancellationToken);
            return null;
        }
    }

    private static void SetShareHeaders(
        HttpContext context,
        ShareRecord share)
    {
        context.Response.Headers.ETag =
            $"\"v{share.Version.ToString(CultureInfo.InvariantCulture)}\"";
        context.Response.Headers.CacheControl = "no-store";
    }

    private static Task WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status404NotFound,
            "share_not_found",
            "The share was not found",
            cancellationToken);

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

    private sealed record UpdateShareEnvelope(
        UpdateShareRequest Request,
        bool HasExpiry);

    private sealed class ShareRequestTooLargeException : Exception;

    private sealed class MaximumLengthReadStream(
        Stream inner,
        long maximumBytes) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(
                buffer,
                offset,
                LimitCount(count));
            Track(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer[..LimitCount(buffer.Length)]);
            Track(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(
                buffer[..LimitCount(buffer.Length)],
                cancellationToken);
            Track(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int LimitCount(int requested)
        {
            long remaining = maximumBytes - _read;
            if (remaining < 0)
            {
                throw new ShareRequestTooLargeException();
            }

            return (int)Math.Min(requested, remaining + 1);
        }

        private void Track(int read)
        {
            _read += read;
            if (_read > maximumBytes)
            {
                throw new ShareRequestTooLargeException();
            }
        }
    }
}
