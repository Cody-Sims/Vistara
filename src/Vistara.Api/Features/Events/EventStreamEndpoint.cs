using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Application.Common.Events;

namespace Vistara.Api.Features.Events;

public static class EventStreamEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        IEventStreamAuthorizationPort authorization,
        IEventStreamSource source,
        IEventStreamHeartbeatDelay heartbeatDelay,
        EventStreamOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        EventStreamAccess access =
            await authorization.AuthorizeAsync(context, cancellationToken);
        await WriteAsync(
            context,
            access,
            source,
            heartbeatDelay,
            options,
            cancellationToken);
    }

    public static async Task WriteAsync(
        HttpContext context,
        EventStreamAccess access,
        IEventStreamSource source,
        IEventStreamHeartbeatDelay heartbeatDelay,
        EventStreamOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(heartbeatDelay);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (access.Status != EventStreamAccessStatus.Authorized)
        {
            int status = access.Status == EventStreamAccessStatus.Unauthenticated
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            await WriteProblemAsync(
                context,
                status,
                access.Status == EventStreamAccessStatus.Unauthenticated
                    ? "events.authentication_required"
                    : "events.forbidden",
                access.Status == EventStreamAccessStatus.Unauthenticated
                    ? "Authentication is required"
                    : "Event stream access is forbidden",
                cancellationToken);
            return;
        }

        Guid tenantId = access.TenantId!.Value;
        if (!TryParseCursor(context.Request.Headers["Last-Event-ID"], out long afterSequence))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "events.invalid_cursor",
                "The Last-Event-ID cursor is invalid",
                cancellationToken);
            return;
        }

        EventStreamBounds bounds =
            await source.GetBoundsAsync(tenantId, cancellationToken);
        if (afterSequence > bounds.Latest)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "events.cursor_in_future",
                "The event cursor is ahead of the event log",
                cancellationToken);
            return;
        }

        if (afterSequence != 0 &&
            ((bounds.OldestAvailable == 0 && afterSequence < bounds.Latest) ||
             (bounds.OldestAvailable > 0 &&
              afterSequence < bounds.OldestAvailable - 1)))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "events.resync_required",
                "The event cursor is no longer retained; a full resync is required",
                cancellationToken);
            return;
        }

        IReadOnlyList<EventEnvelope> replay = await source.ReadReplayAsync(
            tenantId,
            afterSequence,
            options.MaximumReplayEvents,
            cancellationToken);
        if (!TryValidateBatch(replay, tenantId, afterSequence, options.MaximumReplayEvents))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "events.source_invalid",
                "The event source returned an invalid replay batch",
                cancellationToken);
            return;
        }

        if (afterSequence > 0 &&
            bounds.Latest > afterSequence &&
            (replay.Count == 0 ||
             replay[0].Metadata.Sequence.Value != afterSequence + 1))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "events.resync_required",
                "The retained event replay has a gap; a full resync is required",
                cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await WriteAndFlushAsync(
                context.Response,
                FormattableString.Invariant($"retry: {options.RetryMilliseconds}\n\n"),
                cancellationToken);

            long lastSequence = afterSequence;
            foreach (EventEnvelope envelope in replay)
            {
                await WriteEventAsync(context.Response, envelope, cancellationToken);
                lastSequence = envelope.Metadata.Sequence.Value;
            }

            await StreamLiveAsync(
                context.Response,
                source,
                heartbeatDelay,
                options.HeartbeatInterval,
                tenantId,
                lastSequence,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static async Task StreamLiveAsync(
        HttpResponse response,
        IEventStreamSource source,
        IEventStreamHeartbeatDelay heartbeatDelay,
        TimeSpan heartbeatInterval,
        Guid tenantId,
        long lastSequence,
        CancellationToken cancellationToken)
    {
        await using IAsyncEnumerator<EventEnvelope> live = source
            .ReadLiveAsync(tenantId, lastSequence, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        Task<bool>? moveNext = null;
        try
        {
            moveNext = live.MoveNextAsync().AsTask();
            while (true)
            {
                using var heartbeatCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task heartbeat = heartbeatDelay
                    .DelayAsync(heartbeatInterval, heartbeatCancellation.Token)
                    .AsTask();
                Task completed = await Task.WhenAny(moveNext, heartbeat);
                if (completed == heartbeat)
                {
                    await heartbeat;
                    await WriteAndFlushAsync(response, ": heartbeat\n\n", cancellationToken);
                    continue;
                }

                await heartbeatCancellation.CancelAsync();
                try
                {
                    await heartbeat;
                }
                catch (OperationCanceledException) when (
                    heartbeatCancellation.IsCancellationRequested)
                {
                }

                if (!await moveNext)
                {
                    return;
                }

                EventEnvelope envelope = live.Current;
                if (envelope.Metadata.TenantId.Value != tenantId)
                {
                    throw new InvalidOperationException(
                        "A live event cannot cross the authorized tenant boundary.");
                }

                long sequence = envelope.Metadata.Sequence.Value;
                if (sequence > lastSequence)
                {
                    await WriteEventAsync(response, envelope, cancellationToken);
                    lastSequence = sequence;
                }

                moveNext = live.MoveNextAsync().AsTask();
            }
        }
        finally
        {
            if (moveNext is { IsCompleted: false })
            {
                try
                {
                    await moveNext;
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        EventMetadata metadata = envelope.Metadata;
        if (!IsSafeEventType(metadata.EventType))
        {
            throw new InvalidOperationException("Event types must be safe SSE token values.");
        }

        var builder = new StringBuilder();
        builder.Append("id: ")
            .Append(metadata.Sequence.Value.ToString(CultureInfo.InvariantCulture))
            .Append('\n')
            .Append("event: ")
            .Append(metadata.EventType)
            .Append('\n');
        string normalizedPayload = envelope.ClientPayload
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (string line in normalizedPayload.Split('\n'))
        {
            builder.Append("data: ").Append(line).Append('\n');
        }

        builder.Append('\n');
        await WriteAndFlushAsync(response, builder.ToString(), cancellationToken);
    }

    private static bool TryValidateBatch(
        IReadOnlyList<EventEnvelope> replay,
        Guid tenantId,
        long afterSequence,
        int maximumCount)
    {
        if (replay.Count > maximumCount)
        {
            return false;
        }

        long previous = afterSequence;
        foreach (EventEnvelope envelope in replay)
        {
            if (envelope.Metadata.TenantId.Value != tenantId ||
                envelope.Metadata.Sequence.Value <= previous ||
                !IsSafeEventType(envelope.Metadata.EventType))
            {
                return false;
            }

            previous = envelope.Metadata.Sequence.Value;
        }

        return true;
    }

    private static bool TryParseCursor(
        Microsoft.Extensions.Primitives.StringValues values,
        out long cursor)
    {
        cursor = 0;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        string? value = values[0];
        if (string.IsNullOrEmpty(value) ||
            value.Length > 19 ||
            value.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        return long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out cursor);
    }

    private static bool IsSafeEventType(string eventType) =>
        eventType.Length is > 0 and <= 200 &&
        eventType.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-' or '_' or ':');

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = $"https://vistara.dev/problems/{code.Replace('.', '-')}",
            title,
            status,
            code,
            traceId = context.TraceIdentifier,
        });
        await context.Response.Body.WriteAsync(body, cancellationToken);
    }

    private static async Task WriteAndFlushAsync(
        HttpResponse response,
        string content,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
