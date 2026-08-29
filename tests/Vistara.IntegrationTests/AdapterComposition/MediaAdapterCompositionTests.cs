using Amazon.Runtime;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Storage.Azure;
using Vistara.Storage.Local;
using Vistara.Storage.S3;
using Xunit;
using ApiMedia = Vistara.Api.Composition.Media;
using WorkerMedia = Vistara.Worker.Composition.Media;

namespace Vistara.IntegrationTests.AdapterComposition;

public sealed class MediaAdapterCompositionTests
{
    public static TheoryData<CompositionRole> Roles => new()
    {
        CompositionRole.Api,
        CompositionRole.Worker,
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Local_registration_binds_options_and_publishes_adapter_capabilities(
        CompositionRole role)
    {
        string root = CreateScratchPath();
        try
        {
            using IHost host = BuildHost(
                role,
                LocalSettings(root),
                new FakeRuntimeDependencies());

            await host.StartAsync();

            IBlobStore store = host.Services.GetRequiredService<IBlobStore>();
            Assert.IsType<LocalBlobStore>(store);
            Assert.Same(store, host.Services.GetRequiredService<IBlobStore>());
            Assert.Same(
                store.Capabilities,
                host.Services.GetRequiredService<BlobStoreCapabilities>());
            Assert.Same(
                host.Services.GetRequiredService<IImageProcessor>().Capabilities,
                host.Services.GetRequiredService<ImageProcessorCapabilities>());
            Assert.False(store.Capabilities.SupportsDirectUpload);
            Assert.IsType<FakeImageProcessor>(
                host.Services.GetRequiredService<IImageProcessor>());
            AssertBoundLocalRoot(role, host.Services, root);
        }
        finally
        {
            DeleteScratchPath(root);
        }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task S3_registration_uses_explicit_profile_and_truthful_capabilities(
        CompositionRole role)
    {
        using IHost host = BuildHost(
            role,
            S3Settings("BackblazeB2"),
            new FakeRuntimeDependencies());

        await host.StartAsync();

        IBlobStore store = host.Services.GetRequiredService<IBlobStore>();
        Assert.IsType<S3BlobStore>(store);
        Assert.Equal("backblaze-b2", store.Name);
        Assert.False(store.Capabilities.SupportsConditionalCreate);
        Assert.Equal(
            BlobConsistencyModel.Eventual,
            store.Capabilities.ListAfterWriteConsistency);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Azure_registration_prefers_default_credentials_and_uses_client_seam(
        CompositionRole role)
    {
        var runtime = new FakeRuntimeDependencies();
        var azureFactory = new FakeAzureBlobClientFactory();
        using IHost host = BuildHost(
            role,
            AzureSettings(),
            runtime,
            azureFactory);

        await host.StartAsync();

        IBlobStore store = host.Services.GetRequiredService<IBlobStore>();
        Assert.IsType<AzureBlobStore>(store);
        Assert.Equal("azure", store.Name);
        Assert.True(store.Capabilities.SupportsConditionalMultipartCompletion);
        Assert.Equal(1, runtime.AzureCredentialRequests);
        Assert.Equal(1, azureFactory.TokenCredentialCreations);
        Assert.Equal(0, azureFactory.ConnectionStringCreations);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Startup_rejects_missing_or_ambiguous_provider_configuration(
        CompositionRole role)
    {
        Dictionary<string, string?> missingProvider = BaseSettings();
        await AssertInvalidOptionsAsync(role, missingProvider);

        Dictionary<string, string?> missingImaging = LocalSettings(CreateScratchPath());
        missingImaging.Remove("Media:Imaging:Provider");
        try
        {
            await AssertInvalidOptionsAsync(role, missingImaging);
        }
        finally
        {
            DeleteScratchPath(missingImaging["Media:Storage:Local:RootPath"]!);
        }

        Dictionary<string, string?> multipleProviders = LocalSettings(CreateScratchPath());
        multipleProviders["Media:Storage:S3:Profile"] = "Aws";
        multipleProviders["Media:Storage:S3:CredentialMode"] = "DefaultChain";
        multipleProviders["Media:Storage:S3:BucketName"] = "vistara-media";
        multipleProviders["Media:Storage:S3:Region"] = "us-east-1";
        try
        {
            await AssertInvalidOptionsAsync(role, multipleProviders);
        }
        finally
        {
            DeleteScratchPath(multipleProviders["Media:Storage:Local:RootPath"]!);
        }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Startup_rejects_missing_roots_profiles_containers_and_credentials(
        CompositionRole role)
    {
        Dictionary<string, string?> missingRoot = BaseSettings();
        missingRoot["Media:Storage:Provider"] = "Local";
        missingRoot["Media:Storage:Local:RootPath"] = "";
        await AssertInvalidOptionsAsync(role, missingRoot);

        Dictionary<string, string?> missingProfile = S3Settings("Aws");
        missingProfile.Remove("Media:Storage:S3:Profile");
        await AssertInvalidOptionsAsync(role, missingProfile);

        Dictionary<string, string?> missingCredentials = S3Settings("Aws");
        missingCredentials["Media:Storage:S3:CredentialMode"] = "Static";
        await AssertInvalidOptionsAsync(role, missingCredentials);

        Dictionary<string, string?> missingContainer = AzureSettings();
        missingContainer["Media:Storage:Azure:ContainerName"] = "";
        await AssertInvalidOptionsAsync(role, missingContainer);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Startup_rejects_ambiguous_credentials_without_disclosing_values(
        CompositionRole role)
    {
        const string secret = "do-not-print-this-secret";
        Dictionary<string, string?> settings = S3Settings("Aws");
        settings["Media:Storage:S3:AccessKeyId"] = "access-key";
        settings["Media:Storage:S3:SecretAccessKey"] = secret;

        OptionsValidationException error =
            await Assert.ThrowsAsync<OptionsValidationException>(
                async () => await StartHostAsync(
                    role,
                    settings,
                    new FakeRuntimeDependencies()));

        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Startup_rejects_insecure_cloud_endpoints(
        CompositionRole role)
    {
        Dictionary<string, string?> s3 = S3Settings("CloudflareR2");
        s3["Media:Storage:S3:ServiceUrl"] =
            "http://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com";
        await AssertInvalidOptionsAsync(role, s3);

        Dictionary<string, string?> azure = AzureSettings();
        azure["Media:Storage:Azure:ServiceUri"] =
            "http://vistaratest.blob.core.windows.net";
        await AssertInvalidOptionsAsync(role, azure);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Shared_key_azure_credentials_require_explicit_opt_in(
        CompositionRole role)
    {
        Dictionary<string, string?> settings = AzureSettings();
        settings["Media:Storage:Azure:CredentialMode"] = "SharedKey";
        settings["Media:Storage:Azure:ConnectionString"] =
            "DefaultEndpointsProtocol=https;AccountName=vistaratest;AccountKey=secret";

        await AssertInvalidOptionsAsync(role, settings);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Shared_key_azure_credentials_are_used_only_after_explicit_opt_in(
        CompositionRole role)
    {
        Dictionary<string, string?> settings = AzureSettings();
        settings["Media:Storage:Azure:CredentialMode"] = "SharedKey";
        settings["Media:Storage:Azure:ConnectionString"] =
            "DefaultEndpointsProtocol=https;AccountName=vistaratest;AccountKey=secret";
        settings["Media:Storage:Azure:AllowSharedKeySas"] = "true";
        var runtime = new FakeRuntimeDependencies();
        var azureFactory = new FakeAzureBlobClientFactory();
        using IHost host = BuildHost(role, settings, runtime, azureFactory);

        await host.StartAsync();

        Assert.Equal(0, runtime.AzureCredentialRequests);
        Assert.Equal(0, azureFactory.TokenCredentialCreations);
        Assert.Equal(1, azureFactory.ConnectionStringCreations);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Startup_fails_when_required_native_codecs_are_unavailable(
        CompositionRole role)
    {
        var runtime = new FakeRuntimeDependencies
        {
            ImageProcessorError = new ImageProcessorException(
                ImageProcessorErrorCode.Unsupported,
                "Required native codecs are unavailable."),
        };
        string root = CreateScratchPath();
        try
        {
            ImageProcessorException error =
                await Assert.ThrowsAsync<ImageProcessorException>(
                    async () => await StartHostAsync(
                        role,
                        LocalSettings(root),
                        runtime));

            Assert.Equal(ImageProcessorErrorCode.Unsupported, error.Code);
        }
        finally
        {
            DeleteScratchPath(root);
        }
    }

    private static async Task AssertInvalidOptionsAsync(
        CompositionRole role,
        IReadOnlyDictionary<string, string?> settings)
    {
        await Assert.ThrowsAsync<OptionsValidationException>(
            async () => await StartHostAsync(
                role,
                settings,
                new FakeRuntimeDependencies()));
    }

    private static async Task StartHostAsync(
        CompositionRole role,
        IReadOnlyDictionary<string, string?> settings,
        FakeRuntimeDependencies runtime)
    {
        using IHost host = BuildHost(role, settings, runtime);
        await host.StartAsync();
    }

    private static IHost BuildHost(
        CompositionRole role,
        IReadOnlyDictionary<string, string?> settings,
        FakeRuntimeDependencies runtime,
        IAzureBlobClientFactory? azureFactory = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
            });
        builder.Configuration.AddInMemoryCollection(settings);
        if (azureFactory is not null)
        {
            builder.Services.AddSingleton(azureFactory);
        }

        switch (role)
        {
            case CompositionRole.Api:
                builder.Services.AddSingleton<ApiMedia.IMediaRuntimeDependencies>(runtime);
                ApiMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
                    builder.Services,
                    builder.Configuration);
                break;
            case CompositionRole.Worker:
                builder.Services.AddSingleton<WorkerMedia.IMediaRuntimeDependencies>(runtime);
                WorkerMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
                    builder.Services,
                    builder.Configuration);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role));
        }

        return builder.Build();
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Media:Imaging:Provider"] = "NetVips",
    };

    private static Dictionary<string, string?> LocalSettings(string root)
    {
        Dictionary<string, string?> settings = BaseSettings();
        settings["Media:Storage:Provider"] = "Local";
        settings["Media:Storage:Local:RootPath"] = root;
        return settings;
    }

    private static Dictionary<string, string?> S3Settings(string profile)
    {
        Dictionary<string, string?> settings = BaseSettings();
        settings["Media:Storage:Provider"] = "S3";
        settings["Media:Storage:S3:Profile"] = profile;
        settings["Media:Storage:S3:CredentialMode"] = "DefaultChain";
        settings["Media:Storage:S3:BucketName"] = "vistara-media";
        settings["Media:Storage:S3:Region"] =
            profile == "CloudflareR2" ? "auto" : "us-east-1";
        if (profile == "CloudflareR2")
        {
            settings["Media:Storage:S3:ServiceUrl"] =
                "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com";
        }
        else if (profile == "BackblazeB2")
        {
            settings["Media:Storage:S3:ServiceUrl"] =
                "https://s3.us-east-1.backblazeb2.com";
        }

        return settings;
    }

    private static Dictionary<string, string?> AzureSettings()
    {
        Dictionary<string, string?> settings = BaseSettings();
        settings["Media:Storage:Provider"] = "Azure";
        settings["Media:Storage:Azure:AccountName"] = "vistaratest";
        settings["Media:Storage:Azure:ContainerName"] = "media";
        settings["Media:Storage:Azure:ServiceUri"] =
            "https://vistaratest.blob.core.windows.net";
        settings["Media:Storage:Azure:CredentialMode"] = "DefaultCredential";
        return settings;
    }

    private static string CreateScratchPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            $"adapter-composition-{Guid.NewGuid():N}");

    private static void DeleteScratchPath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void AssertBoundLocalRoot(
        CompositionRole role,
        IServiceProvider services,
        string expectedRoot)
    {
        string actualRoot = role switch
        {
            CompositionRole.Api => services
                .GetRequiredService<IOptions<ApiMedia.MediaOptions>>()
                .Value.Storage.Local.RootPath!,
            CompositionRole.Worker => services
                .GetRequiredService<IOptions<WorkerMedia.MediaOptions>>()
                .Value.Storage.Local.RootPath!,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        Assert.Equal(expectedRoot, actualRoot);
    }

    public enum CompositionRole
    {
        Api,
        Worker,
    }

    private sealed class FakeRuntimeDependencies :
        ApiMedia.IMediaRuntimeDependencies,
        WorkerMedia.IMediaRuntimeDependencies
    {
        public ImageProcessorException? ImageProcessorError { get; init; }

        public int AzureCredentialRequests { get; private set; }

        public AWSCredentials CreateS3Credentials(
            ApiMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public AWSCredentials CreateS3Credentials(
            WorkerMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public TokenCredential CreateAzureCredential()
        {
            AzureCredentialRequests++;
            return new FakeTokenCredential();
        }

        public IImageProcessor CreateImageProcessor()
        {
            if (ImageProcessorError is not null)
            {
                throw ImageProcessorError;
            }

            return new FakeImageProcessor();
        }
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cloud credentials must not be requested.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cloud credentials must not be requested.");
    }

    private sealed class FakeAzureBlobClientFactory : IAzureBlobClientFactory
    {
        public int TokenCredentialCreations { get; private set; }

        public int ConnectionStringCreations { get; private set; }

        public IAzureBlobClient CreateWithTokenCredential(
            Uri serviceUri,
            string accountName,
            string containerName,
            TokenCredential credential,
            bool emulatorMode)
        {
            TokenCredentialCreations++;
            return new FakeAzureBlobClient(serviceUri, containerName);
        }

        public IAzureBlobClient CreateWithConnectionString(
            string connectionString,
            Uri serviceUri,
            string accountName,
            string containerName,
            bool emulatorMode)
        {
            ConnectionStringCreations++;
            return new FakeAzureBlobClient(serviceUri, containerName);
        }
    }

    private sealed class FakeAzureBlobClient(
        Uri serviceUri,
        string containerName) : AzureBlobClientBase
    {
        public override Uri GetBlobUri(string key) =>
            new(serviceUri, $"{containerName}/{key}");
    }

    private sealed class FakeImageProcessor : IImageProcessor
    {
        public ImageProcessorCapabilities Capabilities { get; } = new()
        {
            InputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
            OutputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
            MaxFrames = 1,
            SupportsAutoOrientation = true,
            SupportsColorProfileNormalization = true,
            SupportsSensitiveMetadataStripping = true,
        };

        public ImagePipelineFingerprint PipelineFingerprint { get; } =
            new("adapter-composition-test");

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
