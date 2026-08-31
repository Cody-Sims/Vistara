using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Contracts.Admin;

namespace Vistara.Api.Features.Admin;

/// <summary>
/// Validates candidate storage settings, including the credential the operator
/// entered, without touching the active configuration. The body is parsed by
/// hand so a submitted secret only ever lives inside a
/// <see cref="RedactedSecret"/>: it is never bound to a record, never echoed,
/// and never survives the request.
/// </summary>
public static class StorageValidationEndpoint
{
    private const string CodePrefix = "storage_validation";

    /// <summary>Upper bound for one validation, independent of the caller.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Bounds the request so a large body cannot be used as a probe.</summary>
    public const int MaximumBodyBytes = 16 * 1024;

    public const int MaximumSecretLength = 4_096;

    private const int MaximumFieldLength = 512;

    private const int MaximumPathLength = 4_096;

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly string[] SupportedProviders =
        ["filesystem", "azureBlob", "s3"];

    /// <summary>
    /// Tells the setup assistant that this deployment can test a credential, so
    /// a secret is never sent to a deployment that cannot check it.
    /// </summary>
    public static async Task DescribeAsync(
        HttpContext context,
        IAccountAuthorizationPort authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);

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

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new StorageValidationSupportResponse(true, SupportedProviders),
            ResponseJsonOptions);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

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

        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                $"{CodePrefix}.body_too_large",
                "The storage validation request is too large.",
                cancellationToken);
            return;
        }

        JsonDocument document;
        try
        {
            document = await ReadBoundedAsync(context.Request.Body, cancellationToken);
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
        catch (StorageValidationBodyTooLargeException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                $"{CodePrefix}.body_too_large",
                "The storage validation request is too large.",
                cancellationToken);
            return;
        }

        StorageValidationCandidate? candidate;
        string field;
        using (document)
        {
            if (!TryReadCandidate(document.RootElement, out candidate, out field))
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
        }

        StorageValidationOutcome outcome;
        string provider = candidate!.Provider;

        // The candidate owns the secrets, so leaving this scope zeroes them
        // whether the probe succeeded, failed, timed out, or was cancelled.
        using (candidate)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                outcome = await validation.ValidateAsync(candidate, timeout.Token);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                outcome = StorageValidationOutcome.Rejected(
                    "The storage target did not answer in time.",
                    "The provider did not answer within the validation timeout.");
            }
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new StorageValidationResponse(
                outcome.Valid,
                provider,
                [.. outcome.Checks.Select(check => new StorageValidationCheckResponse(
                    Describe(check.Id),
                    Describe(check.Status),
                    check.Detail))],
                outcome.Message),
            ResponseJsonOptions);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

    internal static string Describe(StorageCheckId id) =>
        id switch
        {
            StorageCheckId.Reachable => "reachable",
            StorageCheckId.Authenticated => "authenticated",
            StorageCheckId.Read => "read",
            StorageCheckId.Write => "write",
            _ => "delete",
        };

    internal static string Describe(StorageCheckStatus status) =>
        status switch
        {
            StorageCheckStatus.Passed => "passed",
            StorageCheckStatus.Failed => "failed",
            _ => "skipped",
        };

    private static async Task<JsonDocument> ReadBoundedAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes)
            {
                throw new StorageValidationBodyTooLargeException();
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(
            buffer,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Accepts exactly one provider shape and only its allowlisted members.
    /// Secrets are lifted straight into <see cref="RedactedSecret"/> instances
    /// so no intermediate string is retained by a DTO.
    /// </summary>
    internal static bool TryReadCandidate(
        JsonElement root,
        out StorageValidationCandidate? candidate,
        out string field)
    {
        candidate = null;
        field = "provider";
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool hasFilesystem = root.TryGetProperty("filesystem", out JsonElement filesystem) &&
            filesystem.ValueKind == JsonValueKind.Object;
        bool hasAzure = root.TryGetProperty("azureBlob", out JsonElement azure) &&
            azure.ValueKind == JsonValueKind.Object;
        bool hasS3 = root.TryGetProperty("s3", out JsonElement s3) &&
            s3.ValueKind == JsonValueKind.Object;
        int offered = (hasFilesystem ? 1 : 0) + (hasAzure ? 1 : 0) + (hasS3 ? 1 : 0);
        if (offered != 1 ||
            !root.TryGetProperty("provider", out JsonElement provider) ||
            provider.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return provider.GetString() switch
        {
            "filesystem" when hasFilesystem =>
                TryReadFilesystem(filesystem, out candidate, out field),
            "azureBlob" when hasAzure => TryReadAzure(azure, out candidate, out field),
            "s3" when hasS3 => TryReadS3(s3, out candidate, out field),
            _ => false,
        };
    }

    private static bool TryReadFilesystem(
        JsonElement element,
        out StorageValidationCandidate? candidate,
        out string field)
    {
        candidate = null;
        field = "filesystem.rootPath";
        string? rootPath = ReadText(element, "rootPath", MaximumPathLength);
        if (!IsAcceptablePath(rootPath))
        {
            return false;
        }

        candidate = new StorageValidationCandidate(
            StorageCandidateKind.Filesystem,
            "filesystem",
            rootPath: rootPath);
        return true;
    }

    private static bool TryReadAzure(
        JsonElement element,
        out StorageValidationCandidate? candidate,
        out string field)
    {
        candidate = null;
        field = "azureBlob.accountName";
        string? accountName = ReadText(element, "accountName", 24);
        if (!IsAcceptableAccount(accountName))
        {
            return false;
        }

        field = "azureBlob.container";
        string? container = ReadText(element, "container", 63);
        if (!IsAcceptableContainer(container))
        {
            return false;
        }

        field = "azureBlob.endpointSuffix";
        string suffix = ReadText(element, "endpointSuffix", 128) ?? "core.windows.net";
        if (!IsAcceptableHostLabelSequence(suffix))
        {
            return false;
        }

        field = "azureBlob.credentialKind";
        string credentialKind =
            ReadText(element, "credentialKind", 32) ?? "managedIdentity";
        AzureCredentialKind kind;
        RedactedSecret? accountKey = null;
        RedactedSecret? sasToken = null;
        switch (credentialKind)
        {
            case "managedIdentity":
                kind = AzureCredentialKind.ManagedIdentity;
                break;
            case "accountKey":
                kind = AzureCredentialKind.AccountKey;
                field = "azureBlob.accountKey";
                accountKey = ReadSecret(element, "accountKey");
                if (accountKey is null)
                {
                    return false;
                }

                break;
            case "sasToken":
                kind = AzureCredentialKind.SasToken;
                field = "azureBlob.sasToken";
                sasToken = ReadSecret(element, "sasToken");
                if (sasToken is null)
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        field = "azureBlob.accountName";
        if (!Uri.TryCreate(
                $"https://{accountName}.blob.{suffix}",
                UriKind.Absolute,
                out Uri? endpoint))
        {
            accountKey?.Dispose();
            sasToken?.Dispose();
            return false;
        }

        candidate = new StorageValidationCandidate(
            StorageCandidateKind.AzureBlob,
            "azureBlob",
            endpoint: endpoint,
            container: container,
            accountName: accountName,
            azureCredential: kind,
            accountKey: accountKey,
            sasToken: sasToken);
        return true;
    }

    private static bool TryReadS3(
        JsonElement element,
        out StorageValidationCandidate? candidate,
        out string field)
    {
        candidate = null;
        field = "s3.bucket";
        string? bucket = ReadText(element, "bucket", 63);
        if (!IsAcceptableContainer(bucket))
        {
            return false;
        }

        field = "s3.region";
        string? region = ReadText(element, "region", 64);
        if (!IsAcceptableName(region))
        {
            return false;
        }

        field = "s3.endpoint";
        if (!TryReadEndpoint(
                ReadText(element, "endpoint", MaximumFieldLength),
                out Uri? endpoint))
        {
            return false;
        }

        bool forcePathStyle =
            element.TryGetProperty("forcePathStyle", out JsonElement pathStyle) &&
            pathStyle.ValueKind == JsonValueKind.True;

        RedactedSecret? accessKeyId = ReadSecret(element, "accessKeyId");
        RedactedSecret? secretAccessKey = ReadSecret(element, "secretAccessKey");
        RedactedSecret? sessionToken = ReadSecret(element, "sessionToken");
        S3CredentialKind kind;
        if (accessKeyId is null && secretAccessKey is null)
        {
            if (sessionToken is not null)
            {
                sessionToken.Dispose();
                field = "s3.accessKeyId";
                return false;
            }

            kind = S3CredentialKind.Anonymous;
        }
        else if (accessKeyId is not null && secretAccessKey is not null)
        {
            kind = S3CredentialKind.AccessKey;
        }
        else
        {
            accessKeyId?.Dispose();
            secretAccessKey?.Dispose();
            sessionToken?.Dispose();
            field = "s3.secretAccessKey";
            return false;
        }

        candidate = new StorageValidationCandidate(
            StorageCandidateKind.S3,
            "s3",
            endpoint: endpoint,
            container: bucket,
            region: region,
            forcePathStyle: forcePathStyle,
            s3Credential: kind,
            accessKeyId: accessKeyId,
            secretAccessKey: secretAccessKey,
            sessionToken: sessionToken);
        return true;
    }

    private static RedactedSecret? ReadSecret(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement member) &&
        member.ValueKind == JsonValueKind.String &&
        member.GetString() is { Length: > 0 } value &&
        value.Length <= MaximumSecretLength
            ? RedactedSecret.From(value)
            : null;

    private static string? ReadText(JsonElement element, string name, int maximum) =>
        element.TryGetProperty(name, out JsonElement member) &&
        member.ValueKind == JsonValueKind.String &&
        member.GetString() is { Length: > 0 } value &&
        value.Length <= maximum
            ? value
            : null;

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

    private static bool IsAcceptableAccount(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 3 and <= 24 &&
        value.All(character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));

    private static bool IsAcceptableContainer(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 3 and <= 63 &&
        value.All(character =>
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) ||
            character is '-' or '.');

    private static bool IsAcceptableHostLabelSequence(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.') &&
        !value.StartsWith('.') &&
        !value.EndsWith('.');
}

/// <summary>Signals that the submitted body exceeded the accepted bound.</summary>
internal sealed class StorageValidationBodyTooLargeException : Exception
{
    public StorageValidationBodyTooLargeException()
        : base("The storage validation request body is too large.")
    {
    }
}
