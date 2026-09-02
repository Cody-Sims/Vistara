using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vistara.Api.Features.Oidc;
using Vistara.Auth.Cookies;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Oidc;

/// <summary>
/// Sends hostile provider segments at a real Kestrel server.
///
/// The unit-level constraint tests prove the predicate; these prove the thing
/// that actually matters in production, which is that such a request is
/// rejected by the server and by routing before any Vistara code observes it.
/// A raw socket is used rather than <see cref="HttpClient"/> because the point
/// is to control the request target byte for byte, including forms an HTTP
/// client would normalise away.
/// </summary>
[Collection("OidcSignIn")]
public sealed class OidcProviderSegmentKestrelTests
{
    private const string Injected = "X-Injected";

    /// <summary>
    /// The control case. If this did not reach the handler the rest of the
    /// theory would pass for the wrong reason.
    /// </summary>
    [Fact]
    public async Task A_provider_key_reaches_the_sign_in_handler()
    {
        await using KestrelOidcSurface surface = await KestrelOidcSurface.StartAsync();

        RawResponse response = await surface.SendAsync("/api/v1/auth/oidc/entra/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.True(surface.Login.WasStarted);
        Assert.Equal("entra", surface.Login.ProviderId);
    }

    /// <summary>
    /// Every one of these is refused by Kestrel or by routing. None of them
    /// may reach the sign-in handler, and none may appear in a record Vistara
    /// wrote: a request that never touched any Vistara state has nothing worth
    /// recording, and its bytes are attacker chosen. The framework's own
    /// per-request hosting diagnostics are out of scope - they carry the path
    /// of every request on the API and are not an operator-facing Vistara
    /// record.
    /// </summary>
    [Theory]
    [InlineData("percent-encoded slash", "ent%2Fra")]
    [InlineData("percent-encoded CRLF", "e%0d%0a" + Injected + ":%20yes")]
    [InlineData("percent-encoded LF", "e%0aevil")]
    [InlineData("percent-encoded CR", "e%0devil")]
    [InlineData("percent-encoded NUL", "entra%00")]
    [InlineData("percent-encoded space", "en%20tra")]
    [InlineData("traversal", "..%2F..%2Fetc%2Fpasswd")]
    [InlineData("dotted", "entra.example")]
    [InlineData("colon", "entra:8080")]
    [InlineData("unicode", "entra%e2%80%8b")]
    public async Task A_segment_that_is_not_a_provider_key_never_reaches_the_handler(
        string description,
        string segment)
    {
        await using KestrelOidcSurface surface = await KestrelOidcSurface.StartAsync();

        RawResponse response = await surface.SendAsync(
            $"/api/v1/auth/oidc/{segment}/start");

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"{description} produced {(int)response.StatusCode}.");
        Assert.False(surface.Login.WasStarted, description);
        Assert.DoesNotContain(Injected, response.Raw, StringComparison.Ordinal);
        surface.AssertNothingLogged(segment);
    }

