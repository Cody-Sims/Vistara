using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Vistara.Application.Capabilities;
using Vistara.Contracts.Capabilities;

namespace Vistara.Api.Features.Capabilities;

/// <summary>
/// Serves the tenant-scoped, redacted capability document for
/// <c>GET /api/v1/capabilities</c>.
/// </summary>
public static class CapabilitiesEndpoint
{
    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task GetAsync(
        HttpContext context,
        ICapabilitiesAuthorizationPort authorization,
        ICapabilitySnapshotProvider snapshots,
        CapabilitiesSurfaceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(options);

        CapabilitiesAccess access =
            await authorization.AuthorizeAsync(context, cancellationToken);
        switch (access.Status)
        {
            case CapabilitiesAccessStatus.Unauthenticated:
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "capabilities.unauthenticated",
                    "Authentication is required to read platform capabilities.",
                    cancellationToken);
                return;
            case CapabilitiesAccessStatus.Forbidden:
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "capabilities.forbidden",
                    "The caller is not permitted to read platform capabilities.",
                    cancellationToken);
                return;
            case CapabilitiesAccessStatus.Authorized:
            default:
                break;
        }

        CapabilitySnapshot snapshot =
            await snapshots.GetAsync(access.TenantId, cancellationToken);
        CapabilitiesResponse response = Map(snapshot);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            response,
            ResponseJsonOptions);
        string etag = ComputeETag(payload);

        context.Response.Headers.CacheControl =
            $"private, max-age={(int)options.CacheLifetime.TotalSeconds}";
        context.Response.Headers[HeaderNames.Vary] = "Authorization, Cookie, X-API-Key";
        context.Response.Headers.ETag = etag;

        if (MatchesConditional(context.Request, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload, cancellationToken);
    }

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

    private static string ComputeETag(ReadOnlySpan<byte> payload) =>
        $"\"{Convert.ToHexStringLower(SHA256.HashData(payload))}\"";

    private static CapabilitiesResponse Map(CapabilitySnapshot snapshot) =>
        new(
            snapshot.SchemaVersion,
            new DatabaseCapabilitiesResponse(snapshot.DatabaseProvider),
            new StorageCapabilitiesResponse(
                snapshot.Storage.Provider,
                snapshot.Storage.DirectUpload,
                snapshot.Storage.MultipartUpload,
                snapshot.Storage.RangeReads,
                snapshot.Storage.MaxObjectBytes,
                snapshot.Storage.MaxMultipartParts,
                snapshot.Storage.MinMultipartPartBytes,
                snapshot.Storage.MaxMultipartPartBytes),
            new ImagingCapabilitiesResponse(
                snapshot.Imaging.Provider,
                snapshot.Imaging.InputFormats,
                snapshot.Imaging.OutputFormats,
                snapshot.Imaging.MaxEncodedBytes,
                snapshot.Imaging.MaxWidth,
                snapshot.Imaging.MaxHeight,
                snapshot.Imaging.MaxAggregatePixels,
                snapshot.Imaging.MaxFrames,
                snapshot.Imaging.MaxEstimatedDecodedBytes,
                snapshot.Imaging.ProcessingDeadlineSeconds,
                snapshot.Imaging.MaxConcurrentTransforms),
            new UploadCapabilitiesResponse(
                snapshot.Upload.MaxBytes,
                snapshot.Upload.MaxConcurrentUploads,
                snapshot.Upload.ConcurrencyUnlimited,
                snapshot.Upload.MultipartThresholdBytes,
                snapshot.Upload.ProxyUpload,
                snapshot.Upload.DirectUpload,
                snapshot.Upload.MultipartUpload),
            new SearchCapabilitiesResponse(
                snapshot.Search.Text,
                snapshot.Search.Facets,
                snapshot.Search.Timeline,
                snapshot.Search.ProviderNativeFullText),
            new ApiCapabilitiesResponse(
                snapshot.Api.DefaultPageSize,
                snapshot.Api.MaxPageSize,
                snapshot.Api.MaxProxyUploadBytes));
}
