using Microsoft.AspNetCore.Http;
using Vistara.Contracts.Media;

namespace Vistara.Api.Features.Media;

public enum MediaDeliveryAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
    Concealed,
}

public sealed class MediaDeliveryCredential
{
    private const int MaximumLength = 128;

    private MediaDeliveryCredential(string plaintextToken)
    {
        PlaintextToken = plaintextToken;
    }

    public string PlaintextToken { get; }

    public override string ToString() =>
        MediaDeliveryHttpContract.RedactedCredential;

    internal static bool TryCreate(
        string? plaintextToken,
        out MediaDeliveryCredential? credential)
    {
        if (plaintextToken is null ||
            plaintextToken.Length is < 1 or > MaximumLength ||
            plaintextToken.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_')))
        {
            credential = null;
            return false;
        }

        credential = new MediaDeliveryCredential(plaintextToken);
        return true;
    }
}

public sealed record MediaDeliveryAccess
{
    private MediaDeliveryAccess(
        MediaDeliveryAccessStatus status,
        Guid? tenantId,
        Guid? assetId)
    {
        Status = status;
        TenantId = tenantId;
        AssetId = assetId;
    }

    public MediaDeliveryAccessStatus Status { get; }

    public Guid? TenantId { get; }

    public Guid? AssetId { get; }

    public static MediaDeliveryAccess AuthorizedTenant(Guid tenantId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        return new(MediaDeliveryAccessStatus.Authorized, tenantId, null);
    }

    public static MediaDeliveryAccess AuthorizedAsset(Guid tenantId, Guid assetId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(assetId, nameof(assetId));
        return new(MediaDeliveryAccessStatus.Authorized, tenantId, assetId);
    }

    public static MediaDeliveryAccess Denied(MediaDeliveryAccessStatus status)
    {
        if (status == MediaDeliveryAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new(status, null, null);
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "The identifier must be a UUIDv7 value.",
                parameterName);
        }
    }
}

public interface IMediaDeliveryAuthorizationPort
{
    ValueTask<MediaDeliveryAccess> AuthorizePrivateDerivativeAsync(
        HttpContext context,
        MediaDeliveryCredential? credential,
        CancellationToken cancellationToken);

    ValueTask<MediaDeliveryAccess> AuthorizeOriginalAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken);
}

public sealed record MediaTenantScope
{
    public MediaTenantScope(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException(
                "The tenant ID must be UUIDv7.",
                nameof(tenantId));
        }

        TenantId = tenantId;
    }

    public Guid TenantId { get; }
}

public sealed record MediaAssetScope
{
    public MediaAssetScope(Guid tenantId, Guid assetId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException(
                "The tenant ID must be UUIDv7.",
                nameof(tenantId));
        }

        if (assetId == Guid.Empty || assetId.Version != 7)
        {
            throw new ArgumentException(
                "The asset ID must be UUIDv7.",
                nameof(assetId));
        }

        TenantId = tenantId;
        AssetId = assetId;
    }

    public Guid TenantId { get; }

    public Guid AssetId { get; }
}

public sealed record MediaDerivativeRequest
{
    public MediaDerivativeRequest(
        string pipeline,
        string sourceHash,
        string recipeHash,
        string extension)
    {
        Pipeline = ValidatePipeline(pipeline);
        SourceHash = ValidateHash(sourceHash, nameof(sourceHash));
        RecipeHash = ValidateHash(recipeHash, nameof(recipeHash));
        Extension = ValidateExtension(extension);
    }

    public string Pipeline { get; }

    public string SourceHash { get; }

    public string RecipeHash { get; }

    public string Extension { get; }

    private static string ValidatePipeline(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 ||
            value.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '-' or '_')))
        {
            throw new ArgumentException(
                "The pipeline identifier is invalid.",
                nameof(value));
        }

        return value;
    }

    private static string ValidateHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 ||
            value.Any(character =>
                !(char.IsAsciiDigit(character) ||
                  character is >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Media hashes must be lowercase SHA-256 values.",
                parameterName);
        }

        return value;
    }

    private static string ValidateExtension(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is not ("jpg" or "jpeg" or "png" or "webp"))
        {
            throw new ArgumentException(
                "The media extension is invalid.",
                nameof(value));
        }

        return value;
    }
}

public sealed record MediaByteRange
{
    public MediaByteRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _ = checked(offset + length);
        Offset = offset;
        Length = length;
    }

    public long Offset { get; }

    public long Length { get; }
}

public interface IMediaContentSource
{
    ValueTask<MediaReadHandle> OpenReadAsync(
        MediaByteRange? range,
        CancellationToken cancellationToken);
}

public sealed class MediaReadHandle : IAsyncDisposable
{
    public MediaReadHandle(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException(
                "The media content stream must be readable.",
                nameof(content));
        }

        Content = content;
    }

    public Stream Content { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record MediaRepresentation
{
    public MediaRepresentation(
        long contentLength,
        string contentType,
        string sha256,
        IMediaContentSource source,
        string? downloadFileName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(source);
        if (contentType.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(contentType));
        }

        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The representation hash must be a SHA-256 value.",
                nameof(sha256));
        }

        if (downloadFileName?.Length > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadFileName));
        }

        ContentLength = contentLength;
        ContentType = contentType;
        Sha256 = sha256.ToLowerInvariant();
        Source = source;
        DownloadFileName = downloadFileName;
    }

    public long ContentLength { get; }

    public string ContentType { get; }

    public string Sha256 { get; }

    public IMediaContentSource Source { get; }

    public string? DownloadFileName { get; }
}

public enum MediaDeliveryStatus
{
    Ready,
    Queued,
    NotFound,
}

public sealed record MediaDeliveryResult
{
    private MediaDeliveryResult(
        MediaDeliveryStatus status,
        MediaRepresentation? representation)
    {
        Status = status;
        Representation = representation;
    }

    public MediaDeliveryStatus Status { get; }

    public MediaRepresentation? Representation { get; }

    public static MediaDeliveryResult Ready(MediaRepresentation representation)
    {
        ArgumentNullException.ThrowIfNull(representation);
        return new(MediaDeliveryStatus.Ready, representation);
    }

    public static MediaDeliveryResult Queued() =>
        new(MediaDeliveryStatus.Queued, null);

    public static MediaDeliveryResult NotFound() =>
        new(MediaDeliveryStatus.NotFound, null);
}

public interface IMediaDeliveryApplicationPort
{
    ValueTask<MediaDeliveryResult> ResolvePublicDerivativeAsync(
        MediaDerivativeRequest request,
        CancellationToken cancellationToken);

    ValueTask<MediaDeliveryResult> ResolvePrivateDerivativeAsync(
        MediaTenantScope scope,
        MediaDerivativeRequest request,
        CancellationToken cancellationToken);

    ValueTask<MediaDeliveryResult> ResolveOriginalAsync(
        MediaAssetScope scope,
        CancellationToken cancellationToken);
}
