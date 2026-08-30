using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Vistara.Application.Lifecycle;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Errors;
using Vistara.Contracts.Idempotency;
using Vistara.Contracts.Lifecycle;
using Vistara.Contracts.Pagination;
using Vistara.Domain.Common;

namespace Vistara.Api.Features.Lifecycle;

public static class LifecycleEndpoint
{
    private const int MaximumJsonRequestBytes = 64 * 1_024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task ListTrashAsync(
        HttpContext context,
        ILifecycleAuthorizationPort authorization,
        LifecycleService service,
        ILifecycleCursorCodec cursors,
        CancellationToken cancellationToken)
    {
        LifecycleActorContext? actor = await AuthorizeAsync(
            context,
            authorization,
            LifecycleApiOperation.ListTrash,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        if (!TryReadTrashQuery(context, cursors, out LifecycleTrashListRequest? query))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "lifecycle_query_invalid",
                "The trash query is invalid",
                cancellationToken);
            return;
        }

        Result<LifecycleTrashPage> result = await service.ListTrashAsync(
            actor,
            query!,
            cancellationToken);
        if (!result.TryGetValue(out LifecycleTrashPage? page))
        {
            await WriteResultErrorAsync(context, result.Error!, cancellationToken);
            return;
        }

        SignedCursor? nextCursor = null;
        if (page.HasMore && page.Items.Count > 0)
        {
            LifecycleTrashItemSnapshot last = page.Items[^1];
            nextCursor = new SignedCursor(cursors.Encode(new LifecycleCursor(
                last.DeletedAtUtc,
                last.AssetId,
                query!.Descending)));
        }

        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new CursorPage<TrashAssetResponse>(
                page.Items.Select(ToContract).ToArray(),
                nextCursor),
            cancellationToken);
    }

    public static async Task RestoreAsync(
        HttpContext context,
        ILifecycleAuthorizationPort authorization,
        LifecycleService service,
        CancellationToken cancellationToken)
    {
        LifecycleActorContext? actor = await AuthorizeAsync(
            context,
            authorization,
            LifecycleApiOperation.Restore,
            cancellationToken);
        if (actor is null)
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

        RestoreAssetsRequest? request =
            await ReadJsonAsync<RestoreAssetsRequest>(context, cancellationToken);
        if (request is null)
        {
            return;
        }

        Result<LifecycleJobSubmission> result = await service.SubmitRestoreAsync(
            actor,
            request.Items.Select(ToTarget).ToArray(),
            idempotencyKey.Value,
            cancellationToken);
        if (!result.TryGetValue(out LifecycleJobSubmission? submission))
        {
            await WriteResultErrorAsync(context, result.Error!, cancellationToken);
            return;
        }

        if (submission.Replayed)
        {
            context.Response.Headers["Idempotency-Replayed"] = "true";
        }

        context.Response.Headers.Location =
            $"/api/v1/jobs/{submission.JobId:D}";
        await WriteJsonAsync(
            context,
            StatusCodes.Status202Accepted,
            new OperationJobResponse(
                submission.JobId,
                submission.State,
                submission.SubmittedCount,
                submission.SubmittedAtUtc),
            cancellationToken);
    }

    public static async Task CreatePurgeDryRunAsync(
        HttpContext context,
        ILifecycleAuthorizationPort authorization,
        LifecycleService service,
        CancellationToken cancellationToken)
    {
        LifecycleActorContext? actor = await AuthorizeAsync(
            context,
            authorization,
            LifecycleApiOperation.PurgeDryRun,
            cancellationToken);
        if (actor is null)
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

        CreatePurgeDryRunRequest? request =
            await ReadJsonAsync<CreatePurgeDryRunRequest>(
                context,
                cancellationToken);
        if (request is null)
        {
            return;
        }

        Result<LifecyclePurgeDryRunSnapshot> result =
            await service.CreatePurgeDryRunAsync(
                actor,
                request.Items.Select(ToTarget).ToArray(),
                idempotencyKey.Value,
                cancellationToken);
        if (!result.TryGetValue(out LifecyclePurgeDryRunSnapshot? dryRun))
        {
            await WriteResultErrorAsync(context, result.Error!, cancellationToken);
            return;
        }

        if (dryRun.Replayed)
        {
            context.Response.Headers["Idempotency-Replayed"] = "true";
        }

        SetVersionHeaders(context, dryRun.Version);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            ToContract(dryRun),
            cancellationToken);
    }

    public static async Task ConfirmPurgeAsync(
        HttpContext context,
        Guid batchId,
        ILifecycleAuthorizationPort authorization,
        LifecycleService service,
        CancellationToken cancellationToken)
    {
        LifecycleActorContext? actor = await AuthorizeAsync(
            context,
            authorization,
            LifecycleApiOperation.PurgeConfirm,
            cancellationToken);
        if (actor is null)
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

        long? expectedVersion = await ReadExpectedVersionAsync(
            context,
            cancellationToken);
        if (expectedVersion is null)
        {
            return;
        }

        ConfirmPurgeRequest? request =
            await ReadJsonAsync<ConfirmPurgeRequest>(context, cancellationToken);
        if (request is null)
        {
            return;
        }

        Result<LifecyclePurgeBatchSnapshot> result =
            await service.ConfirmPurgeAsync(
                actor,
                batchId,
                expectedVersion.Value,
                request.DryRunDigest,
                idempotencyKey.Value,
                cancellationToken);
        if (!result.TryGetValue(out LifecyclePurgeBatchSnapshot? batch))
        {
            if (result.Error?.Code == "lifecycle.version_conflict")
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status412PreconditionFailed,
                    result.Error.Code,
                    result.Error.Message,
                    cancellationToken);
                return;
            }

            await WriteResultErrorAsync(context, result.Error!, cancellationToken);
            return;
        }

        if (batch.Replayed)
        {
            context.Response.Headers["Idempotency-Replayed"] = "true";
        }

        SetVersionHeaders(context, batch.Version);
        context.Response.Headers.Location =
            $"/api/v1/trash/purge/{batch.BatchId:D}";
        await WriteJsonAsync(
            context,
            StatusCodes.Status202Accepted,
            ToContract(batch),
            cancellationToken);
    }

    public static async Task GetPurgeBatchAsync(
        HttpContext context,
        Guid batchId,
        ILifecycleAuthorizationPort authorization,
        LifecycleService service,
        CancellationToken cancellationToken)
    {
        LifecycleActorContext? actor = await AuthorizeAsync(
            context,
            authorization,
            LifecycleApiOperation.PurgeStatus,
            cancellationToken);
        if (actor is null)
        {
            return;
        }

        Result<LifecyclePurgeBatchSnapshot> result =
            await service.GetPurgeBatchAsync(
                actor,
                batchId,
                cancellationToken);
        if (!result.TryGetValue(out LifecyclePurgeBatchSnapshot? batch))
        {
            await WriteResultErrorAsync(context, result.Error!, cancellationToken);
            return;
        }

        SetVersionHeaders(context, batch.Version);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            ToContract(batch),
            cancellationToken);
    }

    private static bool TryReadTrashQuery(
        HttpContext context,
        ILifecycleCursorCodec cursors,
        out LifecycleTrashListRequest? query)
    {
        query = null;
        int limit = CursorPageRequest.DefaultLimit;
        if (context.Request.Query.TryGetValue("limit", out StringValues limitValues) &&
            (limitValues.Count != 1 ||
             !int.TryParse(
                 limitValues[0],
                 NumberStyles.None,
                 CultureInfo.InvariantCulture,
                 out limit)))
        {
            return false;
        }

        string sort = context.Request.Query.TryGetValue(
            "sort",
            out StringValues sortValues)
            ? sortValues.ToString()
            : "deletedAt";
        string direction = context.Request.Query.TryGetValue(
            "direction",
            out StringValues directionValues)
            ? directionValues.ToString()
            : "desc";
        if (!string.Equals(sort, "deletedAt", StringComparison.Ordinal) ||
            direction is not ("asc" or "desc"))
        {
            return false;
        }

        LifecycleCursor? cursor = null;
        if (context.Request.Query.TryGetValue(
                "cursor",
                out StringValues cursorValues) &&
            (cursorValues.Count != 1 ||
             !cursors.TryDecode(cursorValues[0]!, out cursor) ||
             cursor is null ||
             cursor.Descending != (direction == "desc")))
        {
            return false;
        }

        try
        {
            query = new LifecycleTrashListRequest(
                limit,
                cursor?.DeletedAtUtc,
                cursor?.AssetId,
                direction == "desc");
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async ValueTask<LifecycleActorContext?> AuthorizeAsync(
        HttpContext context,
        ILifecycleAuthorizationPort authorization,
        LifecycleApiOperation operation,
        CancellationToken cancellationToken)
    {
        LifecycleAccess access = await authorization.AuthorizeAsync(
            context,
            operation,
            cancellationToken);
        if (access.Status == LifecycleAccessStatus.Authorized &&
            access.Actor is not null)
        {
            return access.Actor;
        }

        (int status, string code, string title) = access.Status switch
        {
            LifecycleAccessStatus.Unauthenticated =>
                (401, "authentication_required", "Authentication is required"),
            LifecycleAccessStatus.Forbidden =>
                (403, "lifecycle_forbidden", "The lifecycle operation is forbidden"),
            LifecycleAccessStatus.Concealed =>
                (404, "lifecycle_not_found", "The lifecycle resource was not found"),
            _ => throw new InvalidOperationException(
                "The lifecycle authorization result is invalid."),
        };
        await WriteProblemAsync(context, status, code, title, cancellationToken);
        return null;
    }

    private static LifecycleAssetTarget ToTarget(VersionedAssetReference item) =>
        new(item.Id, item.Version.Value);

    private static TrashAssetResponse ToContract(
        LifecycleTrashItemSnapshot item) =>
        new(
            new AssetSummaryResponse(
                item.AssetId,
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
                item.CapturedAtUtc,
                item.ImportedAtUtc,
                item.UpdatedAtUtc,
                item.Favorite,
                item.Tags.Select(tag => new AssetTagReferenceResponse(
                    tag.Id,
                    tag.Name,
                    tag.Color)).ToArray(),
                [],
                new ResourceVersion(item.Version)),
            item.DeletedAtUtc,
            item.PurgeAtUtc,
            item.Reason,
            item.ActiveHoldCount,
            item.BlockingReferenceCount,
            item.EstimatedReclaimBytes);

    private static PurgeDryRunResponse ToContract(
        LifecyclePurgeDryRunSnapshot dryRun) =>
        new(
            dryRun.BatchId,
            dryRun.State,
            dryRun.DryRunDigest,
            dryRun.ExpiresAtUtc,
            dryRun.CandidateCount,
            dryRun.EligibleCount,
            dryRun.EstimatedReclaimBytes,
            dryRun.Items.Select(item => new PurgeCandidateResponse(
                item.AssetId,
                item.RevisionNumber,
                item.Title,
                item.Eligible,
                item.Barriers,
                item.SharedLinkImpact,
                item.EstimatedReclaimBytes)).ToArray(),
            new ResourceVersion(dryRun.Version));

    private static PurgeBatchResponse ToContract(
        LifecyclePurgeBatchSnapshot batch) =>
        new(
            batch.BatchId,
            batch.State,
            batch.RequestedAtUtc,
            batch.ApprovedAtUtc,
            batch.StartedAtUtc,
            batch.CompletedAtUtc,
            batch.CandidateCount,
            batch.EligibleCount,
            batch.ProcessedCount,
            batch.ReclaimedBytes,
            batch.Items.Select(item => new PurgeItemResultResponse(
                item.AssetId,
                item.RevisionNumber,
                item.Result,
                item.ReclaimedBytes,
                item.ErrorCode)).ToArray(),
            new ResourceVersion(batch.Version));

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
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':')))
        {
            return false;
        }

        idempotencyKey = new IdempotencyKey(value);
        return true;
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

        if (values.Count != 1)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "lifecycle_version_conflict",
                "The lifecycle version does not match",
                cancellationToken);
            return null;
        }

        string value = values[0]!;
        if (value.Length < 4 ||
            value[0] != '"' ||
            value[1] != 'v' ||
            value[^1] != '"' ||
            !long.TryParse(
                value.AsSpan(2, value.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long version) ||
            version <= 0)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "lifecycle_version_conflict",
                "The lifecycle version does not match",
                cancellationToken);
            return null;
        }

        return version;
    }

    private static async ValueTask<T?> ReadJsonAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is > MaximumJsonRequestBytes ||
            context.Request.ContentType is null ||
            !context.Request.ContentType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "lifecycle_request_invalid",
                "The lifecycle request is invalid",
                cancellationToken);
            return null;
        }

        try
        {
            using var bounded = new LifecycleMaximumLengthReadStream(
                context.Request.Body,
                MaximumJsonRequestBytes);
            T? request = await JsonSerializer.DeserializeAsync<T>(
                bounded,
                JsonOptions,
                cancellationToken);
            if (request is null)
            {
                throw new JsonException("A request body is required.");
            }

            return request;
        }
        catch (JsonException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "lifecycle_request_invalid",
                "The lifecycle request is invalid",
                cancellationToken);
            return null;
        }
        catch (LifecycleRequestTooLargeException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "lifecycle_request_too_large",
                "The lifecycle request is too large",
                cancellationToken);
            return null;
        }
    }

    private static async Task WriteResultErrorAsync(
        HttpContext context,
        ResultError error,
        CancellationToken cancellationToken)
    {
        int status = error.Category switch
        {
            ErrorCategory.Validation => StatusCodes.Status422UnprocessableEntity,
            ErrorCategory.NotFound => StatusCodes.Status404NotFound,
            ErrorCategory.Conflict => StatusCodes.Status409Conflict,
            ErrorCategory.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCategory.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
        await WriteProblemAsync(
            context,
            status,
            error.Code,
            error.Message,
            cancellationToken);
    }

    private static void SetVersionHeaders(HttpContext context, long version)
    {
        context.Response.Headers.ETag =
            $"\"v{version.ToString(CultureInfo.InvariantCulture)}\"";
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
            $"https://vistara.dev/problems/{code.Replace('.', '-')}",
            title,
            status,
            new ErrorCode(code.Replace('.', '_')),
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
        T value,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            JsonOptions,
            cancellationToken);
    }
}

internal sealed class LifecycleMaximumLengthReadStream(
    Stream inner,
    long maximumLength) : Stream
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int permitted = PermittedCount(count);
        int read = inner.Read(buffer, offset, permitted);
        Record(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int permitted = PermittedCount(buffer.Length);
        int read = await inner.ReadAsync(
            buffer[..permitted],
            cancellationToken);
        Record(read);
        return read;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private int PermittedCount(int requested)
    {
        if (requested == 0)
        {
            return 0;
        }

        long remainingWithProbe = checked(maximumLength - _read + 1);
        return checked((int)Math.Min(requested, remainingWithProbe));
    }

    private void Record(int read)
    {
        _read = checked(_read + read);
        if (_read > maximumLength)
        {
            throw new LifecycleRequestTooLargeException();
        }
    }
}

internal sealed class LifecycleRequestTooLargeException : Exception;
