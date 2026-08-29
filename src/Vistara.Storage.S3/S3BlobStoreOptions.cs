using System.Collections.ObjectModel;
using System.Net;

namespace Vistara.Storage.S3;

public sealed record S3BlobStoreOptions(
    S3ProviderKind Provider,
    string BucketName,
    string Region)
{
    public Uri? ServiceUrl { get; init; }

    public bool ForcePathStyle { get; init; }

    public bool AllowInsecureHttp { get; init; }

    public IReadOnlyCollection<string> AllowedEndpointHosts { get; init; } =
        Array.Empty<string>();

    public TimeSpan MaximumPresignLifetime { get; init; } = TimeSpan.FromHours(1);

    public S3ValidatedOptions Validate()
    {
        string bucket = ValidateBucket(BucketName, ForcePathStyle);
        string region = ValidateRegion(Region);
        if (MaximumPresignLifetime <= TimeSpan.Zero ||
            MaximumPresignLifetime > TimeSpan.FromDays(7))
        {
            throw Invalid("The maximum presign lifetime must be between zero and seven days.");
        }

        if (AllowedEndpointHosts is null)
        {
            throw Invalid("The endpoint host allowlist cannot be null.");
        }

        HashSet<string> allowedHosts = new(
            AllowedEndpointHosts.Select(NormalizeHost),
            StringComparer.OrdinalIgnoreCase);
        Uri? serviceUrl = ServiceUrl is null
            ? null
            : ValidateEndpoint(ServiceUrl, allowedHosts);
        switch (Provider)
        {
            case S3ProviderKind.Aws:
                if (serviceUrl is not null || ForcePathStyle || AllowInsecureHttp)
                {
                    throw Invalid(
                        "The AWS profile uses SDK regional endpoints and virtual-host addressing.");
                }

                break;
            case S3ProviderKind.CloudflareR2:
                if (serviceUrl is null ||
                    !serviceUrl.IsDefaultPort ||
                    region != "auto" ||
                    ForcePathStyle ||
                    !serviceUrl.Host.EndsWith(
                        ".r2.cloudflarestorage.com",
                        StringComparison.OrdinalIgnoreCase) ||
                    serviceUrl.Host.Split('.')[0].Length != 32 ||
                    serviceUrl.Host.Split('.')[0].Any(character =>
                        !Uri.IsHexDigit(character)))
                {
                    throw Invalid(
                        "The R2 profile requires its HTTPS account endpoint, region auto, and virtual-host addressing.");
                }

                break;
            case S3ProviderKind.BackblazeB2:
                string expectedB2Host = $"s3.{region}.backblazeb2.com";
                if (serviceUrl is null ||
                    !serviceUrl.IsDefaultPort ||
                    !string.Equals(
                        serviceUrl.Host,
                        expectedB2Host,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw Invalid(
                        "The B2 endpoint host must exactly match the configured region.");
                }

                break;
            case S3ProviderKind.Minio:
                if (serviceUrl is null ||
                    !allowedHosts.Contains(serviceUrl.Host))
                {
                    throw Invalid(
                        "The MinIO endpoint host must be explicitly allowlisted.");
                }

                UriHostNameType hostType = Uri.CheckHostName(serviceUrl.Host);
                if (!ForcePathStyle &&
                    (hostType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ||
                     string.Equals(
                         serviceUrl.Host,
                         "localhost",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    throw Invalid(
                        "IP-address MinIO endpoints require path-style addressing.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Provider));
        }

        return new S3ValidatedOptions(
            S3ProviderProfiles.Get(Provider),
            bucket,
            region,
            serviceUrl,
            ForcePathStyle,
            MaximumPresignLifetime,
            new ReadOnlyCollection<string>(
                allowedHosts.OrderBy(
                    value => value,
                    StringComparer.Ordinal).ToArray()));
    }

    private Uri ValidateEndpoint(Uri value, HashSet<string> allowedHosts)
    {
        if (!value.IsAbsoluteUri ||
            value.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment) ||
            value.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(value.Host))
        {
            throw Invalid(
                "S3 service URLs must be origin-only absolute HTTP URLs without credentials.");
        }

        if (value.Scheme == Uri.UriSchemeHttp &&
            (!AllowInsecureHttp || Provider != S3ProviderKind.Minio))
        {
            throw Invalid("S3 service URLs require HTTPS unless MinIO HTTP is explicitly enabled.");
        }

        if (value.Scheme == Uri.UriSchemeHttps && AllowInsecureHttp)
        {
            throw Invalid("AllowInsecureHttp cannot be enabled for an HTTPS endpoint.");
        }

        if (Provider == S3ProviderKind.Minio &&
            !allowedHosts.Contains(value.Host))
        {
            throw Invalid("The MinIO endpoint host must be explicitly allowlisted.");
        }

        return new Uri(value.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static string ValidateBucket(string value, bool forcePathStyle)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("The S3 bucket name is required.");
        }

        string bucket = value.Trim();
        if (bucket.Length is < 3 or > 63 ||
            bucket[0] is '-' or '.' ||
            bucket[^1] is '-' or '.' ||
            bucket.Contains("..", StringComparison.Ordinal) ||
            bucket.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '-' or '.')) ||
            IPAddress.TryParse(bucket, out _) ||
            (!forcePathStyle && bucket.Contains('.', StringComparison.Ordinal)))
        {
            throw Invalid("The S3 bucket name is invalid for the selected addressing style.");
        }

        return bucket;
    }

    private static string ValidateRegion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("The S3 signing region is required.");
        }

        string region = value.Trim();
        if (region.Length > 64 ||
            region.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '-')))
        {
            throw Invalid("The S3 signing region is invalid.");
        }

        return region;
    }

    private static string NormalizeHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("An allowed endpoint host is required.");
        }

        if (Uri.CheckHostName(value.Trim()) == UriHostNameType.Unknown)
        {
            throw Invalid("An allowed endpoint host is invalid.");
        }

        return value.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static S3ConfigurationException Invalid(string message) => new(message);
}

public sealed record S3ValidatedOptions(
    S3ProviderProfile Profile,
    string BucketName,
    string Region,
    Uri? ServiceUrl,
    bool ForcePathStyle,
    TimeSpan MaximumPresignLifetime,
    IReadOnlyCollection<string> AllowedEndpointHosts);

public sealed class S3ConfigurationException(string message) : Exception(message);
