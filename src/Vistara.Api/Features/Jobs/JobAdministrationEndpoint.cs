using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Contracts.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;

namespace Vistara.Api.Features.Jobs;

/// <summary>
/// Tenant-scoped job administration for <c>GET /api/v1/jobs</c> and the
/// operator actions on a single job.
/// </summary>
public static class JobAdministrationEndpoint
{
    private const string CodePrefix = "jobs";

    private static readonly string[] KnownStates =
    [
        nameof(JobState.Pending),
        nameof(JobState.Leased),
        nameof(JobState.RetryScheduled),
        nameof(JobState.Completed),
        nameof(JobState.DeadLettered),
    ];

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task ListAsync(
        HttpContext context,
        IJobStatusAuthorizationPort authorization,
        IJobAdministrationPort administration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(administration);

        JobAccess access = await authorization.AuthorizeAsync(context, cancellationToken);
        if (access.Status != JobAccessStatus.Authorized)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        IQueryCollection query = context.Request.Query;
        string[] states = query["states"]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (states.Any(state => !KnownStates.Contains(state, StringComparer.Ordinal)))
        {
            await WriteValidationAsync(context, "states", cancellationToken);
            return;
        }

        string? type = query["type"].FirstOrDefault();
        if (type is not null &&
            (type.Length == 0 || type.Length > JobType.MaximumLength))
        {
            await WriteValidationAsync(context, "type", cancellationToken);
            return;
        }

        if (!AdminPaging.TryReadLimit(query["limit"].FirstOrDefault(), out int limit))
        {
            await WriteValidationAsync(context, "limit", cancellationToken);
            return;
        }

        string fingerprint = AdminCursor.Fingerprint(
            "jobs",
            string.Join(',', states.Order(StringComparer.Ordinal)),
            type);
        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;
        string? rawCursor = query["cursor"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(rawCursor))
        {
            if (!AdminCursor.TryDecode(
                    rawCursor,
                    access.TenantId,
                    fingerprint,
                    out AdminCursor cursor))
            {
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    $"{CodePrefix}.cursor_mismatch",
                    "The cursor belongs to a different tenant or query.",
                    cancellationToken);
                return;
            }

            afterCreatedAt = new DateTimeOffset(cursor.Ticks, TimeSpan.Zero);
            afterId = cursor.Id;
        }

        JobListPage page = await administration.ListAsync(
            new JobListQuery(
                access.TenantId,
                states,
                type,
                limit,
                afterCreatedAt,
                afterId),
            cancellationToken);
        string? nextCursor =
            page.NextCreatedAtUtc is { } next && page.NextJobId is { } nextId
                ? new AdminCursor(
                    access.TenantId,
                    fingerprint,
                    next.UtcTicks,
                    nextId).Encode()
                : null;
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new JobCollectionResponse(
                page.Items.Select(JobStatusEndpoint.Map).ToArray(),
                nextCursor),
            cancellationToken);
    }

    public static async Task RetryAsync(
        HttpContext context,
        Guid jobId,
        IJobStatusAuthorizationPort authorization,
        IJobAdministrationPort administration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(administration);

        JobAccess access = await authorization.AuthorizeAsync(context, cancellationToken);
        if (access.Status != JobAccessStatus.Authorized)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        if (jobId == Guid.Empty || jobId.Version != 7)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        IfMatchCondition condition = ApiConcurrency.ReadIfMatch(context.Request);
        if (!await ApiConcurrency.RequirePreconditionAsync(
                context,
                condition,
                CodePrefix,
                cancellationToken))
        {
            return;
        }

        if (condition.Kind == IfMatchKind.Wildcard)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status428PreconditionRequired,
                $"{CodePrefix}.if_match_required",
                "A job action requires the exact job version, not a wildcard.",
                cancellationToken);
            return;
        }

        Result<JobSnapshot> retried = await administration.RetryAsync(
            access.TenantId,
            jobId,
            condition.Version,
            cancellationToken);
        if (!retried.TryGetValue(out JobSnapshot? snapshot))
        {
            switch (retried.Error!.Code)
            {
                case "jobs.not_found":
                    await WriteNotFoundAsync(context, cancellationToken);
                    return;
                case "jobs.version_conflict":
                    await ApiConcurrency.WriteStaleAsync(
                        context,
                        CodePrefix,
                        cancellationToken);
                    return;
                default:
                    await ApiProblemWriter.WriteResultErrorAsync(
                        context,
                        retried.Error,
                        cancellationToken);
                    return;
            }
        }

        context.Response.Headers.ETag =
            ApiConcurrency.ToETag(snapshot.Version.Value);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            JobStatusEndpoint.Map(snapshot),
            cancellationToken);
    }

    /// <summary>
    /// Cancellation has no representation in the durable job model, so the
    /// route exists for a stable contract and always reports that the action
    /// is unavailable. The listed <c>actions.cancel</c> flag is the signal a
    /// client should use.
    /// </summary>
    public static async Task CancelAsync(
        HttpContext context,
        Guid jobId,
        IJobStatusAuthorizationPort authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);

        JobAccess access = await authorization.AuthorizeAsync(context, cancellationToken);
        if (access.Status != JobAccessStatus.Authorized)
        {
            await DenyAsync(context, access.Status, cancellationToken);
            return;
        }

        await ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status409Conflict,
            $"{CodePrefix}.cancel_unsupported",
            "This release cannot cancel a queued job; see actions.cancel.",
            cancellationToken);
    }

    private static Task DenyAsync(
        HttpContext context,
        JobAccessStatus status,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            status == JobAccessStatus.Unauthenticated
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden,
            status == JobAccessStatus.Unauthenticated
                ? $"{CodePrefix}.unauthenticated"
                : $"{CodePrefix}.forbidden",
            "The caller is not permitted to administer jobs.",
            cancellationToken);

    private static Task WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            $"{CodePrefix}.not_found",
            "The requested job was not found.",
            cancellationToken);

    private static Task WriteValidationAsync(
        HttpContext context,
        string field,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            $"{CodePrefix}.invalid_query",
            "The job query is invalid.",
            cancellationToken,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [field] = ["The value is not supported for this parameter."],
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
