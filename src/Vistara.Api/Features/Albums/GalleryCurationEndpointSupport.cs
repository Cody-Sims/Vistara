using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vistara.Application.Gallery;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Errors;
using Vistara.Contracts.Gallery;

namespace Vistara.Api.Features.Albums;

public enum GalleryCurationAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
    Concealed,
}

public enum GalleryCurationOperation
{
    ReadAlbums,
    CreateAlbum,
    ManageAlbum,
    ReadTags,
    ManageTags,
    ManageAssetTags,
    ManageFavorites,
    BulkMutate,
}

public sealed record GalleryCurationAccess
{
    private GalleryCurationAccess(
        GalleryCurationAccessStatus status,
        CurationActor? actor)
    {
        Status = status;
        Actor = actor;
    }

    public GalleryCurationAccessStatus Status { get; }

    public CurationActor? Actor { get; }

    public static GalleryCurationAccess Authorized(CurationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(GalleryCurationAccessStatus.Authorized, actor);
    }

    public static GalleryCurationAccess Denied(GalleryCurationAccessStatus status)
    {
        if (status == GalleryCurationAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new(status, null);
    }
}

public interface IGalleryCurationAuthorizationPort
{
    ValueTask<GalleryCurationAccess> AuthorizeAsync(
        HttpContext context,
        GalleryCurationOperation operation,
        Guid? resourceId,
        CancellationToken cancellationToken);
}

internal static class GalleryCurationEndpointSupport
{
    internal const string PolicyName = "Vistara.GalleryCuration";
    private const int MaximumRequestBytes = 64 * 1_024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static async ValueTask<CurationActor?> AuthorizeAsync(
        HttpContext context,
        IGalleryCurationAuthorizationPort authorization,
        GalleryCurationOperation operation,
        Guid? resourceId,
        CancellationToken cancellationToken)
    {
        GalleryCurationAccess access = await authorization.AuthorizeAsync(
            context,
            operation,
            resourceId,
            cancellationToken);
        if (access.Status == GalleryCurationAccessStatus.Authorized)
        {
            return access.Actor ??
                throw new InvalidOperationException("Authorized access requires an actor.");
        }

        int status = access.Status switch
        {
            GalleryCurationAccessStatus.Unauthenticated =>
                StatusCodes.Status401Unauthorized,
            GalleryCurationAccessStatus.Forbidden =>
                StatusCodes.Status403Forbidden,
            GalleryCurationAccessStatus.Concealed =>
                StatusCodes.Status404NotFound,
            _ => throw new InvalidOperationException("Unknown authorization status."),
        };
        await WriteProblemAsync(
            context,
            status,
            status == StatusCodes.Status404NotFound
                ? "resource_not_found"
                : status == StatusCodes.Status403Forbidden
                    ? "forbidden"
                    : "unauthenticated",
            status == StatusCodes.Status404NotFound
                ? "The resource was not found"
                : "The request is not authorized",
            cancellationToken);
        return null;
    }

