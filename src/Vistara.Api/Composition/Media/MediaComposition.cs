using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Imaging.NetVips;
using Vistara.Storage.Azure;
using Vistara.Storage.Local;
using Vistara.Storage.S3;

namespace Vistara.Api.Composition.Media;

public enum MediaStorageProvider
{
    Local,
    S3,
    Azure,
}

public enum MediaS3CredentialMode
{
    DefaultChain,
    Static,
}

public enum MediaAzureCredentialMode
{
    DefaultCredential,
    SharedKey,
}

public enum MediaImagingProvider
{
    NetVips,
}

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public MediaStorageOptions Storage { get; set; } = new();

    public MediaImagingOptions Imaging { get; set; } = new();
}

public sealed class MediaStorageOptions
{
    public MediaStorageProvider? Provider { get; set; }

    public MediaLocalOptions Local { get; set; } = new();

    public MediaS3Options S3 { get; set; } = new();

    public MediaAzureOptions Azure { get; set; } = new();
}

public sealed class MediaLocalOptions
{
    public string? RootPath { get; set; }
}

public sealed class MediaS3Options
{
    public S3ProviderKind? Profile { get; set; }

    public MediaS3CredentialMode? CredentialMode { get; set; }

    public string? BucketName { get; set; }

    public string? Region { get; set; }

    public string? ServiceUrl { get; set; }

    public bool ForcePathStyle { get; set; }

    public bool AllowInsecureHttp { get; set; }

    public string[] AllowedEndpointHosts { get; set; } = [];

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public string? SessionToken { get; set; }

    public TimeSpan MaximumPresignLifetime { get; set; } = TimeSpan.FromHours(1);
}

public sealed class MediaAzureOptions
{
    public string? AccountName { get; set; }

    public string? ContainerName { get; set; }

    public string? ServiceUri { get; set; }

    public bool EmulatorMode { get; set; }

    public MediaAzureCredentialMode? CredentialMode { get; set; }

    public string? ConnectionString { get; set; }

    public bool AllowSharedKeySas { get; set; }

    public TimeSpan MaximumGrantLifetime { get; set; } = TimeSpan.FromHours(1);
}

public sealed class MediaImagingOptions
{
    public MediaImagingProvider? Provider { get; set; }
}

public interface IMediaRuntimeDependencies
{
    AWSCredentials CreateS3Credentials(MediaS3Options options);

    TokenCredential CreateAzureCredential();

    IImageProcessor CreateImageProcessor();
}

