using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Errors;
using Vistara.Contracts.Media;

namespace Vistara.Api.Features.Media;

public static class MediaDeliveryEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task PublicDerivativeAsync(
        HttpContext context,
        string pipeline,
        string sourceHash,
        string recipeHash,
        string extension,
        IMediaDeliveryApplicationPort application,
        CancellationToken cancellationToken)
    {
        MediaDerivativeRequest? request = CreateDerivativeRequest(
            pipeline,
            sourceHash,
            recipeHash,
            extension);
        if (request is null)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        MediaDeliveryResult? result = await ResolveAsync(
            context,
            () => application.ResolvePublicDerivativeAsync(
                request,
                cancellationToken),
            cancellationToken);
        if (result is null)
        {
            return;
        }

        await DeliverAsync(
            context,
            result,
            request.Extension,
            MediaDeliveryHttpContract.PublicImmutableCacheControl,
            includeDownloadFileName: false,
            allowQueued: true,
            cancellationToken);
    }

    public static async Task PrivateDerivativeAsync(
        HttpContext context,
        string pipeline,
        string sourceHash,
        string recipeHash,
        string extension,
        IMediaDeliveryAuthorizationPort authorization,
        IMediaDeliveryApplicationPort application,
        CancellationToken cancellationToken)
    {
        if (!TryReadDeliveryCredential(context, out MediaDeliveryCredential? credential))
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        MediaDeliveryAccess? access = await AuthorizeAsync(
            context,
            () => authorization.AuthorizePrivateDerivativeAsync(
                context,
                credential,
                cancellationToken),
            cancellationToken);
        if (access is null)
        {
            return;
        }

        if (!await EnsureAuthorizedAsync(
                context,
                access,
                concealUnauthenticated: true,
                cancellationToken))
        {
            return;
        }

        MediaDerivativeRequest? request = CreateDerivativeRequest(
            pipeline,
            sourceHash,
            recipeHash,
            extension);
        if (request is null)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        var scope = new MediaTenantScope(access.TenantId!.Value);
        MediaDeliveryResult? result = await ResolveAsync(
            context,
            () => application.ResolvePrivateDerivativeAsync(
                scope,
                request,
                cancellationToken),
            cancellationToken);
        if (result is null)
        {
            return;
        }

        await DeliverAsync(
            context,
            result,
            request.Extension,
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            includeDownloadFileName: false,
            allowQueued: true,
            cancellationToken);
    }

    public static async Task OriginalAsync(
        HttpContext context,
        Guid assetId,
        IMediaDeliveryAuthorizationPort authorization,
        IMediaDeliveryApplicationPort application,
        CancellationToken cancellationToken)
    {
        MediaDeliveryAccess? access = await AuthorizeAsync(
            context,
            () => authorization.AuthorizeOriginalAsync(
                context,
                assetId,
                cancellationToken),
            cancellationToken);
        if (access is null)
        {
            return;
        }

        if (!await EnsureAuthorizedAsync(
                context,
                access,
                concealUnauthenticated: false,
                cancellationToken))
        {
            return;
        }

        if (access.AssetId != assetId)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        var scope = new MediaAssetScope(access.TenantId!.Value, assetId);
        MediaDeliveryResult? result = await ResolveAsync(
            context,
            () => application.ResolveOriginalAsync(scope, cancellationToken),
            cancellationToken);
        if (result is null)
        {
            return;
        }

        await DeliverAsync(
            context,
            result,
            expectedExtension: null,
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            includeDownloadFileName: true,
            allowQueued: false,
            cancellationToken);
    }

    private static async Task DeliverAsync(
        HttpContext context,
        MediaDeliveryResult result,
        string? expectedExtension,
        string cacheControl,
        bool includeDownloadFileName,
        bool allowQueued,
        CancellationToken cancellationToken)
    {
        if (result.Status == MediaDeliveryStatus.Queued && allowQueued)
        {
            await WriteQueuedAsync(context, cancellationToken);
            return;
        }

        if (result.Status != MediaDeliveryStatus.Ready ||
            result.Representation is null)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        MediaRepresentation representation = result.Representation;
        if (!IsSafeMediaType(representation.ContentType) ||
            (expectedExtension is not null &&
             !MediaTypeMatchesExtension(
                 representation.ContentType,
                 expectedExtension)))
        {
            await WriteServiceUnavailableAsync(context, cancellationToken);
            return;
        }

        string entityTag = $"\"{representation.Sha256}\"";

        if (!IfMatchSatisfied(context.Request.Headers.IfMatch, entityTag))
        {
            SetEmptyNoStoreResponse(
                context,
                StatusCodes.Status412PreconditionFailed);
            return;
        }

        if (IfNoneMatchSatisfied(context.Request.Headers.IfNoneMatch, entityTag))
        {
            SetRepresentationHeaders(
                context,
                representation,
                entityTag,
                cacheControl);
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.ContentType = null;
            context.Response.ContentLength = null;
            return;
        }

        MediaByteRange? range = null;
        bool rangeRequested = !StringValues.IsNullOrEmpty(
            context.Request.Headers.Range);
        if (rangeRequested &&
            IfRangeAllowsRange(context.Request.Headers.IfRange, entityTag))
        {
            if (!TryParseRange(
                    context.Request.Headers.Range.ToString(),
                    representation.ContentLength,
                    out range))
            {
                SetEmptyNoStoreResponse(
                    context,
                    StatusCodes.Status416RangeNotSatisfiable);
                context.Response.Headers.ContentRange =
                    $"bytes */{representation.ContentLength}";
                return;
            }
        }

        SetRepresentationHeaders(
            context,
            representation,
            entityTag,
            cacheControl);
        long responseLength = range?.Length ?? representation.ContentLength;
        if (range is not null)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange =
                $"bytes {range.Offset}-{range.Offset + range.Length - 1}/{representation.ContentLength}";
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
        }

        context.Response.ContentLength = responseLength;
        if (includeDownloadFileName &&
            !string.IsNullOrWhiteSpace(representation.DownloadFileName))
        {
            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{SanitizeFileName(representation.DownloadFileName)}\"";
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        MediaReadHandle? handle;
        try
        {
            handle = await representation.Source.OpenReadAsync(
                range,
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            ClearRepresentationHeaders(context);
            await WriteServiceUnavailableAsync(context, cancellationToken);
            return;
        }

        await using (handle)
        {
            await handle.Content.CopyToAsync(
                context.Response.Body,
                cancellationToken);
        }
    }

    private static void SetRepresentationHeaders(
        HttpContext context,
        MediaRepresentation representation,
        string entityTag,
        string cacheControl)
    {
        context.Response.ContentType = representation.ContentType;
        context.Response.Headers.ETag = entityTag;
        context.Response.Headers.CacheControl = cacheControl;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static void ClearRepresentationHeaders(HttpContext context)
    {
        context.Response.ContentType = null;
        context.Response.ContentLength = null;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers.Remove("Content-Range");
        context.Response.Headers.Remove("Content-Disposition");
    }

    private static void SetEmptyNoStoreResponse(
        HttpContext context,
        int statusCode)
    {
        ClearRepresentationHeaders(context);
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;
        context.Response.Headers.CacheControl =
            MediaDeliveryHttpContract.NoStoreCacheControl;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static bool IfMatchSatisfied(
        StringValues values,
        string entityTag)
    {
        if (StringValues.IsNullOrEmpty(values))
        {
            return true;
        }

        return EnumerateEntityTags(values).Any(value =>
            value == "*" ||
            (!value.StartsWith("W/", StringComparison.Ordinal) &&
             string.Equals(value, entityTag, StringComparison.Ordinal)));
    }

    private static bool IfNoneMatchSatisfied(
        StringValues values,
        string entityTag)
    {
        if (StringValues.IsNullOrEmpty(values))
        {
            return false;
        }

        return EnumerateEntityTags(values).Any(value =>
            value == "*" ||
            string.Equals(
                value.StartsWith("W/", StringComparison.Ordinal)
                    ? value[2..]
                    : value,
                entityTag,
                StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateEntityTags(StringValues values) =>
        values
            .SelectMany(value => value?.Split(',') ?? [])
            .Select(value => value.Trim())
            .Where(value => value.Length > 0);

    private static bool IfRangeAllowsRange(
        StringValues values,
        string entityTag)
    {
        if (StringValues.IsNullOrEmpty(values))
        {
            return true;
        }

        return values.Count == 1 &&
            string.Equals(values[0]?.Trim(), entityTag, StringComparison.Ordinal);
    }

    private static bool TryParseRange(
        string value,
        long totalLength,
        out MediaByteRange? range)
    {
        range = null;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string specification = value[6..].Trim();
        if (specification.Length == 0 || specification.Contains(','))
        {
            return false;
        }

        int separator = specification.IndexOf('-');
        if (separator < 0 ||
            specification.IndexOf('-', separator + 1) >= 0)
        {
            return false;
        }

        string startText = specification[..separator].Trim();
        string endText = specification[(separator + 1)..].Trim();
        if (startText.Length == 0)
        {
            if (!TryParseNonNegativeInt64(endText, out long suffixLength) ||
                suffixLength == 0)
            {
                return false;
            }

            long length = Math.Min(suffixLength, totalLength);
            range = new MediaByteRange(totalLength - length, length);
            return true;
        }

        if (!TryParseNonNegativeInt64(startText, out long start) ||
            start >= totalLength)
        {
            return false;
        }

        if (endText.Length == 0)
        {
            range = new MediaByteRange(start, totalLength - start);
            return true;
        }

        if (!TryParseNonNegativeInt64(endText, out long requestedEnd) ||
            requestedEnd < start)
        {
            return false;
        }

        long end = Math.Min(requestedEnd, totalLength - 1);
        range = new MediaByteRange(start, checked(end - start + 1));
        return true;
    }

    private static bool TryParseNonNegativeInt64(string value, out long result) =>
        long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result);

    private static bool IsSafeMediaType(string contentType) =>
        contentType is "image/jpeg" or "image/png" or "image/webp";

    private static bool MediaTypeMatchesExtension(
        string contentType,
        string extension) =>
        extension switch
        {
            "jpg" or "jpeg" => contentType == "image/jpeg",
            "png" => contentType == "image/png",
            "webp" => contentType == "image/webp",
            _ => false,
        };

    private static string SanitizeFileName(string fileName)
    {
        string leafName = fileName
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? "download";
        var sanitized = new char[Math.Min(leafName.Length, 150)];
        int length = 0;
        foreach (char character in leafName)
        {
            if (length == sanitized.Length)
            {
                break;
            }

            sanitized[length++] =
                character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or
                    ' ' or '.' or '_' or '-' or '(' or ')'
                    ? character
                    : '_';
        }

        string result = new(sanitized, 0, length);
        result = result.Trim(' ', '.');
        return result.Length == 0 ? "download" : result;
    }

    private static MediaDerivativeRequest? CreateDerivativeRequest(
        string pipeline,
        string sourceHash,
        string recipeHash,
        string extension)
    {
        try
        {
            return new MediaDerivativeRequest(
                pipeline,
                sourceHash,
                recipeHash,
                extension);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryReadDeliveryCredential(
        HttpContext context,
        out MediaDeliveryCredential? credential)
    {
        StringValues values = context.Request.Headers.Authorization;
        if (StringValues.IsNullOrEmpty(values))
        {
            credential = null;
            return true;
        }

        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(
                values[0],
                out AuthenticationHeaderValue? authorization))
        {
            credential = null;
            return false;
        }

        if (!string.Equals(
                authorization.Scheme,
                MediaDeliveryHttpContract.DeliveryGrantAuthorizationScheme,
                StringComparison.OrdinalIgnoreCase))
        {
            credential = null;
            return true;
        }

        return MediaDeliveryCredential.TryCreate(
            authorization.Parameter,
            out credential);
    }

    private static async ValueTask<MediaDeliveryAccess?> AuthorizeAsync(
        HttpContext context,
        Func<ValueTask<MediaDeliveryAccess>> authorize,
        CancellationToken cancellationToken)
    {
        try
        {
            return await authorize();
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteServiceUnavailableAsync(context, cancellationToken);
            return null;
        }
    }

    private static async ValueTask<MediaDeliveryResult?> ResolveAsync(
        HttpContext context,
        Func<ValueTask<MediaDeliveryResult>> resolve,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resolve();
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteServiceUnavailableAsync(context, cancellationToken);
            return null;
        }
    }

    private static async ValueTask<bool> EnsureAuthorizedAsync(
        HttpContext context,
        MediaDeliveryAccess access,
        bool concealUnauthenticated,
        CancellationToken cancellationToken)
    {
        if (access.Status == MediaDeliveryAccessStatus.Authorized &&
            access.TenantId is not null)
        {
            return true;
        }

        if (access.Status == MediaDeliveryAccessStatus.Unauthenticated &&
            !concealUnauthenticated)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required",
                cancellationToken);
            return false;
        }

        await WriteNotFoundAsync(context, cancellationToken);
        return false;
    }

    private static async Task WriteQueuedAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new MediaProcessingResponse("queued"),
            JsonOptions);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        context.Response.Headers.CacheControl =
            MediaDeliveryHttpContract.NoStoreCacheControl;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(body, cancellationToken);
        }
    }

    private static Task WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status404NotFound,
            "media_not_found",
            "The requested media was not found",
            cancellationToken);

    private static Task WriteServiceUnavailableAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "media_service_unavailable",
            "Media delivery is unavailable",
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
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(problem, JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.ContentLength = body.Length;
        context.Response.Headers.CacheControl =
            MediaDeliveryHttpContract.NoStoreCacheControl;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(body, cancellationToken);
        }
    }

    private static bool IsDependencyFailure(Exception exception) =>
        exception is BlobStoreException or
            InvalidOperationException or
            IOException or
            TimeoutException;
}
