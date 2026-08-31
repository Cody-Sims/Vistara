using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Vistara.Application.Jobs;
using Vistara.Contracts.Jobs;
using Vistara.Domain.Jobs;

namespace Vistara.Api.Features.Jobs;

/// <summary>
/// Serves tenant-scoped durable job status for <c>GET /api/v1/jobs/{id}</c>.
/// </summary>
public static class JobStatusEndpoint
{
    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task GetAsync(
        HttpContext context,
        Guid jobId,
        IJobStatusAuthorizationPort authorization,
        IJobStatusReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(reader);

        JobAccess access = await authorization.AuthorizeAsync(context, cancellationToken);
        switch (access.Status)
        {
            case JobAccessStatus.Unauthenticated:
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "jobs.unauthenticated",
                    "Authentication is required to read job status.",
                    cancellationToken);
                return;
            case JobAccessStatus.Forbidden:
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "jobs.forbidden",
                    "The caller is not permitted to read job status.",
                    cancellationToken);
                return;
            case JobAccessStatus.Authorized:
            default:
                break;
        }

        if (jobId == Guid.Empty || jobId.Version != 7)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        JobSnapshot? snapshot = await reader.FindAsync(
            access.TenantId,
            new JobId(jobId),
            cancellationToken);
        if (snapshot is null || snapshot.TenantId.Value != access.TenantId)
        {
            await WriteNotFoundAsync(context, cancellationToken);
            return;
        }

        string etag = $"\"v{snapshot.Version.Value}\"";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ETag = etag;
        if (MatchesConditional(context.Request, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            Map(snapshot),
            ResponseJsonOptions);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload, cancellationToken);
    }

    internal static string DescribeState(JobState state) =>
        state switch
        {
            JobState.Pending => "pending",
            JobState.Leased => "leased",
            JobState.RetryScheduled => "retryScheduled",
            JobState.Completed => "completed",
            JobState.DeadLettered => "deadLettered",
            _ => "unknown",
        };

    private static Task WriteNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            "jobs.not_found",
            "The requested job was not found.",
            cancellationToken);

    private static bool MatchesConditional(HttpRequest request, string etag)
    {
        if (!request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var values))
        {
            return false;
        }

        foreach (string? value in values)
        {
            if (value is null)
            {
                continue;
            }

            foreach (string candidate in value.Split(','))
            {
                string trimmed = candidate.Trim();
                if (trimmed == "*" || string.Equals(trimmed, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static JobStatusResponse Map(JobSnapshot snapshot) =>
        new(
            snapshot.Id.Value,
            snapshot.Type.Value,
            DescribeState(snapshot.State),
            snapshot.Attempts,
            snapshot.MaxAttempts,
            snapshot.CreatedAtUtc,
            snapshot.AvailableAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.LastFailure is null
                ? null
                : new JobFailureResponse(
                    snapshot.LastFailure.Code,
                    snapshot.LastFailure.Summary),
            snapshot.Version.Value,
            new JobActionsResponse(
                JobActions.CanRetry(snapshot.State),
                JobActions.CanCancel(snapshot.State)));
}
