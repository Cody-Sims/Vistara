using System.Collections.ObjectModel;

namespace Vistara.Application.Common.Storage;

public enum HttpMethodKind
{
    Get,
    Put,
    Post,
    Delete,
}

public sealed class SignedHttpRequest
{
    private readonly IReadOnlyDictionary<string, string> _headers;

    public SignedHttpRequest(
        HttpMethodKind method,
        Uri url,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Signed request URLs must be absolute HTTP URLs.",
                nameof(url));
        }

        Dictionary<string, string> copy = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in
                 headers ?? Array.Empty<KeyValuePair<string, string>>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(value);
            if (!copy.TryAdd(name.Trim(), value))
            {
                throw new ArgumentException(
                    $"Duplicate signed request header '{name}'.",
                    nameof(headers));
            }
        }

        Method = method;
        Url = url;
        _headers = new ReadOnlyDictionary<string, string>(copy);
    }

    public HttpMethodKind Method { get; }

    public Uri Url { get; }

    public IReadOnlyDictionary<string, string> Headers => _headers;

    public override string ToString() => $"{Method} [signed request redacted]";
}

public sealed record DirectUploadRequest
{
    public DirectUploadRequest(
        BlobKey key,
        long contentLength,
        BlobMediaType contentType,
        BlobChecksum? checksum,
        BlobRequestConditions conditions,
        TimeSpan lifetime,
        BlobMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentLength);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(metadata);
        Key = key;
        ContentLength = contentLength;
        ContentType = contentType;
        Checksum = checksum;
        Conditions = conditions;
        Lifetime = lifetime;
        Metadata = metadata;
    }

    public BlobKey Key { get; }

    public long ContentLength { get; }

    public BlobMediaType ContentType { get; }

    public BlobChecksum? Checksum { get; }

    public BlobRequestConditions Conditions { get; }

    public TimeSpan Lifetime { get; }

    public BlobMetadata Metadata { get; }
}

public sealed record DirectUploadPlan
{
    public DirectUploadPlan(
        BlobKey key,
        SignedHttpRequest request,
        DateTimeOffset expiresAtUtc,
        BlobRequestConditions conditions,
        BlobChecksum? requiredChecksum)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(conditions);
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        Key = key;
        Request = request;
        ExpiresAtUtc = expiresAtUtc;
        Conditions = conditions;
        RequiredChecksum = requiredChecksum;
    }

    public BlobKey Key { get; }

    public SignedHttpRequest Request { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public BlobRequestConditions Conditions { get; }

    public BlobChecksum? RequiredChecksum { get; }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public sealed record MultipartRequest
{
    public MultipartRequest(
        BlobKey key,
        long contentLength,
        BlobMediaType contentType,
        BlobChecksum? checksum,
        BlobRequestConditions conditions,
        TimeSpan lifetime,
        BlobMetadata metadata)
        : this(
            key,
            contentLength,
            contentType,
            checksum,
            conditions,
            lifetime,
            lifetime,
            metadata)
    {
    }

    public MultipartRequest(
        BlobKey key,
        long contentLength,
        BlobMediaType contentType,
        BlobChecksum? checksum,
        BlobRequestConditions conditions,
        TimeSpan sessionLifetime,
        TimeSpan partPlanLifetime,
        BlobMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentLength);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            sessionLifetime,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            partPlanLifetime,
            TimeSpan.Zero);
        if (partPlanLifetime > sessionLifetime)
        {
            throw new ArgumentException(
                "A multipart part plan cannot outlive its upload session.",
                nameof(partPlanLifetime));
        }

        ArgumentNullException.ThrowIfNull(metadata);
        Key = key;
        ContentLength = contentLength;
        ContentType = contentType;
        Checksum = checksum;
        Conditions = conditions;
        SessionLifetime = sessionLifetime;
        PartPlanLifetime = partPlanLifetime;
        Metadata = metadata;
    }

    public BlobKey Key { get; }

    public long ContentLength { get; }

    public BlobMediaType ContentType { get; }

    public BlobChecksum? Checksum { get; }

    public BlobRequestConditions Conditions { get; }

    public TimeSpan Lifetime => SessionLifetime;

    public TimeSpan SessionLifetime { get; }

    public TimeSpan PartPlanLifetime { get; }

    public BlobMetadata Metadata { get; }
}

