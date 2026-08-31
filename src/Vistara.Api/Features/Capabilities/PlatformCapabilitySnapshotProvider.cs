using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Application.Capabilities;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Persistence;
using Vistara.Persistence.Uploads;

namespace Vistara.Api.Features.Capabilities;

/// <summary>
/// Builds the capability snapshot from the composed storage, imaging, persistence,
/// and upload adapters without exposing any configured secret or location.
/// </summary>
public sealed class PlatformCapabilitySnapshotProvider(
    IBlobStore blobStore,
    IImageProcessor imageProcessor,
    VistaraPersistenceOptions persistence,
    UploadPersistenceOptions uploads,
    IOptions<MediaOptions> media,
    ITenantCapabilitySource tenants,
    CapabilitiesSurfaceOptions options) : ICapabilitySnapshotProvider
{
    private readonly IBlobStore _blobStore =
        blobStore ?? throw new ArgumentNullException(nameof(blobStore));

    private readonly IImageProcessor _imageProcessor =
        imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));

    private readonly VistaraPersistenceOptions _persistence =
        persistence ?? throw new ArgumentNullException(nameof(persistence));

    private readonly UploadPersistenceOptions _uploads =
        uploads ?? throw new ArgumentNullException(nameof(uploads));

    private readonly MediaOptions _media = media is null
        ? throw new ArgumentNullException(nameof(media))
        : media.Value;

    private readonly ITenantCapabilitySource _tenants =
        tenants ?? throw new ArgumentNullException(nameof(tenants));

    private readonly CapabilitiesSurfaceOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<CapabilitySnapshot> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TenantCapabilityLimits limits =
            await _tenants.GetAsync(tenantId, cancellationToken);

        BlobStoreCapabilities storage = _blobStore.Capabilities;
        ImageProcessorCapabilities imaging = _imageProcessor.Capabilities;
        long maxUploadBytes = Math.Min(
            _uploads.MaximumUploadBytes,
            limits.MaxUploadBytes ?? _uploads.MaximumUploadBytes);
        bool concurrencyUnlimited = limits.MaxConcurrentUploads is null;

        return new CapabilitySnapshot(
            Contracts.Capabilities.CapabilitiesResponse.CurrentSchemaVersion,
            DescribeDatabase(_persistence.Provider),
            new StorageCapabilityView(
                _blobStore.Name,
                storage.SupportsDirectUpload,
                storage.SupportsMultipartUpload,
                storage.SupportsRangeReads,
                storage.Limits.MaxObjectBytes,
                storage.Limits.MaxMultipartParts,
                storage.Limits.MinMultipartPartBytes,
                storage.Limits.MaxMultipartPartBytes),
            new ImagingCapabilityView(
                DescribeImaging(_media.Imaging.Provider),
                DescribeFormats(imaging.InputFormats),
                DescribeFormats(imaging.OutputFormats),
                _options.Imaging.MaxEncodedBytes,
                _options.Imaging.MaxWidth,
                _options.Imaging.MaxHeight,
                _options.Imaging.MaxAggregatePixels,
                imaging.MaxFrames,
                _options.Imaging.MaxEstimatedDecodedBytes,
                (int)_options.Imaging.ProcessingDeadline.TotalSeconds,
                _options.Imaging.MaxConcurrentTransforms),
            new UploadCapabilityView(
                maxUploadBytes,
                concurrencyUnlimited ? 0 : limits.MaxConcurrentUploads!.Value,
                concurrencyUnlimited,
                _uploads.MultipartThresholdBytes,
                ProxyUpload: true,
                storage.SupportsDirectUpload,
                storage.SupportsMultipartUpload),
            new SearchCapabilityView(
                _options.Search.Text,
                _options.Search.Facets,
                _options.Search.Timeline,
                _persistence.Provider == VistaraDatabaseProvider.PostgreSql),
            new ApiCapabilityView(
                _options.DefaultPageSize,
                _options.MaxPageSize,
                maxUploadBytes));
    }

    private static string DescribeDatabase(VistaraDatabaseProvider provider) =>
        provider switch
        {
            VistaraDatabaseProvider.PostgreSql => "postgresql",
            VistaraDatabaseProvider.Sqlite => "sqlite",
            _ => "unknown",
        };

    private static string DescribeImaging(MediaImagingProvider? provider) =>
        provider switch
        {
            MediaImagingProvider.NetVips => "net-vips",
            _ => "unknown",
        };

    private static string[] DescribeFormats(
        IReadOnlyList<ImageFormat> formats) =>
        formats
            .Select(format => format.ToString().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
