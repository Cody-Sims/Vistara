using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vistara.Application.Gallery.Queries;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Errors;
using Vistara.Contracts.Pagination;

namespace Vistara.Api.Features.Assets;

public static class AssetEndpoint
{
    private const int MaximumRequestBytes = 16 * 1_024;
    private static readonly HashSet<string> AssetQueryParameters =
        new(
        [
            "limit",
            "cursor",
            "search",
            "statuses",
            "contentTypes",
            "albumId",
            "tagIds",
            "favorite",
            "capturedFrom",
            "capturedTo",
            "importedFrom",
            "importedTo",
            "sort",
            "direction",
        ],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task ListAsync(
        HttpContext context,
        IAssetQueryAuthorizationPort authorization,
        IAssetQueryService application,
        CancellationToken cancellationToken)
    {
        AssetQueryScope? scope = await AuthorizeCollectionAsync(
            context,
            authorization,
            cancellationToken);
        if (scope is null)
        {
            return;
        }

        if (!TryReadCriteria(context, out AssetQueryCriteria? criteria, out string? cursor))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "asset_query_invalid",
                "The asset query is invalid",
                cancellationToken);
            return;
        }

        try
        {
            AssetQueryPageResult result = await application.ListAsync(
                scope,
                criteria!,
                cursor,
                cancellationToken);
            await WritePageResultAsync(context, result, cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "asset_query_unavailable",
                "Asset queries are unavailable",
                cancellationToken);
        }
    }

    public static async Task TimelineAsync(
        HttpContext context,
        IAssetQueryAuthorizationPort authorization,
        IAssetQueryService application,
        CancellationToken cancellationToken)
    {
        AssetQueryScope? scope = await AuthorizeCollectionAsync(
            context,
            authorization,
            cancellationToken);
        if (scope is null)
        {
            return;
        }

        if (!TryReadCriteria(
                context,
                out AssetQueryCriteria? criteria,
                out string? cursor,
                allowGroupBy: true))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "asset_query_invalid",
                "The timeline query is invalid",
                cancellationToken);
            return;
        }

        string groupBy = context.Request.Query["groupBy"].ToString();
        groupBy = string.IsNullOrWhiteSpace(groupBy) ? "day" : groupBy;
        try
        {
            AssetQueryPageResult result = await application.TimelineAsync(
                scope,
                criteria!,
                groupBy,
                cursor,
                cancellationToken);
            if (result.Status != AssetQueryResultStatus.Success || result.Page is null)
            {
                await WriteQueryFailureAsync(context, result.Status, cancellationToken);
                return;
            }

            TimelineGroupResponse[] groups = GroupTimeline(result.Page.Items, groupBy);
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new TimelinePageResponse(
                    groups,
                    ToSignedCursor(result.Page.NextCursor)),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "asset_query_unavailable",
                "Asset queries are unavailable",
                cancellationToken);
        }
    }

    public static async Task FacetsAsync(
        HttpContext context,
        IAssetQueryAuthorizationPort authorization,
        IAssetQueryService application,
        CancellationToken cancellationToken)
    {
        AssetQueryScope? scope = await AuthorizeCollectionAsync(
            context,
            authorization,
            cancellationToken);
        if (scope is null)
        {
            return;
        }

        if (!TryReadCriteria(context, out AssetQueryCriteria? criteria, out _))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "asset_query_invalid",
                "The facet query is invalid",
                cancellationToken);
            return;
        }

        try
        {
            AssetFacetResult result = await application.GetFacetsAsync(
                scope,
                criteria!,
                cancellationToken);
            if (result.Status != AssetQueryResultStatus.Success ||
                result.Groups is null)
            {
                await WriteQueryFailureAsync(context, result.Status, cancellationToken);
                return;
            }

            var response = new SearchFacetsResponse(
                result.Groups.Select(group => new SearchFacetGroupResponse(
                    group.Name,
                    group.Values.Select(value => ToFacetValue(group.Name, value))
                        .ToArray(),
                    group.Truncated)).ToArray());
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
                "asset_query_unavailable",
                "Asset queries are unavailable",
                cancellationToken);
        }
    }

    /// <summary>
    /// A facet value is the exact filter argument a client sends back, so the
    /// status facet publishes the documented <c>AssetQueryStatus</c> token and
    /// keeps the stored enum name out of the wire; the label carries the
    /// readable form instead.
    /// </summary>
    private static SearchFacetValueResponse ToFacetValue(
        string groupName,
        AssetFacetValue value) =>
        string.Equals(groupName, "status", StringComparison.Ordinal)
            ? new SearchFacetValueResponse(
                AssetContractVocabulary.PublishQueryStatus(value.Value),
                AssetContractVocabulary.DisplayQueryStatus(value.Value),
                value.Count)
            : new SearchFacetValueResponse(value.Value, value.Label, value.Count);

    public static async Task GetAsync(
        HttpContext context,
        Guid assetId,
        IAssetQueryAuthorizationPort authorization,
        IAssetQueryService application,
        CancellationToken cancellationToken)
    {
        (AssetQueryScope? scope, _) = await AuthorizeAssetAsync(
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
            AssetDetailResult result =
                await application.GetAsync(scope, assetId, cancellationToken);
            await WriteDetailResultAsync(context, result, cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "asset_query_unavailable",
                "Asset queries are unavailable",
                cancellationToken);
        }
    }

    public static async Task GetMetadataAsync(
        HttpContext context,
        Guid assetId,
        IAssetQueryAuthorizationPort authorization,
        IAssetQueryService application,
        CancellationToken cancellationToken)
    {
        (AssetQueryScope? scope, AssetQueryAccess? access) =
            await AuthorizeAssetAsync(
                context,
                assetId,
                authorization,
                cancellationToken);
        if (scope is null || access is null)
        {
            return;
        }

        if (!access.CanReadRestrictedMetadata)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        try
        {
            AssetMetadataResult result = await application.GetMetadataAsync(
                scope,
                assetId,
                cancellationToken);
            if (result.Status != AssetQueryResultStatus.Success ||
                result.Metadata is null)
            {
                await WriteQueryFailureAsync(context, result.Status, cancellationToken);
                return;
            }

            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                ToMetadataResponse(result.Metadata),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "asset_query_unavailable",
                "Asset queries are unavailable",
                cancellationToken);
        }
    }

    public static async Task UpdateAsync(
        HttpContext context,
        Guid assetId,
        IAssetQueryAuthorizationPort authorization,
        IAssetQueryService application,
        CancellationToken cancellationToken)
    {
        (AssetQueryScope? scope, AssetQueryAccess? access) =
            await AuthorizeAssetAsync(
                context,
                assetId,
                authorization,
                cancellationToken);
        if (scope is null || access is null)
        {
            return;
        }

        if (!access.CanUpdateMetadata)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        if (!TryReadIfMatch(context.Request.Headers.IfMatch, out long expectedVersion))
        {
            bool missing = context.Request.Headers.IfMatch.Count == 0;
            await WriteProblemAsync(
                context,
                missing
                    ? StatusCodes.Status428PreconditionRequired
                    : StatusCodes.Status400BadRequest,
                missing ? "if_match_required" : "if_match_invalid",
                missing
                    ? "A canonical If-Match header is required"
                    : "The If-Match header is invalid",
                cancellationToken);
            return;
        }

        if (!TryReadIdempotencyKey(
                context.Request.Headers["Idempotency-Key"],
                out string? idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_idempotency_key",
                "A valid Idempotency-Key header is required",
                cancellationToken);
            return;
        }

        AssetMetadataPatch? patch = await ReadPatchAsync(context, cancellationToken);
        if (patch is null)
        {
            return;
        }

        try
        {
            AssetUpdateResult result = await application.UpdateAsync(
                scope,
                assetId,
                expectedVersion,
                idempotencyKey!,
                patch,
                cancellationToken);
            if (result.Status == AssetQueryResultStatus.VersionConflict)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status412PreconditionFailed,
                    "asset_version_conflict",
                    "The asset version does not match",
                    cancellationToken);
                return;
            }

            if (result.Status != AssetQueryResultStatus.Success ||
                result.Detail is null)
            {
                await WriteQueryFailureAsync(context, result.Status, cancellationToken);
                return;
            }

            SetEntityHeaders(context, result.Detail.Asset.Version);
            if (result.Replayed)
            {
                context.Response.Headers["Idempotency-Replayed"] = "true";
            }

            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                ToDetailResponse(result.Detail),
                cancellationToken);
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "asset_query_unavailable",
                "Asset queries are unavailable",
                cancellationToken);
        }
    }

    private static async ValueTask<AssetQueryScope?> AuthorizeCollectionAsync(
        HttpContext context,
        IAssetQueryAuthorizationPort authorization,
        CancellationToken cancellationToken)
    {
        AssetQueryAccess access = await authorization.AuthorizeCollectionAsync(
            context,
            cancellationToken);
        return await ToScopeAsync(context, access, cancellationToken);
    }

    private static async ValueTask<(AssetQueryScope?, AssetQueryAccess?)>
        AuthorizeAssetAsync(
            HttpContext context,
            Guid assetId,
            IAssetQueryAuthorizationPort authorization,
            CancellationToken cancellationToken)
    {
        AssetQueryAccess access = await authorization.AuthorizeAssetAsync(
            context,
            assetId,
            cancellationToken);
        AssetQueryScope? scope =
            await ToScopeAsync(context, access, cancellationToken);
        return (scope, scope is null ? null : access);
    }

    private static async ValueTask<AssetQueryScope?> ToScopeAsync(
        HttpContext context,
        AssetQueryAccess access,
        CancellationToken cancellationToken)
    {
        if (access.Status == AssetQueryAccessStatus.Authorized &&
            access.TenantId is Guid tenantId &&
            access.ActorId is Guid actorId)
        {
            return new AssetQueryScope(tenantId, actorId);
        }

        (int status, string code, string title) = access.Status switch
        {
            AssetQueryAccessStatus.Unauthenticated => (
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required"),
            AssetQueryAccessStatus.Forbidden => (
                StatusCodes.Status403Forbidden,
                "asset_query_forbidden",
                "Asset access is forbidden"),
            _ => (
                StatusCodes.Status404NotFound,
                "asset_not_found",
                "The requested resource was not found"),
        };
        await WriteProblemAsync(
            context,
            status,
            code,
            title,
            cancellationToken);
        return null;
    }

    private static bool TryReadCriteria(
        HttpContext context,
        out AssetQueryCriteria? criteria,
        out string? cursor,
        bool allowGroupBy = false)
    {
        criteria = null;
        cursor = NullIfEmpty(context.Request.Query["cursor"].ToString());
        try
        {
            if (context.Request.Query.Keys.Any(key =>
                    !AssetQueryParameters.Contains(key) &&
                    !(allowGroupBy &&
                        string.Equals(key, "groupBy", StringComparison.Ordinal))))
            {
                return false;
            }

            int limit = 60;
            string? limitValue = NullIfEmpty(
                context.Request.Query["limit"].ToString());
            if (limitValue is not null &&
                !int.TryParse(
                    limitValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out limit))
            {
                return false;
            }

            criteria = AssetQueryCriteria.Create(
                limit,
                NullIfEmpty(context.Request.Query["search"].ToString()),
                ReadQueryStatuses(context.Request.Query["statuses"]),
                ReadStrings(context.Request.Query["contentTypes"]),
                ReadGuid(context.Request.Query["albumId"]),
                ReadGuids(context.Request.Query["tagIds"]),
                ReadBoolean(context.Request.Query["favorite"]),
                ReadInstant(context.Request.Query["capturedFrom"]),
                ReadInstant(context.Request.Query["capturedTo"]),
                ReadInstant(context.Request.Query["importedFrom"]),
                ReadInstant(context.Request.Query["importedTo"]),
                NullIfEmpty(context.Request.Query["sort"].ToString()) ?? "capturedAt",
                NullIfEmpty(context.Request.Query["direction"].ToString()) ?? "desc");
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static string[]? ReadStrings(StringValues values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        return values
            .SelectMany(value => (value ?? string.Empty).Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
    }

    private static Guid? ReadGuid(StringValues values)
    {
        string? value = NullIfEmpty(values.ToString());
        return value is null ? null : Guid.Parse(value);
    }

    /// <summary>
    /// Mirrors the update seam: the filter accepts exactly the documented
    /// <c>AssetQueryStatus</c> tokens and translates them to the stored enum
    /// names, so no request-rewriting middleware has to guess at casing.
    /// </summary>
    private static string[]? ReadQueryStatuses(StringValues values) =>
        ReadStrings(values)?
            .Select(token =>
                AssetContractVocabulary.TryReadQueryStatus(
                    token,
                    out string storedValue)
                    ? storedValue
                    : throw new ArgumentException(
                        "The asset status filter is unsupported.",
                        nameof(values)))
            .ToArray();

    private static Guid[]? ReadGuids(StringValues values) =>
        ReadStrings(values)?.Select(Guid.Parse).ToArray();

    private static bool? ReadBoolean(StringValues values)
    {
        string? value = NullIfEmpty(values.ToString());
        return value is null ? null : bool.Parse(value);
    }

    private static DateTimeOffset? ReadInstant(StringValues values)
    {
        string? value = NullIfEmpty(values.ToString());
        return value is null
            ? null
            : DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
    }

    private static async ValueTask<AssetMetadataPatch?> ReadPatchAsync(
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
                "asset_update_invalid",
                "The asset update is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("An object is required.");
            }

            bool hasTitle = false;
            string? title = null;
            bool hasDescription = false;
            string? description = null;
            bool hasVisibility = false;
            string? visibility = null;
            bool hasCapturedAt = false;
            DateTimeOffset? capturedAt = null;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "title":
                        hasTitle = true;
                        title = property.Value.GetString();
                        break;
                    case "description":
                        hasDescription = true;
                        description = property.Value.ValueKind == JsonValueKind.Null
                            ? null
                            : property.Value.GetString();
                        break;
                    case "visibility":
                        hasVisibility = true;
                        visibility = ReadVisibility(property.Value.GetString());
                        break;
                    case "capturedAt":
                        hasCapturedAt = true;
                        capturedAt = property.Value.ValueKind == JsonValueKind.Null
                            ? null
                            : property.Value.GetDateTimeOffset();
                        break;
                    default:
                        throw new JsonException("An unsupported property was supplied.");
                }
            }

            if (!hasTitle &&
                !hasDescription &&
                !hasVisibility &&
                !hasCapturedAt)
            {
                throw new JsonException("At least one field is required.");
            }

            return new AssetMetadataPatch(
                hasTitle,
                title,
                hasDescription,
                description,
                hasVisibility,
                visibility,
                hasCapturedAt,
                capturedAt);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidOperationException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "asset_update_invalid",
                "The asset update is invalid",
                cancellationToken);
            return null;
        }
    }

    /// <summary>
    /// The contract publishes and accepts the documented lower-camel visibility
    /// tokens, while the store keeps the domain enum name, so an update is
    /// translated once here instead of leaking either casing across the seam.
    /// </summary>
    private static string ReadVisibility(string? token) =>
        AssetContractVocabulary.TryReadVisibility(token, out string storedValue)
            ? storedValue
            : throw new JsonException("An unsupported visibility was supplied.");

    private static bool TryReadIfMatch(
        StringValues values,
        out long expectedVersion)
    {
        expectedVersion = 0;
        if (values.Count != 1)
        {
            return false;
        }

        try
        {
            expectedVersion = EntityTag.Parse(values[0]!).Version.Value;
            return expectedVersion >= 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool TryReadIdempotencyKey(
        StringValues values,
        out string? key)
    {
        key = values.Count == 1 ? values[0] : null;
        return key is not null &&
            key.Length is > 0 and <= 128 &&
            key.All(character => character is >= '!' and <= '~');
    }

    private static async ValueTask WritePageResultAsync(
        HttpContext context,
        AssetQueryPageResult result,
        CancellationToken cancellationToken)
    {
        if (result.Status != AssetQueryResultStatus.Success || result.Page is null)
        {
            await WriteQueryFailureAsync(context, result.Status, cancellationToken);
            return;
        }

        var page = new CursorPage<AssetSummaryResponse>(
            result.Page.Items.Select(ToSummaryResponse).ToArray(),
            ToSignedCursor(result.Page.NextCursor));
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            page,
            cancellationToken);
    }

    private static async ValueTask WriteDetailResultAsync(
        HttpContext context,
        AssetDetailResult result,
        CancellationToken cancellationToken)
    {
        if (result.Status != AssetQueryResultStatus.Success || result.Detail is null)
        {
            await WriteQueryFailureAsync(context, result.Status, cancellationToken);
            return;
        }

        SetEntityHeaders(context, result.Detail.Asset.Version);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            ToDetailResponse(result.Detail),
            cancellationToken);
    }

    private static async ValueTask WriteQueryFailureAsync(
        HttpContext context,
        AssetQueryResultStatus status,
        CancellationToken cancellationToken)
    {
        (int httpStatus, string code, string title) = status switch
        {
            AssetQueryResultStatus.InvalidQuery => (
                StatusCodes.Status400BadRequest,
                "asset_query_invalid",
                "The asset query is invalid"),
            AssetQueryResultStatus.InvalidCursor => (
                StatusCodes.Status400BadRequest,
                "asset_cursor_invalid",
                "The asset cursor is invalid"),
            AssetQueryResultStatus.CursorMismatch => (
                StatusCodes.Status409Conflict,
                "asset_cursor_mismatch",
                "The asset cursor does not match this query"),
            AssetQueryResultStatus.NotFound => (
                StatusCodes.Status404NotFound,
                "asset_not_found",
                "The requested resource was not found"),
            AssetQueryResultStatus.ValidationFailed => (
                StatusCodes.Status422UnprocessableEntity,
                "asset_update_invalid",
                "The asset update is invalid"),
            _ => (
                StatusCodes.Status503ServiceUnavailable,
                "asset_query_unavailable",
                "Asset queries are unavailable"),
        };
        await WriteProblemAsync(
            context,
            httpStatus,
            code,
            title,
            cancellationToken);
    }

    private static AssetDetailResponse ToDetailResponse(AssetDetail detail) =>
        new(
            ToSummaryResponse(detail.Asset),
            ToMetadataSummary(detail.Metadata),
            detail.Albums.Select(album =>
                new AssetAlbumReferenceResponse(album.Id, album.Name)).ToArray());

    private static AssetSummaryResponse ToSummaryResponse(AssetQueryItem item) =>
        new(
            item.Id,
            item.Title,
            item.Description,
            item.Status,
            item.Visibility,
            item.RevisionNumber,
            item.ContentType,
            item.Format,
            item.Width,
            item.Height,
            item.SizeBytes,
            item.CapturedAt,
            item.ImportedAt,
            item.UpdatedAt,
            item.Favorite,
            item.Tags.Select(tag =>
                new AssetTagReferenceResponse(tag.Id, tag.Name, tag.Color)).ToArray(),
            item.Renditions.Select(rendition =>
                new AssetRenditionResponse(
                    rendition.Kind,
                    rendition.Path,
                    rendition.Width,
                    rendition.Height,
                    rendition.ContentType)).ToArray(),
            new ResourceVersion(item.Version));

    private static AssetMetadataResponse ToMetadataResponse(AssetMetadata metadata) =>
        new(
            metadata.AssetId,
            metadata.RevisionNumber,
            ToMetadataSummary(metadata),
            metadata.SafeProperties);

    private static AssetMetadataSummaryResponse ToMetadataSummary(
        AssetMetadata metadata) =>
        new(
            metadata.CapturedAt,
            metadata.Orientation,
            metadata.CameraMake,
            metadata.CameraModel,
            metadata.LensModel,
            metadata.ColorSpace,
            metadata.RestrictedMetadataAvailable);

    private static TimelineGroupResponse[] GroupTimeline(
        IReadOnlyList<AssetQueryItem> items,
        string groupBy) =>
        items.GroupBy(item =>
            TimelineRange(item.CapturedAt ?? item.ImportedAt, groupBy))
            .Select(group => new TimelineGroupResponse(
                group.Key.Label,
                group.Key.Label,
                group.Key.Start,
                group.Key.End,
                group.Select(ToSummaryResponse).ToArray()))
            .ToArray();

    private static (DateTimeOffset Start, DateTimeOffset End, string Label)
        TimelineRange(DateTimeOffset value, string groupBy)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        DateTimeOffset start = groupBy switch
        {
            "year" => new DateTimeOffset(utc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "month" => new DateTimeOffset(
                utc.Year,
                utc.Month,
                1,
                0,
                0,
                0,
                TimeSpan.Zero),
            _ => new DateTimeOffset(
                utc.Year,
                utc.Month,
                utc.Day,
                0,
                0,
                0,
                TimeSpan.Zero),
        };
        DateTimeOffset end = groupBy switch
        {
            "year" => start.AddYears(1),
            "month" => start.AddMonths(1),
            _ => start.AddDays(1),
        };
        string label = groupBy switch
        {
            "year" => start.ToString("yyyy", CultureInfo.InvariantCulture),
            "month" => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        return (start, end, label);
    }

    private static SignedCursor? ToSignedCursor(string? cursor) =>
        cursor is null ? null : new SignedCursor(cursor);

    private static void SetEntityHeaders(HttpContext context, long version)
    {
        context.Response.Headers.ETag =
            new EntityTag(new ResourceVersion(version)).ToString();
        context.Response.Headers.CacheControl = "no-store";
    }

    private static async ValueTask WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        await WriteProblemAsync(
            context,
            StatusCodes.Status404NotFound,
            "asset_not_found",
            "The requested resource was not found",
            cancellationToken);

    private static async ValueTask WriteJsonAsync<T>(
        HttpContext context,
        int status,
        T body,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            body,
            JsonOptions,
            cancellationToken);
    }

    private static async ValueTask WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        CancellationToken cancellationToken)
    {
        var problem = new ApiProblemDetails(
            $"https://vistara.dev/problems/{code}",
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

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsDependencyFailure(Exception exception) =>
        exception is not OperationCanceledException;
}