public sealed record MultipartSession
{
    public MultipartSession(
        string uploadId,
        BlobKey key,
        DateTimeOffset expiresAtUtc,
        long contentLength,
        BlobRequestConditions completionConditions,
        int maxParts,
        long minPartBytes,
        long maxPartBytes,
        TimeSpan? partPlanLifetime = null,
        BlobMediaType? contentType = null,
        BlobChecksum? checksum = null,
        BlobMetadata? metadata = null,
        string? providerState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        ArgumentNullException.ThrowIfNull(key);
        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(expiresAtUtc));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentLength);
        ArgumentNullException.ThrowIfNull(completionConditions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minPartBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartBytes);
        if (minPartBytes > maxPartBytes)
        {
            throw new ArgumentException(
                "The multipart minimum cannot exceed the multipart maximum.");
        }

        TimeSpan effectivePartPlanLifetime =
            partPlanLifetime ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            effectivePartPlanLifetime,
            TimeSpan.Zero);
        string effectiveProviderState =
            string.IsNullOrWhiteSpace(providerState)
                ? uploadId.Trim()
                : providerState.Trim();
        if (effectiveProviderState.Length > 8_192 ||
            effectiveProviderState.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The multipart provider state is invalid.",
                nameof(providerState));
        }

        UploadId = uploadId.Trim();
        Key = key;
        ExpiresAtUtc = expiresAtUtc;
        ContentLength = contentLength;
        CompletionConditions = completionConditions;
        MaxParts = maxParts;
        MinPartBytes = minPartBytes;
        MaxPartBytes = maxPartBytes;
        PartPlanLifetime = effectivePartPlanLifetime;
        ContentType = contentType ??
            new BlobMediaType("application/octet-stream");
        Checksum = checksum;
        Metadata = metadata ?? BlobMetadata.Empty;
        ProviderState = effectiveProviderState;
    }

    public string UploadId { get; }

    public BlobKey Key { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public long ContentLength { get; }

    public BlobRequestConditions CompletionConditions { get; }

    public int MaxParts { get; }

    public long MinPartBytes { get; }

    public long MaxPartBytes { get; }

    public TimeSpan PartPlanLifetime { get; }

    public BlobMediaType ContentType { get; }

    public BlobChecksum? Checksum { get; }

    public BlobMetadata Metadata { get; }

    public string ProviderState { get; }
}

public sealed record MultipartPartPlan(
    string UploadId,
    int PartNumber,
    SignedHttpRequest Request,
    long MinBytes,
    long MaxBytes,
    DateTimeOffset ExpiresAtUtc);

public sealed record UploadedPart
{
    public UploadedPart(
        int partNumber,
        BlobEntityTag entityTag,
        BlobChecksum? checksum,
        long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        ArgumentNullException.ThrowIfNull(entityTag);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        PartNumber = partNumber;
        EntityTag = entityTag;
        Checksum = checksum;
        SizeBytes = sizeBytes;
    }

    public int PartNumber { get; }

    public BlobEntityTag EntityTag { get; }

    public BlobChecksum? Checksum { get; }

    public long SizeBytes { get; }
}

public sealed record MultipartCompletion(BlobHead Head);

public sealed record ReadGrantOptions
{
    public ReadGrantOptions(
        TimeSpan lifetime,
        BlobRange? range = null,
        string? downloadFileName = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        Lifetime = lifetime;
        Range = range;
        DownloadFileName = string.IsNullOrWhiteSpace(downloadFileName)
            ? null
            : downloadFileName.Trim();
    }

    public TimeSpan Lifetime { get; }

    public BlobRange? Range { get; }

    public string? DownloadFileName { get; }
}

public sealed record SignedAccessPlan(
    BlobKey Key,
    SignedHttpRequest Request,
    DateTimeOffset ExpiresAtUtc,
    BlobRange? Range);
