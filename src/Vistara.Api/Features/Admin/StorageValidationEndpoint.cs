using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Contracts.Admin;

namespace Vistara.Api.Features.Admin;

/// <summary>
/// Validates candidate storage settings for the setup assistant without
/// touching the active configuration. The request is ephemeral: no field is
/// persisted, logged, or echoed, and the answer is a fixed shape that never
/// carries provider text.
/// </summary>
public static class StorageValidationEndpoint
{
    private const string CodePrefix = "storage_validation";

    /// <summary>Upper bound for one probe, independent of the caller.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private const int MaximumFieldLength = 512;

    private const int MaximumPathLength = 4_096;

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task ValidateAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        IStorageValidationPort validation,
        IPlatformRateLimitHook rateLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(rateLimit);

        AccountAccess access = await authorization.AuthorizeAsync(
            context,
            AccountOperation.ManageQuotas,
            cancellationToken);
        if (access.Actor is null)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                access.Status == AccountAccessStatus.Unauthenticated
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status403Forbidden,
                access.Status == AccountAccessStatus.Unauthenticated
                    ? $"{CodePrefix}.unauthenticated"
                    : $"{CodePrefix}.forbidden",
                "Only a tenant owner may validate storage settings.",
                cancellationToken);
            return;
        }

        PlatformRateLimitDecision decision =
            await rateLimit.CheckAsync(context, cancellationToken);
        if (!decision.IsAllowed)
        {
            if (decision.RetryAfter is { } retryAfter)
            {
                context.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                $"{CodePrefix}.throttled",
                "Storage validation is throttled; retry later.",
                cancellationToken);
            return;
        }

        ValidateStorageRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ValidateStorageRequest>(
                context.Request.Body,
                ResponseJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"{CodePrefix}.malformed_request",
                "The storage validation request could not be parsed.",
                cancellationToken);
            return;
        }

        if (request is null)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"{CodePrefix}.malformed_request",
                "The storage validation request is required.",
                cancellationToken);
            return;
        }

        if (!TryReadTarget(request, out StorageValidationTarget? target, out string field))
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                $"{CodePrefix}.invalid_request",
                "The storage candidate is invalid.",
                cancellationToken,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    [field] = ["The value is not accepted for this provider."],
                });
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        StorageValidationOutcome outcome;
        try
        {
            outcome = await validation.ValidateAsync(target!, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = StorageValidationOutcome.TimedOut;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new StorageValidationResponse(
                outcome.Reachable,
                target!.Provider,
                outcome.Code,
                outcome.Message),
            ResponseJsonOptions);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Accepts only the allowlisted, non-secret members of one provider shape
    /// and rejects everything else, including a request that names more than
    /// one provider.
    /// </summary>
    internal static bool TryReadTarget(
        ValidateStorageRequest request,
        out StorageValidationTarget? target,
        out string field)
    {
        target = null;
        field = "provider";
        int offered =
            (request.Filesystem is null ? 0 : 1) +
            (request.Azure is null ? 0 : 1) +
            (request.S3 is null ? 0 : 1);
        if (offered != 1)
        {
            return false;
        }

        switch (request.Provider)
        {
            case "filesystem" when request.Filesystem is { } filesystem:
                field = "filesystem.rootPath";
                if (!IsAcceptablePath(filesystem.RootPath))
                {
                    return false;
                }

                target = new StorageValidationTarget(
                    StorageCandidateKind.Filesystem,
                    "filesystem",
                    filesystem.RootPath,
                    null,
                    null);
                return true;
            case "azure" when request.Azure is { } azure:
                field = "azure.serviceUri";
                if (!IsAcceptableName(azure.AccountName) ||
                    !IsAcceptableContainer(azure.ContainerName) ||
                    !TryReadEndpoint(azure.ServiceUri, out Uri? azureUri))
                {
                    return false;
                }

                target = new StorageValidationTarget(
                    StorageCandidateKind.Azure,
                    "azure",
                    null,
                    azureUri,
                    azure.ContainerName);
                return true;
            case "s3" when request.S3 is { } s3:
                field = "s3.serviceUrl";
                if (!IsAcceptableContainer(s3.BucketName) ||
                    !IsAcceptableName(s3.Region) ||
                    !TryReadEndpoint(s3.ServiceUrl, out Uri? s3Uri))
                {
                    return false;
                }

                target = new StorageValidationTarget(
                    StorageCandidateKind.S3,
                    "s3",
                    null,
                    s3Uri,
                    s3.BucketName);
                return true;
            default:
                return false;
        }
    }

    internal static bool TryReadEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumFieldLength)
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.HostNameType == UriHostNameType.Unknown ||
            parsed.Host.Length is 0 or > 253)
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    /// <summary>
    /// Rejects loopback, private, link-local, unique-local, and multicast
    /// literals so the probe cannot be pointed at the deployment's own network
    /// or a cloud metadata service.
    /// </summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlockedAddress(address.MapToIPv4());
            }

            byte first = address.GetAddressBytes()[0];
            return (first & 0xfe) == 0xfc;
        }

        byte[] octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,
            127 => true,
            0 => true,
            169 when octets[1] == 254 => true,
            172 when octets[1] >= 16 && octets[1] <= 31 => true,
            192 when octets[1] == 168 => true,
            100 when octets[1] >= 64 && octets[1] <= 127 => true,
            >= 224 => true,
            _ => false,
        };
    }

    private static bool IsAcceptablePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumPathLength &&
        Path.IsPathFullyQualified(value) &&
        value.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
        !value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is ".." or ".");

    private static bool IsAcceptableName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsAcceptableContainer(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 3 and <= 63 &&
        value.All(character =>
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) ||
            character is '-' or '.');
}
