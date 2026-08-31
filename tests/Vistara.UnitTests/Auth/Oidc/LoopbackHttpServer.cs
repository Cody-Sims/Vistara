using System.Globalization;
using System.Net;
using System.Text;

namespace Vistara.UnitTests.Auth.Oidc;

/// <summary>
/// A real HTTP server on the loopback interface. The redirect tests must run
/// against a live HttpClientHandler so the redirect is actually followed by
/// the transport, which a stub HttpMessageHandler can never reproduce.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Func<LoopbackResponse>> _routes =
        new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<string> _requestedPaths = [];
    private readonly Task _loop;

    internal LoopbackHttpServer()
    {
        Port = ReserveFreePort();
        Origin = new Uri($"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}");
        _listener.Prefixes.Add($"{Origin.AbsoluteUri}");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    internal int Port { get; }

    internal Uri Origin { get; }

    internal IReadOnlyList<string> RequestedPaths
    {
        get
        {
            lock (_gate)
            {
                return _requestedPaths.ToArray();
            }
        }
    }

    internal Uri Route(string path, Func<LoopbackResponse> handler)
    {
        lock (_gate)
        {
            _routes[path] = handler;
        }

        return new Uri(Origin, path);
    }

    internal static LoopbackResponse Json(string body) =>
        new(HttpStatusCode.OK, "application/json", body, null);

    internal static LoopbackResponse Redirect(Uri location, HttpStatusCode status) =>
        new(status, null, string.Empty, location);

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            // The listener is torn down; a racing accept is expected to fault.
        }
#pragma warning restore CA1031

        _shutdown.Dispose();
    }

    private static int ReserveFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception)
            {
                return;
            }
#pragma warning restore CA1031

            string path = context.Request.Url?.AbsolutePath ?? "/";
            Func<LoopbackResponse>? handler;
            lock (_gate)
            {
                _requestedPaths.Add(path);
                _ = _routes.TryGetValue(path, out handler);
            }

            LoopbackResponse response = handler is null
                ? new LoopbackResponse(HttpStatusCode.NotFound, "application/json", "{}", null)
                : handler();
            WriteResponse(context.Response, response);
        }
    }

    private static void WriteResponse(HttpListenerResponse target, LoopbackResponse response)
    {
        try
        {
            target.StatusCode = (int)response.Status;
            if (response.Location is not null)
            {
                target.Headers["Location"] = response.Location.AbsoluteUri;
            }

            if (response.ContentType is not null)
            {
                target.ContentType = response.ContentType;
            }

            byte[] body = Encoding.UTF8.GetBytes(response.Body);
            target.ContentLength64 = body.Length;
            target.OutputStream.Write(body, 0, body.Length);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            // A client that gave up mid-write must not fault the server loop.
        }
#pragma warning restore CA1031
        finally
        {
            target.Close();
        }
    }
}

internal sealed record LoopbackResponse(
    HttpStatusCode Status,
    string? ContentType,
    string Body,
    Uri? Location);