public static class MediaServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraMedia(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>());
        services.TryAddSingleton<IMediaRuntimeDependencies, DefaultMediaRuntimeDependencies>();

        services.AddSingleton<IBlobStore>(CreateBlobStore);
        services.AddSingleton(
            static provider => provider.GetRequiredService<IBlobStore>().Capabilities);
        services.AddSingleton<IImageProcessor>(
            static provider => provider
                .GetRequiredService<IMediaRuntimeDependencies>()
                .CreateImageProcessor());
        services.AddSingleton(
            static provider => provider.GetRequiredService<IImageProcessor>().Capabilities);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, MediaStartupValidationService>());
        return services;
    }

    private static IBlobStore CreateBlobStore(IServiceProvider provider)
    {
        MediaOptions options = provider.GetRequiredService<IOptions<MediaOptions>>().Value;
        IMediaRuntimeDependencies runtime =
            provider.GetRequiredService<IMediaRuntimeDependencies>();
        return options.Storage.Provider switch
        {
            MediaStorageProvider.Local => new LocalBlobStore(
                new LocalBlobStoreOptions(options.Storage.Local.RootPath!)),
            MediaStorageProvider.S3 => CreateS3Store(options.Storage.S3, runtime),
            MediaStorageProvider.Azure => CreateAzureStore(
                options.Storage.Azure,
                runtime,
                provider.GetService<IAzureBlobClientFactory>()),
            _ => throw new InvalidOperationException(
                "Media storage configuration was not validated."),
        };
    }

    private static S3BlobStore CreateS3Store(
        MediaS3Options options,
        IMediaRuntimeDependencies runtime) =>
        new(ToS3Options(options), runtime.CreateS3Credentials(options));

    private static AzureBlobStore CreateAzureStore(
        MediaAzureOptions options,
        IMediaRuntimeDependencies runtime,
        IAzureBlobClientFactory? clientFactory)
    {
        AzureBlobStoreOptions adapterOptions = ToAzureOptions(options, runtime);
        return clientFactory is null
            ? new AzureBlobStore(adapterOptions)
            : new AzureBlobStore(adapterOptions, clientFactory);
    }

    private static S3BlobStoreOptions ToS3Options(MediaS3Options options) =>
        new(options.Profile!.Value, options.BucketName!, options.Region!)
        {
            ServiceUrl = string.IsNullOrWhiteSpace(options.ServiceUrl)
                ? null
                : new Uri(options.ServiceUrl, UriKind.Absolute),
            ForcePathStyle = options.ForcePathStyle,
            AllowInsecureHttp = options.AllowInsecureHttp,
            AllowedEndpointHosts = options.AllowedEndpointHosts,
            MaximumPresignLifetime = options.MaximumPresignLifetime,
        };

    private static AzureBlobStoreOptions ToAzureOptions(
        MediaAzureOptions options,
        IMediaRuntimeDependencies runtime) =>
        new(
            options.AccountName!,
            options.ContainerName!,
            new Uri(options.ServiceUri!, UriKind.Absolute),
            options.EmulatorMode)
        {
            CredentialMode = options.CredentialMode == MediaAzureCredentialMode.SharedKey
                ? AzureBlobCredentialMode.ConnectionString
                : AzureBlobCredentialMode.TokenCredential,
            TokenCredential = options.CredentialMode ==
                MediaAzureCredentialMode.DefaultCredential
                ? runtime.CreateAzureCredential()
                : null,
            ConnectionString = options.ConnectionString,
            SasMode = options.CredentialMode == MediaAzureCredentialMode.SharedKey
                ? AzureBlobSasMode.SharedKey
                : AzureBlobSasMode.UserDelegation,
            AllowSharedKeySas = options.AllowSharedKeySas,
            MaximumGrantLifetime = options.MaximumGrantLifetime,
        };

    private sealed class MediaStartupValidationService(
        IOptions<MediaOptions> options,
        IBlobStore blobStore,
        IImageProcessor imageProcessor) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = options.Value;
            _ = blobStore.Name;
            _ = blobStore.Capabilities;
            _ = imageProcessor.Capabilities;
            _ = imageProcessor.PipelineFingerprint;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