    internal static async ValueTask<T?> ReadRequestAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is > MaximumRequestBytes)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "request_invalid",
                "The request body is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            T? request = await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                JsonOptions,
                cancellationToken);
            if (request is null)
            {
                throw new JsonException();
            }

            return request;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "request_invalid",
                "The request body is invalid",
                cancellationToken);
            return null;
        }
    }

    internal static async ValueTask<JsonDocument?> ReadDocumentAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > MaximumRequestBytes)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "request_invalid",
                "The request body is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            return await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "request_invalid",
                "The request body is invalid",
                cancellationToken);
            return null;
        }
    }

    internal static async ValueTask<string?> ReadIdempotencyKeyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        StringValues values = context.Request.Headers["Idempotency-Key"];
        string? value = values.Count == 1 ? values[0] : null;
        bool valid = value is { Length: > 0 and <= 255 } &&
            value.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
        if (valid)
        {
            return value;
        }

        await WriteProblemAsync(
            context,
            StatusCodes.Status400BadRequest,
            "invalid_idempotency_key",
            "A valid Idempotency-Key header is required",
            cancellationToken);
        return null;
    }

    internal static async ValueTask<long?> ReadExpectedVersionAsync(
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

        try
        {
            string? value = values.Count == 1 ? values[0] : null;
            return value is null ? throw new FormatException() : EntityTag.Parse(value).Version.Value;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "version_conflict",
                "The resource version does not match",
                cancellationToken);
            return null;
        }
    }

    internal static async Task WriteAlbumAsync(
        HttpContext context,
        int status,
        AlbumSnapshot album,
        CancellationToken cancellationToken)
    {
        SetVersionHeaders(context, album.Version);
        await WriteJsonAsync(
            context,
            status,
            ToContract(album),
            cancellationToken);
    }

    internal static async Task WriteTagAsync(
        HttpContext context,
        int status,
        TagSnapshot tag,
        CancellationToken cancellationToken)
    {
        SetVersionHeaders(context, tag.Version);
        await WriteJsonAsync(
            context,
            status,
            ToContract(tag),
            cancellationToken);
    }

    internal static async Task WriteAssetAsync(
        HttpContext context,
        CuratedAssetSnapshot asset,
        CancellationToken cancellationToken)
    {
        SetVersionHeaders(context, asset.Version);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            ToContract(asset),
            cancellationToken);
    }

    internal static async Task<bool> WriteFailureAsync<T>(
        HttpContext context,
        CurationResult<T> result,
        CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            return false;
        }

        CurationFailure failure = result.Error!;
        int status = failure.Kind switch
        {
            CurationFailureKind.Invalid => StatusCodes.Status422UnprocessableEntity,
            CurationFailureKind.NotFound => StatusCodes.Status404NotFound,
            CurationFailureKind.Forbidden => StatusCodes.Status403Forbidden,
            CurationFailureKind.Conflict when
                failure.Code.EndsWith("_version_conflict", StringComparison.Ordinal) =>
                StatusCodes.Status412PreconditionFailed,
            CurationFailureKind.Conflict => StatusCodes.Status409Conflict,
            CurationFailureKind.IdempotencyConflict => StatusCodes.Status409Conflict,
            CurationFailureKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        await WriteProblemAsync(
            context,
            status,
            failure.Code,
            "The gallery curation request could not be completed",
            cancellationToken);
        return true;
    }

    internal static async Task WriteJsonAsync<T>(
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

    internal static async Task WriteProblemAsync(
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
            context.TraceIdentifier);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            JsonOptions,
            cancellationToken);
    }

    internal static AlbumDetailResponse ToContract(AlbumSnapshot album) =>
        new(
            new AlbumSummaryResponse(
                album.Id,
                album.Name,
                album.Description,
                album.Cover is null ? null : ToContract(album.Cover),
                album.ItemCount,
                album.UpdatedAt,
                new ResourceVersion(album.Version)),
            new Contracts.Pagination.CursorPage<AlbumItemResponse>(
                album.Items.Select(item => new AlbumItemResponse(
                    ToSummaryContract(item.Asset),
                    item.Position,
                    item.AddedAt)).ToArray()));

    internal static TagResponse ToContract(TagSnapshot tag) =>
        new(
            tag.Id,
            tag.Name,
            tag.Color,
            tag.AssetCount,
            new ResourceVersion(tag.Version));

    internal static AssetDetailResponse ToContract(CuratedAssetSnapshot asset) =>
        new(
            ToSummaryContract(asset),
            new AssetMetadataSummaryResponse(
                asset.CapturedAt,
                null,
                null,
                null,
                null,
                null,
                false),
            (asset.Albums ?? []).Select(album =>
                new AssetAlbumReferenceResponse(album.Id, album.Name)).ToArray());

    private static AssetSummaryResponse ToSummaryContract(CuratedAssetSnapshot asset) =>
        new(
            asset.Id,
            asset.Title,
            asset.Description,
            asset.Status,
            asset.Visibility,
            asset.RevisionNumber,
            asset.ContentType,
            asset.Format,
            asset.Width,
            asset.Height,
            asset.SizeBytes,
            asset.CapturedAt,
            asset.ImportedAt,
            asset.UpdatedAt,
            asset.Favorite,
            asset.Tags.Select(tag =>
                new AssetTagReferenceResponse(tag.Id, tag.Name, tag.Color)).ToArray(),
            asset.Renditions.Select(ToContract).ToArray(),
            new ResourceVersion(asset.Version));

    private static AssetRenditionResponse ToContract(CuratedRenditionSnapshot rendition) =>
        new(
            rendition.Kind,
            rendition.Path,
            rendition.Width,
            rendition.Height,
            rendition.ContentType);

    private static void SetVersionHeaders(HttpContext context, long version)
    {
        context.Response.Headers.ETag =
            $"\"v{version.ToString(CultureInfo.InvariantCulture)}\"";
        context.Response.Headers.CacheControl = "no-store";
    }
}
