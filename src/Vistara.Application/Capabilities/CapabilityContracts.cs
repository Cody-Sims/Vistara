namespace Vistara.Application.Capabilities;

/// <summary>
/// Operator-configurable, non-secret bounds published by the capability surface.
/// </summary>
public sealed class CapabilitiesSurfaceOptions
{
    public CapabilitiesImagingOptions Imaging { get; } = new();

    public CapabilitiesSearchOptions Search { get; } = new();

    /// <summary>Default page size advertised for cursor listings.</summary>
    public int DefaultPageSize { get; set; } = 60;

    /// <summary>Maximum page size advertised for cursor listings.</summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>Private cache lifetime advertised for the capability document.</summary>
    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Decode and transform ceilings mirroring the configured imaging pipeline limits.
/// </summary>
public sealed class CapabilitiesImagingOptions
{
    public long MaxEncodedBytes { get; set; } = 50L * 1024 * 1024;

    public int MaxWidth { get; set; } = 20_000;

    public int MaxHeight { get; set; } = 20_000;

    public long MaxAggregatePixels { get; set; } = 40_000_000;

    public long MaxEstimatedDecodedBytes { get; set; } = 512L * 1024 * 1024;

    public TimeSpan ProcessingDeadline { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxConcurrentTransforms { get; set; } = 1;
}

/// <summary>
/// Search features that the deployed persistence provider exposes.
/// </summary>
public sealed class CapabilitiesSearchOptions
{
    public bool Text { get; set; } = true;

    public bool Facets { get; set; } = true;

    public bool Timeline { get; set; } = true;
}

/// <summary>
/// Tenant-scoped quota ceilings that narrow the advertised deployment limits.
/// </summary>
/// <param name="MaxUploadBytes">Tenant upload ceiling, or <c>null</c> to inherit the deployment ceiling.</param>
/// <param name="MaxConcurrentUploads">Concurrent upload ceiling, or <c>null</c> when unlimited.</param>
public sealed record TenantCapabilityLimits(long? MaxUploadBytes, long? MaxConcurrentUploads)
{
    public static TenantCapabilityLimits Unlimited { get; } = new(null, null);
}

/// <summary>
/// Resolves the tenant quota ceilings that constrain advertised capabilities.
/// </summary>
public interface ITenantCapabilitySource
{
    ValueTask<TenantCapabilityLimits> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Produces the tenant-scoped capability snapshot for the current deployment.
/// </summary>
public interface ICapabilitySnapshotProvider
{
    ValueTask<CapabilitySnapshot> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken);
}

public sealed record CapabilitySnapshot(
    int SchemaVersion,
    string DatabaseProvider,
    StorageCapabilityView Storage,
    ImagingCapabilityView Imaging,
    UploadCapabilityView Upload,
    SearchCapabilityView Search,
    ApiCapabilityView Api);

public sealed record StorageCapabilityView(
    string Provider,
    bool DirectUpload,
    bool MultipartUpload,
    bool RangeReads,
    long MaxObjectBytes,
    int MaxMultipartParts,
    long MinMultipartPartBytes,
    long MaxMultipartPartBytes);

public sealed record ImagingCapabilityView(
    string Provider,
    IReadOnlyList<string> InputFormats,
    IReadOnlyList<string> OutputFormats,
    long MaxEncodedBytes,
    int MaxWidth,
    int MaxHeight,
    long MaxAggregatePixels,
    int MaxFrames,
    long MaxEstimatedDecodedBytes,
    int ProcessingDeadlineSeconds,
    int MaxConcurrentTransforms);

public sealed record UploadCapabilityView(
    long MaxBytes,
    long MaxConcurrentUploads,
    bool ConcurrencyUnlimited,
    long MultipartThresholdBytes,
    bool ProxyUpload,
    bool DirectUpload,
    bool MultipartUpload);

public sealed record SearchCapabilityView(
    bool Text,
    bool Facets,
    bool Timeline,
    bool ProviderNativeFullText);

public sealed record ApiCapabilityView(
    int DefaultPageSize,
    int MaxPageSize,
    long MaxProxyUploadBytes);
