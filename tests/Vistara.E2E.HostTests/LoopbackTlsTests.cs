using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vistara.E2E.HostTests;

/// <summary>
/// The transport the end-to-end suite is served over.
///
/// Vistara's session cookie and hosted sign-in login handle are <c>Secure</c>
/// <c>__Host-</c> cookies. WebKit, unlike Chromium and Gecko, does not make a
/// loopback exception for them, so an E2E host served over plain HTTP could
/// only ever sign in on two of the three engines the suite claims to cover.
/// These cases hold the harness on TLS, and hold it there without asking the
/// repository to carry a key or the machine to trust anything.
/// </summary>
public sealed class LoopbackTlsTests
{
    [Fact]
    public void Certificate_is_a_loopback_server_certificate_with_a_usable_key()
    {
        using X509Certificate2 certificate = LoopbackTls.CreateCertificate();

        Assert.True(certificate.HasPrivateKey);
        Assert.Equal("CN=127.0.0.1", certificate.Subject);
        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.True(DateTimeOffset.UtcNow < certificate.NotAfter);
        Assert.True(DateTimeOffset.UtcNow > certificate.NotBefore);

        X509BasicConstraintsExtension constraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Single();
        Assert.False(constraints.CertificateAuthority);

        X509EnhancedKeyUsageExtension usage = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Single();
        Assert.Contains(
            usage.EnhancedKeyUsages.Cast<Oid>(),
            oid => oid.Value == "1.3.6.1.5.5.7.3.1");

        // The suite reaches its hosts by loopback address, so the address has
        // to be in the certificate rather than only the name.
        Assert.Contains(
            "127.0.0.1",
            certificate.Extensions
                .First(extension => extension.Oid?.Value == "2.5.29.17")
                .Format(false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_run_generates_its_own_certificate()
    {
        using X509Certificate2 first = LoopbackTls.CreateCertificate();
        using X509Certificate2 second = LoopbackTls.CreateCertificate();

        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public async Task Published_certificate_carries_no_private_key()
    {
        string root = CreateScratchDirectory();
        string publishedPath = Path.Combine(root, "nested", "published.cer");
        try
        {
            using X509Certificate2 certificate = LoopbackTls.CreateCertificate();
            await LoopbackTls.PublishAsync(certificate, publishedPath);

            byte[] published = await File.ReadAllBytesAsync(publishedPath);
            using X509Certificate2 reloaded =
                X509CertificateLoader.LoadCertificate(published);
            Assert.False(reloaded.HasPrivateKey);
            Assert.Equal(certificate.Thumbprint, reloaded.Thumbprint);
            Assert.DoesNotContain(
                "PRIVATE KEY",
                System.Text.Encoding.ASCII.GetString(published),
                StringComparison.Ordinal);

            // The published file is exactly what a pinning caller agrees with.
            Assert.Equal(
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                LoopbackTls.ReadPublishedThumbprint(publishedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5188")]
    [InlineData("https://127.0.0.1:5188;http://127.0.0.1:5189")]
    [InlineData("http://localhost:5188")]
    [InlineData("")]
    [InlineData(null)]
    public void Serving_anything_but_https_is_a_startup_error(string? urls)
    {
        Assert.Throws<InvalidOperationException>(
            () => LoopbackTls.RequireHttpsUrls(urls));
    }

    [Fact]
    public void Https_urls_are_accepted_as_given()
    {
        Assert.Equal(
            ["https://127.0.0.1:5188", "https://127.0.0.1:5189"],
            LoopbackTls.RequireHttpsUrls(
                "https://127.0.0.1:5188; https://127.0.0.1:5189"));
    }

    /// <summary>
    /// The wiring a served host uses, exercised end to end: a client that pins
    /// the run's certificate is answered, a client that pins a different one is
    /// refused, and a plaintext caller never gets a working response out of the
    /// port.
    /// </summary>
    [Fact]
    public async Task Configured_host_serves_https_only_and_pins_to_its_own_certificate()
    {
        using X509Certificate2 certificate = LoopbackTls.CreateCertificate();
        using X509Certificate2 other = LoopbackTls.CreateCertificate();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseLoopbackHttps(certificate);
        builder.WebHost.UseUrls("https://127.0.0.1:0");
        await using WebApplication app = builder.Build();
        app.MapGet("/health/live", () => Results.Text("ready"));
        await app.StartAsync();

        string address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        Assert.StartsWith("https://", address, StringComparison.Ordinal);
        var served = new Uri(address);

        using (HttpClient pinned = CreatePinnedClient(certificate))
        {
            using HttpResponseMessage response =
                await pinned.GetAsync(new Uri(served, "/health/live"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (HttpClient mismatched = CreatePinnedClient(other))
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => mismatched.GetAsync(new Uri(served, "/health/live")));
        }

        // Nothing on this port answers a caller who did not negotiate TLS.
        using (var plaintext = new HttpClient())
        {
            var insecure = new Uri(
                $"http://{served.Host}:{served.Port}/health/live");
            HttpStatusCode? status = null;
            try
            {
                using HttpResponseMessage response =
                    await plaintext.GetAsync(insecure);
                status = response.StatusCode;
            }
            catch (HttpRequestException)
            {
                // Refusing the request outright is the other acceptable answer.
            }

            Assert.NotEqual(HttpStatusCode.OK, status);
        }

        await app.StopAsync();
    }

    private static HttpClient CreatePinnedClient(X509Certificate2 pinned)
    {
        string thumbprint = pinned.GetCertHashString(HashAlgorithmName.SHA256);
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null &&
                    string.Equals(
                        certificate.GetCertHashString(HashAlgorithmName.SHA256),
                        thumbprint,
                        StringComparison.OrdinalIgnoreCase),
            },
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static string CreateScratchDirectory()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            ".artifacts",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