    /// <summary>
    /// A literal carriage return or line feed in the request target is a
    /// request-smuggling attempt, and the server refuses the request outright
    /// rather than routing it.
    /// </summary>
    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task A_literal_newline_in_the_request_target_is_refused(string newline)
    {
        await using KestrelOidcSurface surface = await KestrelOidcSurface.StartAsync();

        RawResponse response = await surface.SendAsync(
            $"/api/v1/auth/oidc/entra{newline}{Injected}: yes/start");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(surface.Login.WasStarted);
        Assert.DoesNotContain(Injected, response.Raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// A segment far past the bounded provider key length is refused without
    /// the two kilobytes ever reaching a handler or a log.
    /// </summary>
    [Fact]
    public async Task A_two_kilobyte_provider_segment_is_refused()
    {
        await using KestrelOidcSurface surface = await KestrelOidcSurface.StartAsync();
        string segment = new('a', 2048);

        RawResponse response = await surface.SendAsync(
            $"/api/v1/auth/oidc/{segment}/start");

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.BadRequest
                or HttpStatusCode.RequestUriTooLong,
            $"A 2KB segment produced {(int)response.StatusCode}.");
        Assert.False(surface.Login.WasStarted);
        surface.AssertNothingLogged(segment);
    }

    /// <summary>
    /// The same constraint guards relying-party sign-out, so an unbounded
    /// segment cannot reach the revocation path either.
    /// </summary>
    [Fact]
    public async Task The_sign_out_route_is_constrained_the_same_way()
    {
        await using KestrelOidcSurface surface = await KestrelOidcSurface.StartAsync();

        RawResponse response = await surface.SendAsync(
            "/api/v1/auth/oidc/not%20a%20key/sign-out",
            method: "POST");

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
        Assert.False(surface.SignOut.WasCalled);
    }

    /// <summary>
    /// A real cross-site iframe GET: no cookie, no credential, nothing to act
    /// on. It answers 200 with no body and sets no cookie.
    /// </summary>
    [Fact]
    public async Task Front_channel_logout_answers_an_unauthenticated_cross_site_get()
    {
        await using KestrelOidcSurface surface = await KestrelOidcSurface.StartAsync();

        RawResponse response = await surface.SendAsync(
            "/api/v1/auth/oidc/entra/frontchannel-logout?sid=abc",
            headers: ["Sec-Fetch-Site: cross-site", "Sec-Fetch-Dest: iframe"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Set-Cookie", response.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.False(surface.SignOut.WasCalled);
    }

    /// <summary>
    /// The hosted surface mapped over a real server, with the sign-in and
    /// sign-out ports replaced by recorders so a test can prove they were
    /// never reached.
    /// </summary>
    private sealed class KestrelOidcSurface : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RecordedLogProvider _log;

        private KestrelOidcSurface(
            WebApplication app,
            RecordedLogProvider log,
            int port,
            RecordingLoginPort login,
            RecordingSignOutPort signOut)
        {
            _app = app;
            _log = log;
            Port = port;
            Login = login;
            SignOut = signOut;
        }

        internal int Port { get; }

        internal RecordingLoginPort Login { get; }

        internal RecordingSignOutPort SignOut { get; }

        internal static async Task<KestrelOidcSurface> StartAsync()
        {
            var log = new RecordedLogProvider();
            var login = new RecordingLoginPort();
            var signOut = new RecordingSignOutPort();
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(log);
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton<IOidcLoginPort>(login);
            builder.Services.AddSingleton<IOidcSignOutPort>(signOut);
            builder.Services.AddSingleton(new CookieAuthOptions());
            builder.Services.AddSingleton<IOidcProviderCatalog>(
                new SingleProviderCatalog());
            builder.Services.AddVistaraOidcRouting();

            WebApplication app = builder.Build();
            app.MapVistaraOidcAuthentication();
            await app.StartAsync();
            var listening = new Uri(
                app.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()!
                    .Addresses
                    .First());
            return new KestrelOidcSurface(app, log, listening.Port, login, signOut);
        }

        /// <summary>
        /// Sends the request target verbatim. Nothing normalises, escapes, or
        /// validates it on the way out, which is the only way to test what the
        /// server does with a target a client would refuse to build.
        /// </summary>
        internal async Task<RawResponse> SendAsync(
            string requestTarget,
            string method = "GET",
            IReadOnlyList<string>? headers = null)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port, CancellationToken.None);
            await using NetworkStream stream = client.GetStream();

            var request = new StringBuilder();
            request.Append(CultureInfo.InvariantCulture, $"{method} {requestTarget} HTTP/1.1\r\n");
            request.Append(CultureInfo.InvariantCulture, $"Host: 127.0.0.1:{Port}\r\n");
            foreach (string header in headers ?? [])
            {
                request.Append(CultureInfo.InvariantCulture, $"{header}\r\n");
            }

            request.Append("Content-Length: 0\r\nConnection: close\r\n\r\n");
            byte[] payload = Encoding.ASCII.GetBytes(request.ToString());
            await stream.WriteAsync(payload, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, CancellationToken.None);
            string raw = Encoding.ASCII.GetString(buffer.ToArray());
            return new RawResponse(ReadStatus(raw), raw);
        }

        /// <summary>
        /// Proves the hostile bytes reached no Vistara log or audit record at
        /// any level, so they cannot appear in an operator-facing record.
        /// </summary>
        internal void AssertNothingLogged(string segment)
        {
            string logged = string.Join("\n", _log.VistaraMessages);
            Assert.DoesNotContain(segment, logged, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Uri.UnescapeDataString(segment),
                logged,
                StringComparison.Ordinal);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static HttpStatusCode ReadStatus(string raw)
        {
            int start = raw.IndexOf(' ', StringComparison.Ordinal);
            return start > 0 &&
                int.TryParse(
                    raw.AsSpan(start + 1, 3),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int status)
                ? (HttpStatusCode)status
                : HttpStatusCode.Unused;
        }
    }

    private sealed record RawResponse(HttpStatusCode StatusCode, string Raw);

    private sealed class SingleProviderCatalog : IOidcProviderCatalog
    {
        public IReadOnlyList<OidcProviderCapability> Providers { get; } =
        [
            new("entra", "Microsoft Entra ID", "/api/v1/auth/oidc/entra/start"),
        ];
    }

    private sealed class RecordingLoginPort : IOidcLoginPort
    {
        public bool WasStarted { get; private set; }

        public string? ProviderId { get; private set; }

        public ValueTask<Result<OidcStartResult>> StartAsync(
            string providerId,
            string? returnTo,
            CancellationToken cancellationToken)
        {
            WasStarted = true;
            ProviderId = providerId;
            return ValueTask.FromResult(Result.Success(new OidcStartResult(
                new Uri("https://login.microsoftonline.com/authorize"),
                OidcHandleCookie.ToSetCookieHeader("handle", TimeSpan.FromMinutes(10)))));
        }

        public ValueTask<Result<OidcSignInResult>> CompleteAsync(
            OidcCallbackCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Failure<OidcSignInResult>(OidcErrors.InvalidState));
    }

    private sealed class RecordingSignOutPort : IOidcSignOutPort
    {
        public bool WasCalled { get; private set; }

        public ValueTask<OidcSignOutResult> SignOutAsync(
            string providerId,
            string sessionToken,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            return ValueTask.FromResult(
                new OidcSignOutResult("__Host-vistara-session=; Path=/; Max-Age=0", null));
        }
    }
}
