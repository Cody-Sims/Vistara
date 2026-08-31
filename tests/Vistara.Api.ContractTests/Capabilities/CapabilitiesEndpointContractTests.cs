using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Api.Features.Capabilities;
using Vistara.Application.Capabilities;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Persistence;
using Vistara.Persistence.Uploads;
using Xunit;

namespace Vistara.Api.ContractTests.Capabilities;

public sealed class CapabilitiesEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    [Fact]
    public void Mapping_registers_one_authenticated_versioned_route()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraCapabilities();
        WebApplication app = builder.Build();

        app.MapVistaraCapabilities();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>());
        Assert.Equal("/api/v1/capabilities", endpoint.RoutePattern.RawText);
        Assert.Contains(
            "GET",
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        IAuthorizeData authorization =
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            CapabilitiesEndpointMapping.PolicyName,
            authorization.Policy);
    }

    [Theory]
    [MemberData(nameof(ProviderCases))]
    public async Task Endpoint_reports_safe_provider_features(
        string provider,
        BlobStoreCapabilities storage,
        bool expectedDirect,
        bool expectedMultipart)
    {
        TestResponse response = await SendAsync(
            provider,
            storage,
            configure: options =>
            {
                options.Imaging.MaxEncodedBytes = 45_000_000;
                options.Imaging.MaxWidth = 18_000;
                options.Imaging.MaxHeight = 17_000;
                options.Imaging.MaxAggregatePixels = 35_000_000;
                options.Imaging.MaxEstimatedDecodedBytes = 400_000_000;
                options.Imaging.ProcessingDeadline = TimeSpan.FromSeconds(25);
                options.Imaging.MaxConcurrentTransforms = 1;
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("postgresql", root.GetProperty("database").GetProperty("provider").GetString());
        Assert.Equal(provider, root.GetProperty("storage").GetProperty("provider").GetString());
        Assert.Equal(
            expectedDirect,
            root.GetProperty("storage").GetProperty("directUpload").GetBoolean());
        Assert.Equal(
            expectedMultipart,
            root.GetProperty("storage").GetProperty("multipartUpload").GetBoolean());
        Assert.True(root.GetProperty("storage").GetProperty("rangeReads").GetBoolean());
        Assert.Equal(
            expectedDirect,
            root.GetProperty("upload").GetProperty("directUpload").GetBoolean());
        Assert.Equal(
            expectedMultipart,
            root.GetProperty("upload").GetProperty("multipartUpload").GetBoolean());
        Assert.Equal(
            3,
            root.GetProperty("upload").GetProperty("maxConcurrentUploads").GetInt64());
    }

    [Fact]
    public async Task Response_is_private_cacheable_stable_and_redacted()
    {
        const string bucket = "private-gallery-bucket";
        const string endpoint = "storage.internal.example";
        const string connectionString =
            "AccountName=secretaccount;AccountKey=secret-key";
        const string filesystemPath = "/srv/vistara/private-media";

        TestResponse first = await SendAsync(
            "azure",
            AzureCapabilities(),
            mediaOptions: new MediaOptions
            {
                Storage = new MediaStorageOptions
                {
                    Provider = MediaStorageProvider.Azure,
                    Local = new MediaLocalOptions { RootPath = filesystemPath },
                    S3 = new MediaS3Options
                    {
                        BucketName = bucket,
                        ServiceUrl = $"https://{endpoint}",
                        AccessKeyId = "access-key",
                        SecretAccessKey = "secret-access-key",
                    },
                    Azure = new MediaAzureOptions
                    {
                        AccountName = "secretaccount",
                        ContainerName = "private-container",
                        ServiceUri = "https://secretaccount.blob.core.windows.net",
                        ConnectionString = connectionString,
                    },
                },
                Imaging = new MediaImagingOptions
                {
                    Provider = MediaImagingProvider.NetVips,
                },
            });
        TestResponse second = await SendAsync("azure", AzureCapabilities());

        Assert.Equal("private, max-age=60", first.Headers.CacheControl.ToString());
        Assert.Contains("Authorization", first.Headers.Vary.ToString(), StringComparison.Ordinal);
        Assert.Contains("Cookie", first.Headers.Vary.ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(first.Headers.ETag));
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(first.Headers.ETag.ToString(), second.Headers.ETag.ToString());
        foreach (string secret in new[]
                 {
                     bucket,
                     endpoint,
                     connectionString,
                     filesystemPath,
                     "private-container",
                     "access-key",
                     "secret-access-key",
                     "secretaccount",
                 })
        {
            Assert.DoesNotContain(secret, first.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Request_cancellation_is_forwarded_to_the_snapshot_provider()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var provider = new CancellationObservingSnapshotProvider();
        var authorization = new FakeCapabilitiesAuthorizationPort();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(
                "local",
                LocalCapabilities(),
                snapshotProvider: provider,
                authorizationPort: authorization,
                cancellationToken: cancellation.Token));

        Assert.True(authorization.CancellationObserved);
        Assert.True(provider.CancellationObserved);
    }

    public static TheoryData<string, BlobStoreCapabilities, bool, bool> ProviderCases =>
        new()
        {
            { "local", LocalCapabilities(), false, false },
            {
                "aws-s3",
                S3Capabilities(),
                true,
                true
            },
            { "azure", AzureCapabilities(), true, true },
        };

    private static BlobStoreCapabilities LocalCapabilities() => new()
    {
        SupportsRangeReads = true,
        SupportsConditionalCreate = true,
        NativeChecksumAlgorithms = [BlobChecksumAlgorithm.Sha256],
        Limits = new BlobStoreLimits(long.MaxValue - 1_048_576, 1_024, 1, 1, 1),
    };

    private static BlobStoreCapabilities S3Capabilities() => new()
    {
        SupportsDirectUpload = true,
        SupportsMultipartUpload = true,
        SupportsRangeReads = true,
        SupportsConditionalCreate = true,
        SupportsConditionalMultipartCompletion = true,
        NativeChecksumAlgorithms = [BlobChecksumAlgorithm.Sha256],
        Limits = new BlobStoreLimits(
            5L * 1024 * 1024 * 1024 * 10_000,
            1_024,
            10_000,
            5L * 1024 * 1024,
            5L * 1024 * 1024 * 1024),
    };

    private static BlobStoreCapabilities AzureCapabilities() => new()
    {
        SupportsDirectUpload = true,
        SupportsMultipartUpload = true,
        SupportsRangeReads = true,
        SupportsConditionalCreate = true,
        SupportsConditionalMultipartCompletion = true,
        NativeChecksumAlgorithms =
        [
            BlobChecksumAlgorithm.Md5,
            BlobChecksumAlgorithm.Sha256,
        ],
        Limits = new BlobStoreLimits(
            4_000_000L * 50_000,
            1_024,
            50_000,
            1,
            4_000_000_000),
    };

    private static async Task<TestResponse> SendAsync(
        string provider,
        BlobStoreCapabilities storage,
        Action<CapabilitiesSurfaceOptions>? configure = null,
        MediaOptions? mediaOptions = null,
        ICapabilitySnapshotProvider? snapshotProvider = null,
        ICapabilitiesAuthorizationPort? authorizationPort = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IBlobStore>(
            new FakeBlobStore(provider, storage));
        builder.Services.AddSingleton<IImageProcessor>(new FakeImageProcessor());
        builder.Services.AddSingleton(new VistaraPersistenceOptions
        {
            Provider = VistaraDatabaseProvider.PostgreSql,
            ConnectionString = "Host=database.internal;Password=do-not-return",
        });
        builder.Services.AddSingleton(new UploadPersistenceOptions
        {
            MaximumUploadBytes = 50_000_000,
            MultipartThresholdBytes = 16_000_000,
            StorageContainer = "internal-media-container",
        });
        builder.Services.AddSingleton<IOptions<MediaOptions>>(
            Options.Create(mediaOptions ?? new MediaOptions
            {
                Storage = new MediaStorageOptions
                {
                    Provider = provider == "local"
                        ? MediaStorageProvider.Local
                        : provider == "azure"
                            ? MediaStorageProvider.Azure
                            : MediaStorageProvider.S3,
                },
                Imaging = new MediaImagingOptions
                {
                    Provider = MediaImagingProvider.NetVips,
                },
            }));
        builder.Services.AddSingleton<ITenantCapabilitySource>(
            new FakeTenantCapabilitySource());
        builder.Services.AddSingleton<ICapabilitiesAuthorizationPort>(
            authorizationPort ?? new FakeCapabilitiesAuthorizationPort());
        if (snapshotProvider is not null)
        {
            builder.Services.AddSingleton(snapshotProvider);
        }

        builder.Services.AddVistaraCapabilities(configure);
        WebApplication app = builder.Build();
        app.MapVistaraCapabilities();
        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>());
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim("tenant_id", TenantId.ToString("D"))],
                    "test")),
        };
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers,
            body);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        IHeaderDictionary Headers,
        string Body);

    private sealed class FakeTenantCapabilitySource : ITenantCapabilitySource
    {
        public ValueTask<TenantCapabilityLimits> GetAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TenantId, tenantId);
            return ValueTask.FromResult(
                new TenantCapabilityLimits(48_000_000, 3));
        }
    }

    private sealed class FakeCapabilitiesAuthorizationPort :
        ICapabilitiesAuthorizationPort
    {
        public bool CancellationObserved { get; private set; }

        public ValueTask<CapabilitiesAccess> AuthorizeAsync(
            HttpContext context,
            CancellationToken cancellationToken)
        {
            CancellationObserved = cancellationToken.IsCancellationRequested;
            return ValueTask.FromResult(CapabilitiesAccess.Authorized(TenantId));
        }
    }

    private sealed class CancellationObservingSnapshotProvider :
        ICapabilitySnapshotProvider
    {
        public bool CancellationObserved { get; private set; }

        public ValueTask<CapabilitySnapshot> GetAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            CancellationObserved = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }

    private sealed class FakeBlobStore(
        string name,
        BlobStoreCapabilities capabilities) : IBlobStore
    {
        public string Name { get; } = name;
        public BlobStoreCapabilities Capabilities { get; } = capabilities;

        public ValueTask<BlobHead?> HeadAsync(
            BlobKey key,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobReadHandle> OpenReadAsync(
            BlobKey key,
            BlobReadOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobWriteResult> PutAsync(
            BlobKey key,
            IReplayableBlobContent content,
            BlobWriteOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobCopyResult> CopyAsync(
            BlobKey source,
            BlobKey destination,
            BlobCopyOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<BlobDeleteResult> DeleteAsync(
            BlobKey key,
            BlobDeleteOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<BlobHead> ListAsync(
            BlobListOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
            DirectUploadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MultipartSession> BeginMultipartAsync(
            MultipartRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
            MultipartSession session,
            int partNumber,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<MultipartCompletion> CompleteMultipartAsync(
            MultipartSession session,
            IReadOnlyList<UploadedPart> parts,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask AbortMultipartAsync(
            MultipartSession session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
            BlobKey key,
            ReadGrantOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeImageProcessor : IImageProcessor
    {
        public ImageProcessorCapabilities Capabilities { get; } = new()
        {
            InputFormats = [ImageFormat.WebP, ImageFormat.Jpeg, ImageFormat.Png],
            OutputFormats = [ImageFormat.WebP, ImageFormat.Jpeg, ImageFormat.Png],
            MaxFrames = 1,
            SupportsAutoOrientation = true,
            SupportsColorProfileNormalization = true,
            SupportsSensitiveMetadataStripping = true,
        };

        public ImagePipelineFingerprint PipelineFingerprint =>
            new("test-pipeline");

        public ValueTask<ImageInspection> InspectAsync(
            IReplayableImageSource source,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ImageTransformResult> TransformAsync(
            IReplayableImageSource source,
            Stream destination,
            CanonicalTransformRecipe recipe,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
