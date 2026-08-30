using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vistara.Api.Features.Events;
using Vistara.Application.Common.Events;
using Xunit;

namespace Vistara.IntegrationTests.OutboxEvents;

public sealed class EventStreamEndpointTests
{
    private static readonly Guid TenantOne =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000101");
    private static readonly Guid TenantTwo =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000102");
    internal static readonly DateTimeOffset Now =
        new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Mapping_extension_registers_the_versioned_event_route()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();

        app.MapVistaraEventStream();

        RouteEndpoint endpoint = Assert.IsType<RouteEndpoint>(
            Assert.Single(((IEndpointRouteBuilder)app).DataSources)
                .Endpoints
                .Single());
        Assert.Equal("/api/v1/events", endpoint.RoutePattern.RawText);
        Assert.Equal("GET", Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods));
    }

    [Fact]
    public async Task Reconnect_replays_in_order_then_streams_live_with_required_headers()
    {
        var source = new FakeEventStreamSource(
            new EventStreamBounds(1, 4),
            replay: [Envelope(TenantOne, 2), Envelope(TenantOne, 3)],
            live: [Envelope(TenantOne, 4)]);
        DefaultHttpContext context = Context();
        context.Request.Headers["Last-Event-ID"] = "1";

        await EventStreamEndpoint.WriteAsync(
            context,
            EventStreamAccess.Authorized(TenantOne),
            source,
            new NeverHeartbeatDelay(),
            new EventStreamOptions(),
            CancellationToken.None);

        string body = Body(context);
        Assert.True(
            body.IndexOf("id: 2", StringComparison.Ordinal) <
            body.IndexOf("id: 3", StringComparison.Ordinal));
        Assert.True(
            body.IndexOf("id: 3", StringComparison.Ordinal) <
            body.IndexOf("id: 4", StringComparison.Ordinal));
        Assert.StartsWith("retry: 3000\n\n", body, StringComparison.Ordinal);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache, no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
        Assert.Equal(TenantOne, source.BoundsTenant);
        Assert.Equal(TenantOne, source.ReplayTenant);
        Assert.Equal(TenantOne, source.LiveTenant);
        Assert.Equal(3, source.LiveAfter);
    }

    [Fact]
    public async Task Authorization_and_tenant_scope_are_resolved_before_event_lookup()
    {
        var deniedSource = new FakeEventStreamSource(new EventStreamBounds(0, 0));
        DefaultHttpContext denied = Context();

        await EventStreamEndpoint.WriteAsync(
            denied,
            EventStreamAccess.Forbidden(),
            deniedSource,
            new NeverHeartbeatDelay(),
            new EventStreamOptions(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);
        Assert.Equal(0, deniedSource.LookupCount);

        var source = new FakeEventStreamSource(
            new EventStreamBounds(0, 1),
            replay: [Envelope(TenantOne, 1)]);
        DefaultHttpContext authorized = Context();
        authorized.Request.QueryString = new QueryString($"?tenantId={TenantTwo}");

        await EventStreamEndpoint.WriteAsync(
            authorized,
            EventStreamAccess.Authorized(TenantOne),
            source,
            new NeverHeartbeatDelay(),
            new EventStreamOptions(),
            CancellationToken.None);

        Assert.Equal(TenantOne, source.BoundsTenant);
        Assert.DoesNotContain(TenantTwo.ToString(), Body(authorized), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("999999999999999999999")]
    public async Task Invalid_last_event_id_is_rejected_without_lookup(string cursor)
    {
        var source = new FakeEventStreamSource(new EventStreamBounds(1, 3));
        DefaultHttpContext context = Context();
        context.Request.Headers["Last-Event-ID"] = cursor;

        await EventStreamEndpoint.WriteAsync(
            context,
            EventStreamAccess.Authorized(TenantOne),
            source,
            new NeverHeartbeatDelay(),
            new EventStreamOptions(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("events.invalid_cursor", Body(context), StringComparison.Ordinal);
        Assert.Equal(0, source.LookupCount);
    }

    [Theory]
    [InlineData("1", 3, 5, "events.resync_required")]
    [InlineData("3", 0, 5, "events.resync_required")]
    [InlineData("6", 3, 5, "events.cursor_in_future")]
    public async Task Stale_and_future_cursors_fail_before_streaming(
        string cursor,
        long oldest,
        long latest,
        string expectedCode)
    {
        var source = new FakeEventStreamSource(new EventStreamBounds(oldest, latest));
        DefaultHttpContext context = Context();
        context.Request.Headers["Last-Event-ID"] = cursor;

        await EventStreamEndpoint.WriteAsync(
            context,
            EventStreamAccess.Authorized(TenantOne),
            source,
            new NeverHeartbeatDelay(),
            new EventStreamOptions(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Contains(expectedCode, Body(context), StringComparison.Ordinal);
        Assert.Null(source.ReplayTenant);
    }

    [Fact]
    public async Task Replay_gap_detected_after_bounds_lookup_requires_resync()
    {
        var source = new FakeEventStreamSource(
            new EventStreamBounds(1, 5),
            replay: [Envelope(TenantOne, 4)]);
        DefaultHttpContext context = Context();
        context.Request.Headers["Last-Event-ID"] = "2";

        await EventStreamEndpoint.WriteAsync(
            context,
            EventStreamAccess.Authorized(TenantOne),
            source,
            new NeverHeartbeatDelay(),
            new EventStreamOptions(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Contains("events.resync_required", Body(context), StringComparison.Ordinal);
        Assert.Null(source.LiveTenant);
    }

    [Fact]
    public async Task Heartbeats_continue_while_idle_and_disconnect_cancels_subscription()
    {
        using var disconnected = new CancellationTokenSource();
        var source = new BlockingEventStreamSource();
        DefaultHttpContext context = Context();
        context.RequestAborted = disconnected.Token;
        var delay = new OneHeartbeatThenDisconnectDelay(disconnected);

        await EventStreamEndpoint.WriteAsync(
            context,
            EventStreamAccess.Authorized(TenantOne),
            source,
            delay,
            new EventStreamOptions(),
            disconnected.Token);

        Assert.Contains(": heartbeat\n\n", Body(context), StringComparison.Ordinal);
        Assert.True(source.CancellationObserved);
    }

    [Fact]
    public async Task Response_backpressure_prevents_unbounded_live_reads()
    {
        using var disconnected = new CancellationTokenSource();
        var source = new CountingLiveSource();
        var stream = new BlockingFlushStream();
        DefaultHttpContext context = Context(stream);
        context.RequestAborted = disconnected.Token;

        Task writing = EventStreamEndpoint.WriteAsync(
            context,
            EventStreamAccess.Authorized(TenantOne),
            source,
            new NeverHeartbeatDelay(),
            new EventStreamOptions(),
            disconnected.Token);

        await stream.WaitUntilBlockedAsync();
        Assert.Equal(1, source.MoveNextCount);

        disconnected.Cancel();
        await writing;
        Assert.Equal(1, source.MoveNextCount);
    }

    private static DefaultHttpContext Context(Stream? body = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = body ?? new MemoryStream();
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        var stream = Assert.IsAssignableFrom<MemoryStream>(context.Response.Body);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static EventEnvelope Envelope(Guid tenantId, long sequence) =>
        new(
            new EventMetadata(
                new EventId(Guid.CreateVersion7()),
                new EventTenantId(tenantId),
                new EventSequence(sequence),
                "asset.ready",
                1,
                Now,
                Guid.CreateVersion7()),
            $$"""{"sequence":{{sequence}}}""");
}

internal sealed class FakeEventStreamSource(
    EventStreamBounds bounds,
    IReadOnlyList<EventEnvelope>? replay = null,
    IReadOnlyList<EventEnvelope>? live = null) : IEventStreamSource
{
    private readonly IReadOnlyList<EventEnvelope> _replay = replay ?? [];
    private readonly IReadOnlyList<EventEnvelope> _live = live ?? [];

    internal int LookupCount { get; private set; }
    internal Guid? BoundsTenant { get; private set; }
    internal Guid? ReplayTenant { get; private set; }
    internal Guid? LiveTenant { get; private set; }
    internal long? LiveAfter { get; private set; }

    public ValueTask<EventStreamBounds> GetBoundsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LookupCount++;
        BoundsTenant = tenantId;
        return ValueTask.FromResult(bounds);
    }

    public ValueTask<IReadOnlyList<EventEnvelope>> ReadReplayAsync(
        Guid tenantId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LookupCount++;
        ReplayTenant = tenantId;
        return ValueTask.FromResult(_replay);
    }

    public async IAsyncEnumerable<EventEnvelope> ReadLiveAsync(
        Guid tenantId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LiveTenant = tenantId;
        LiveAfter = afterSequence;
        foreach (EventEnvelope item in _live)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}

internal sealed class BlockingEventStreamSource : IEventStreamSource
{
    internal bool CancellationObserved { get; private set; }

    public ValueTask<EventStreamBounds> GetBoundsAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EventStreamBounds(0, 0));

    public ValueTask<IReadOnlyList<EventEnvelope>> ReadReplayAsync(
        Guid tenantId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<EventEnvelope>>([]);

    public async IAsyncEnumerable<EventEnvelope> ReadLiveAsync(
        Guid tenantId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            CancellationObserved = cancellationToken.IsCancellationRequested;
        }

        yield break;
    }
}

internal sealed class CountingLiveSource : IEventStreamSource
{
    internal int MoveNextCount { get; private set; }

    public ValueTask<EventStreamBounds> GetBoundsAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EventStreamBounds(0, 0));

    public ValueTask<IReadOnlyList<EventEnvelope>> ReadReplayAsync(
        Guid tenantId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<EventEnvelope>>([]);

    public async IAsyncEnumerable<EventEnvelope> ReadLiveAsync(
        Guid tenantId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (long sequence = 1; sequence <= 2; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MoveNextCount++;
            yield return new EventEnvelope(
                new EventMetadata(
                    new EventId(Guid.CreateVersion7()),
                    new EventTenantId(tenantId),
                    new EventSequence(sequence),
                    "asset.ready",
                    1,
                    EventStreamEndpointTests.Now,
                    Guid.CreateVersion7()),
                $$"""{"sequence":{{sequence}}}""");
            await Task.Yield();
        }
    }
}

internal sealed class NeverHeartbeatDelay : IEventStreamHeartbeatDelay
{
    public ValueTask DelayAsync(TimeSpan interval, CancellationToken cancellationToken) =>
        new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
}

internal sealed class OneHeartbeatThenDisconnectDelay(CancellationTokenSource disconnected)
    : IEventStreamHeartbeatDelay
{
    private int _calls;

    public ValueTask DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        _calls++;
        if (_calls == 1)
        {
            return ValueTask.CompletedTask;
        }

        disconnected.Cancel();
        return ValueTask.FromCanceled(cancellationToken);
    }
}

internal sealed class BlockingFlushStream : MemoryStream
{
    private readonly TaskCompletionSource _blocked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _flushes;

    internal Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        _flushes++;
        if (_flushes == 2)
        {
            _blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
