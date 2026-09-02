using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;

// The E2E host is a top-level program in the global namespace. A namespace of
// its own here would be named Vistara.E2E.Host, which the sibling test project
// would then resolve 'Host' to instead of the hosting API.

/// <summary>
/// The transport the end-to-end suite runs over.
///
/// Vistara's browser session cookie is a host cookie: <c>Secure</c>, with the
/// <c>__Host-</c> prefix, and so is the short-lived login handle hosted
/// sign-in sets. Chromium and Gecko make an exception for loopback and keep
/// such a cookie on plain HTTP; WebKit does not, so a suite served over
/// <c>http://127.0.0.1</c> could only ever sign in on two of the three engines
/// it claims to cover. The answer is to give the harness a transport the
/// cookie is actually valid on rather than to weaken the cookie.
///
/// Nothing here is production code and nothing here is reachable from one. The
/// certificate is generated per process and self-signed, its private key never
/// leaves memory, no trust store on the machine is touched, and the only thing
/// written anywhere is the public certificate, into the run's ignored
/// artifacts folder, so that the probes which wait for a host to come up can
/// pin exactly that one certificate instead of relaxing validation.
/// </summary>
internal static class LoopbackTls
{
    /// <summary>
    /// The environment variable a served host reads to learn where to publish
    /// its public certificate for this run.
    /// </summary>
    internal const string PublishedCertificateVariable =
        "VISTARA_E2E_SERVER_CERTIFICATE";

    /// <summary>
    /// A self-signed server certificate for the loopback address, exported and
    /// reloaded as PKCS#12 so the private key is usable on every platform the
    /// suite runs on. The PKCS#12 blob is never written down: it exists only
    /// long enough to hand the key to the returned certificate.
    /// </summary>
    internal static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=127.0.0.1",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        alternativeNames.AddDnsName("localhost");
        request.CertificateExtensions.Add(alternativeNames.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 generated = request.CreateSelfSigned(
            now.AddDays(-1),
            now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null);
    }

    /// <summary>
    /// Writes the public half of a certificate where a probe can pin it. Only
    /// <see cref="X509ContentType.Cert"/> is exported, so no private key and
    /// no passphrase is ever written to disk.
    /// </summary>
    internal static async Task PublishAsync(
        X509Certificate2 certificate,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(
            path,
            certificate.Export(X509ContentType.Cert),
            cancellationToken);
    }

    /// <summary>
    /// Loads a certificate a run published and returns its SHA-256 thumbprint,
    /// which is the whole of what a pinning caller has to agree with.
    /// </summary>
    internal static string ReadPublishedThumbprint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using X509Certificate2 published =
            X509CertificateLoader.LoadCertificateFromFile(path);
        return published.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Serves every endpoint this host binds with the supplied certificate.
    /// The certificate is set explicitly rather than left to Kestrel, so the
    /// suite never depends on a development certificate being installed in the
    /// machine's trust store.
    /// </summary>
    internal static IWebHostBuilder UseLoopbackHttps(
        this IWebHostBuilder webHost,
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(certificate);

        return webHost
            .UseKestrelHttpsConfiguration()
            .ConfigureKestrel(kestrel =>
                kestrel.ConfigureHttpsDefaults(
                    (HttpsConnectionAdapterOptions https) =>
                        https.ServerCertificate = certificate));
    }

    /// <summary>
    /// Refuses to start a served host on anything but HTTPS. A harness that
    /// quietly fell back to plain HTTP would pass on two engines and fail on
    /// the third, which is the failure this transport exists to remove, so it
    /// is made a startup error rather than a browser-visible one.
    /// </summary>
    internal static IReadOnlyList<string> RequireHttpsUrls(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            throw new InvalidOperationException(
                "The E2E host must be given explicit https:// --urls.");
        }

        string[] addresses = urls.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException(
                "The E2E host must be given explicit https:// --urls.");
        }

        foreach (string address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? parsed) ||
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The E2E host serves HTTPS only, so '{address}' cannot be bound. "
                    + "The session cookie is a __Host- cookie and WebKit drops it "
                    + "over plain HTTP.");
            }
        }

        return addresses;
    }
}