internal sealed class MediaOptionsValidator : IValidateOptions<MediaOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaOptions options)
    {
        if (options.Storage is null || options.Imaging is null)
        {
            return Invalid("Media storage and imaging configuration is required.");
        }

        if (options.Storage.Provider is null)
        {
            return Invalid("Exactly one media storage provider must be selected.");
        }

        int configuredProviders =
            Convert.ToInt32(IsConfigured(options.Storage.Local)) +
            Convert.ToInt32(IsConfigured(options.Storage.S3)) +
            Convert.ToInt32(IsConfigured(options.Storage.Azure));
        if (configuredProviders != 1 ||
            !SelectedProviderIsConfigured(options.Storage))
        {
            return Invalid(
                "Exactly one media storage provider section must be configured and match the selected provider.");
        }

        if (options.Imaging.Provider != MediaImagingProvider.NetVips)
        {
            return Invalid("The NetVips imaging provider must be selected.");
        }

        return options.Storage.Provider switch
        {
            MediaStorageProvider.Local => ValidateLocal(options.Storage.Local),
            MediaStorageProvider.S3 => ValidateS3(options.Storage.S3),
            MediaStorageProvider.Azure => ValidateAzure(options.Storage.Azure),
            _ => Invalid("The selected media storage provider is unsupported."),
        };
    }

    private static ValidateOptionsResult ValidateLocal(MediaLocalOptions options)
    {
        try
        {
            _ = new LocalBlobStoreOptions(options.RootPath!);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException)
        {
            return Invalid(
                "The local media provider requires an explicit, dedicated absolute root path.");
        }
    }

    private static ValidateOptionsResult ValidateS3(MediaS3Options options)
    {
        if (options.Profile is null)
        {
            return Invalid("The S3 provider profile must be selected explicitly.");
        }

        if (options.CredentialMode is null)
        {
            return Invalid("The S3 credential mode must be selected explicitly.");
        }

        bool hasAccessKey = !string.IsNullOrWhiteSpace(options.AccessKeyId);
        bool hasSecretKey = !string.IsNullOrWhiteSpace(options.SecretAccessKey);
        bool hasSessionToken = !string.IsNullOrWhiteSpace(options.SessionToken);
        if (options.CredentialMode == MediaS3CredentialMode.DefaultChain &&
            (hasAccessKey || hasSecretKey || hasSessionToken))
        {
            return Invalid(
                "S3 static credentials cannot be combined with the default credential chain.");
        }

        if (options.CredentialMode == MediaS3CredentialMode.Static &&
            (!hasAccessKey || !hasSecretKey))
        {
            return Invalid(
                "S3 static credential mode requires both an access key and a secret key.");
        }

        if (!TryCreateAbsoluteUri(options.ServiceUrl, out Uri? serviceUri))
        {
            return Invalid("The S3 service endpoint is invalid.");
        }

        try
        {
            _ = new S3BlobStoreOptions(
                options.Profile.Value,
                options.BucketName!,
                options.Region!)
            {
                ServiceUrl = serviceUri,
                ForcePathStyle = options.ForcePathStyle,
                AllowInsecureHttp = options.AllowInsecureHttp,
                AllowedEndpointHosts = options.AllowedEndpointHosts ?? [],
                MaximumPresignLifetime = options.MaximumPresignLifetime,
            }.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (Exception error) when (
            error is ArgumentException or S3ConfigurationException)
        {
            return Invalid("The S3 media provider configuration is invalid.");
        }
    }

    private static ValidateOptionsResult ValidateAzure(MediaAzureOptions options)
    {
        if (options.CredentialMode is null)
        {
            return Invalid("The Azure credential mode must be selected explicitly.");
        }

        bool hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString);
        if (options.CredentialMode == MediaAzureCredentialMode.DefaultCredential &&
            (hasConnectionString || options.AllowSharedKeySas))
        {
            return Invalid(
                "Azure shared-key settings cannot be combined with default credentials.");
        }

        if (options.CredentialMode == MediaAzureCredentialMode.SharedKey &&
            (!hasConnectionString || !options.AllowSharedKeySas))
        {
            return Invalid(
                "Azure shared-key credentials require an explicit shared-key SAS opt-in.");
        }

        if (!TryCreateAbsoluteUri(options.ServiceUri, out Uri? serviceUri) ||
            serviceUri is null)
        {
            return Invalid("The Azure Blob service endpoint is invalid.");
        }

        try
        {
            var adapterOptions = new AzureBlobStoreOptions(
                options.AccountName!,
                options.ContainerName!,
                serviceUri,
                options.EmulatorMode)
            {
                CredentialMode = options.CredentialMode ==
                    MediaAzureCredentialMode.SharedKey
                    ? AzureBlobCredentialMode.ConnectionString
                    : AzureBlobCredentialMode.TokenCredential,
                ConnectionString = options.ConnectionString,
                SasMode = options.CredentialMode == MediaAzureCredentialMode.SharedKey
                    ? AzureBlobSasMode.SharedKey
                    : AzureBlobSasMode.UserDelegation,
                AllowSharedKeySas = options.AllowSharedKeySas,
                MaximumGrantLifetime = options.MaximumGrantLifetime,
            };
            if (options.CredentialMode == MediaAzureCredentialMode.DefaultCredential)
            {
                adapterOptions = new AzureBlobStoreOptions(
                    options.AccountName!,
                    options.ContainerName!,
                    serviceUri,
                    options.EmulatorMode)
                {
                    CredentialMode = AzureBlobCredentialMode.TokenCredential,
                    TokenCredential = ValidationTokenCredential.Instance,
                    SasMode = AzureBlobSasMode.UserDelegation,
                    MaximumGrantLifetime = options.MaximumGrantLifetime,
                };
            }

            _ = new AzureBlobStore(
                adapterOptions,
                ValidationAzureBlobClientFactory.Instance);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException)
        {
            return Invalid("The Azure media provider configuration is invalid.");
        }
    }

    private static bool SelectedProviderIsConfigured(MediaStorageOptions options) =>
        options.Provider switch
        {
            MediaStorageProvider.Local => IsConfigured(options.Local),
            MediaStorageProvider.S3 => IsConfigured(options.S3),
            MediaStorageProvider.Azure => IsConfigured(options.Azure),
            _ => false,
        };

    private static bool IsConfigured(MediaLocalOptions options) =>
        !string.IsNullOrWhiteSpace(options.RootPath);

    private static bool IsConfigured(MediaS3Options options) =>
        options.Profile is not null ||
        options.CredentialMode is not null ||
        !string.IsNullOrWhiteSpace(options.BucketName) ||
        !string.IsNullOrWhiteSpace(options.Region) ||
        !string.IsNullOrWhiteSpace(options.ServiceUrl) ||
        options.ForcePathStyle ||
        options.AllowInsecureHttp ||
        options.AllowedEndpointHosts?.Length > 0 ||
        !string.IsNullOrWhiteSpace(options.AccessKeyId) ||
        !string.IsNullOrWhiteSpace(options.SecretAccessKey) ||
        !string.IsNullOrWhiteSpace(options.SessionToken);

    private static bool IsConfigured(MediaAzureOptions options) =>
        !string.IsNullOrWhiteSpace(options.AccountName) ||
        !string.IsNullOrWhiteSpace(options.ContainerName) ||
        !string.IsNullOrWhiteSpace(options.ServiceUri) ||
        options.EmulatorMode ||
        options.CredentialMode is not null ||
        !string.IsNullOrWhiteSpace(options.ConnectionString) ||
        options.AllowSharedKeySas;

    private static bool TryCreateAbsoluteUri(string? value, out Uri? uri)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            uri = null;
            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out uri);
    }

    private static ValidateOptionsResult Invalid(string message) =>
        ValidateOptionsResult.Fail(message);

    private sealed class ValidationAzureBlobClientFactory :
        IAzureBlobClientFactory
    {
        public static ValidationAzureBlobClientFactory Instance { get; } = new();

        public IAzureBlobClient CreateWithTokenCredential(
            Uri serviceUri,
            string accountName,
            string containerName,
            TokenCredential credential,
            bool emulatorMode) =>
            ValidationAzureBlobClient.Instance;

        public IAzureBlobClient CreateWithConnectionString(
            string connectionString,
            Uri serviceUri,
            string accountName,
            string containerName,
            bool emulatorMode) =>
            ValidationAzureBlobClient.Instance;
    }

    private sealed class ValidationAzureBlobClient : AzureBlobClientBase
    {
        public static ValidationAzureBlobClient Instance { get; } = new();
    }

    private sealed class ValidationTokenCredential : TokenCredential
    {
        public static ValidationTokenCredential Instance { get; } = new();

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Validation credentials cannot request access tokens.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Validation credentials cannot request access tokens.");
    }
}

internal sealed class DefaultMediaRuntimeDependencies : IMediaRuntimeDependencies
{
    public AWSCredentials CreateS3Credentials(MediaS3Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CredentialMode == MediaS3CredentialMode.Static)
        {
            return string.IsNullOrWhiteSpace(options.SessionToken)
                ? new BasicAWSCredentials(options.AccessKeyId!, options.SecretAccessKey!)
                : new SessionAWSCredentials(
                    options.AccessKeyId!,
                    options.SecretAccessKey!,
                    options.SessionToken);
        }

        try
        {
#pragma warning disable CS0618
            return FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618
        }
        catch (AmazonClientException)
        {
            throw new InvalidOperationException(
                "The S3 default credential chain did not provide credentials.");
        }
    }

    public TokenCredential CreateAzureCredential() => new DefaultAzureCredential();

    public IImageProcessor CreateImageProcessor() => new NetVipsImageProcessor();
}
