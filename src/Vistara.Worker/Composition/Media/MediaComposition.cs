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

namespace Vistara.Worker.Composition.Media;

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
    /// <summary>
    /// Ambient developer credentials. Local and development compatibility only;
    /// a deployed environment must review and opt in explicitly.
    /// </summary>
    DefaultCredential,

    /// <summary>
    /// Connection-string fallback for deployments that cannot use Entra
    /// identities.
    /// </summary>
    SharedKey,

    /// <summary>
    /// A user-assigned managed identity addressed by its client ID. This is the
    /// only credential mode that is trusted without review in a deployed
    /// environment.
    /// </summary>
    ManagedIdentity,
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

    public override string ToString() =>
        $"S3 {{ Profile = {Profile}, BucketName = {BucketName}, Region = {Region}, " +
        $"ServiceUrl = {ServiceUrl}, ForcePathStyle = {ForcePathStyle}, " +
        $"AllowInsecureHttp = {AllowInsecureHttp}, " +
        $"CredentialMode = {CredentialMode}, " +
        $"StaticCredentials = {(string.IsNullOrWhiteSpace(AccessKeyId) ? "none" : MediaAzureOptions.RedactedPlaceholder)} }}";
}

public sealed class MediaAzureOptions
{
    internal const string RedactedPlaceholder = "[redacted]";

    public string? AccountName { get; set; }

    public string? ContainerName { get; set; }

    public string? ServiceUri { get; set; }

    public bool EmulatorMode { get; set; }

    public MediaAzureCredentialMode? CredentialMode { get; set; }

    /// <summary>
    /// The client ID of the user-assigned managed identity that
    /// <see cref="MediaAzureCredentialMode.ManagedIdentity"/> binds to. A
    /// system-assigned or otherwise implicit identity is never inferred, so an
    /// unexpected identity on the host cannot silently gain blob access.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// The reviewed deployment opt-in that keeps
    /// <see cref="MediaAzureCredentialMode.DefaultCredential"/> usable outside
    /// local development. It applies to no other credential mode.
    /// </summary>
    public bool AllowDefaultCredentialOutsideDevelopment { get; set; }

    public string? ConnectionString { get; set; }

    public bool AllowSharedKeySas { get; set; }

    public TimeSpan MaximumGrantLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Decides whether a value identifies a user-assigned managed identity.
    /// Only a non-empty hyphenated GUID is accepted, so a braced, numeric, or
    /// padded value fails closed instead of reaching the Azure identity
    /// endpoint as an unintended identity.
    /// </summary>
    public static bool IsUserAssignedClientId(string? value) =>
        value is not null &&
        value.Length == 36 &&
        Guid.TryParseExact(value, "D", out Guid clientId) &&
        clientId != Guid.Empty;

    /// <summary>
    /// Describes the configuration for startup diagnostics. Credential material
    /// is replaced with a placeholder so a log or a validation failure cannot
    /// disclose an identity or an account key.
    /// </summary>
    public override string ToString() =>
        $"Azure {{ AccountName = {AccountName}, ContainerName = {ContainerName}, " +
        $"ServiceUri = {ServiceUri}, EmulatorMode = {EmulatorMode}, " +
        $"CredentialMode = {CredentialMode}, " +
        $"ManagedIdentityClientId = {Describe(ManagedIdentityClientId)}, " +
        $"AllowDefaultCredentialOutsideDevelopment = " +
        $"{AllowDefaultCredentialOutsideDevelopment}, " +
        $"ConnectionString = {Describe(ConnectionString)}, " +
        $"AllowSharedKeySas = {AllowSharedKeySas} }}";

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : RedactedPlaceholder;
}

public sealed class MediaImagingOptions
{
    public MediaImagingProvider? Provider { get; set; }
}

public interface IMediaRuntimeDependencies
{
    AWSCredentials CreateS3Credentials(MediaS3Options options);

    /// <summary>
    /// Creates the ambient developer credential. Retained for local and
    /// development composition only.
    /// </summary>
    TokenCredential CreateAzureCredential();

    /// <summary>
    /// Creates the credential for the validated Azure credential mode. An
    /// implementation that does not override this method only supplies the
    /// development credential, so every other mode fails instead of silently
    /// downgrading a managed-identity deployment to a developer credential
    /// chain.
    /// </summary>
    TokenCredential CreateAzureCredential(MediaAzureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CredentialMode != MediaAzureCredentialMode.DefaultCredential)
        {
            throw new NotSupportedException(
                $"Azure credential mode '{options.CredentialMode}' requires a media runtime dependency that selects the credential from the configured mode.");
        }

        return CreateAzureCredential();
    }

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
            TokenCredential = UsesTokenCredential(options.CredentialMode)
                ? runtime.CreateAzureCredential(options)
                : null,
            ConnectionString = options.ConnectionString,
            SasMode = options.CredentialMode == MediaAzureCredentialMode.SharedKey
                ? AzureBlobSasMode.SharedKey
                : AzureBlobSasMode.UserDelegation,
            AllowSharedKeySas = options.AllowSharedKeySas,
            MaximumGrantLifetime = options.MaximumGrantLifetime,
        };

    internal static bool UsesTokenCredential(MediaAzureCredentialMode? mode) =>
        mode is MediaAzureCredentialMode.DefaultCredential or
            MediaAzureCredentialMode.ManagedIdentity;

    private sealed class MediaStartupValidationService(
        IOptions<MediaOptions> options,
        IBlobStore blobStore,
        IImageProcessor imageProcessor,
        ILoggerFactory? loggerFactory = null) : IHostedService
    {
        private static readonly Action<ILogger, string, string, Exception?> LogComposition =
            LoggerMessage.Define<string, string>(
                LogLevel.Information,
                new EventId(1, "MediaCompositionReady"),
                "Media composition ready. Storage provider {StorageProvider}; {StorageConfiguration}");

        public Task StartAsync(CancellationToken cancellationToken)
        {
            MediaOptions value = options.Value;
            _ = blobStore.Name;
            _ = blobStore.Capabilities;
            _ = imageProcessor.Capabilities;
            _ = imageProcessor.PipelineFingerprint;
            if (loggerFactory is not null)
            {
                LogComposition(
                    loggerFactory.CreateLogger("Vistara.Media.Composition"),
                    value.Storage.Provider?.ToString() ?? "none",
                    Describe(value.Storage),
                    null);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        private static string Describe(MediaStorageOptions storage) =>
            storage.Provider switch
            {
                MediaStorageProvider.Azure => storage.Azure.ToString(),
                MediaStorageProvider.S3 => storage.S3.ToString(),
                _ => "Local { RootPath = configured }",
            };
    }
}

internal sealed class MediaOptionsValidator(IHostEnvironment? environment = null) :
    IValidateOptions<MediaOptions>
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

    private ValidateOptionsResult ValidateAzure(MediaAzureOptions options)
    {
        if (options.CredentialMode is null)
        {
            return Invalid("The Azure credential mode must be selected explicitly.");
        }

        ValidateOptionsResult credentialResult = ValidateAzureCredentialMode(options);
        if (credentialResult != ValidateOptionsResult.Success)
        {
            return credentialResult;
        }

        if (!TryCreateAbsoluteUri(options.ServiceUri, out Uri? serviceUri) ||
            serviceUri is null)
        {
            return Invalid("The Azure Blob service endpoint is invalid.");
        }

        bool ambientCredential =
            MediaServiceCollectionExtensions.UsesTokenCredential(options.CredentialMode);
        if (ambientCredential &&
            !options.EmulatorMode &&
            !AzureBlobStoreOptions.IsTrustedBlobEndpoint(options.AccountName, serviceUri))
        {
            return Invalid(
                "Azure identity credentials are limited to a first-party Azure Blob endpoint for the configured account.");
        }

        try
        {
            var adapterOptions = new AzureBlobStoreOptions(
                options.AccountName!,
                options.ContainerName!,
                serviceUri,
                options.EmulatorMode)
            {
                CredentialMode = ambientCredential
                    ? AzureBlobCredentialMode.TokenCredential
                    : AzureBlobCredentialMode.ConnectionString,
                TokenCredential = ambientCredential
                    ? ValidationTokenCredential.Instance
                    : null,
                ConnectionString = ambientCredential ? null : options.ConnectionString,
                SasMode = ambientCredential
                    ? AzureBlobSasMode.UserDelegation
                    : AzureBlobSasMode.SharedKey,
                AllowSharedKeySas = !ambientCredential && options.AllowSharedKeySas,
                MaximumGrantLifetime = options.MaximumGrantLifetime,
            };

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

    private ValidateOptionsResult ValidateAzureCredentialMode(MediaAzureOptions options)
    {
        bool hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString);
        bool hasClientId = !string.IsNullOrWhiteSpace(options.ManagedIdentityClientId);
        switch (options.CredentialMode)
        {
            case MediaAzureCredentialMode.ManagedIdentity:
                if (!hasClientId)
                {
                    return Invalid(
                        "Azure managed-identity mode requires an explicit user-assigned client ID.");
                }

                if (!MediaAzureOptions.IsUserAssignedClientId(
                        options.ManagedIdentityClientId))
                {
                    return Invalid(
                        "The Azure user-assigned managed identity client ID must be a non-empty hyphenated GUID.");
                }

                if (hasConnectionString || options.AllowSharedKeySas)
                {
                    return Invalid(
                        "Azure shared-key settings cannot be combined with managed-identity credentials.");
                }

                break;
            case MediaAzureCredentialMode.DefaultCredential:
                if (hasConnectionString || options.AllowSharedKeySas)
                {
                    return Invalid(
                        "Azure shared-key settings cannot be combined with default credentials.");
                }

                if (hasClientId)
                {
                    return Invalid(
                        "An Azure user-assigned client ID requires the managed-identity credential mode.");
                }

                if (RequiresReviewedDefaultCredential() &&
                    !options.AllowDefaultCredentialOutsideDevelopment)
                {
                    return Invalid(
                        "Azure default credentials are limited to local development unless the deployment reviews and allows them explicitly.");
                }

                break;
            case MediaAzureCredentialMode.SharedKey:
                if (!hasConnectionString || !options.AllowSharedKeySas)
                {
                    return Invalid(
                        "Azure shared-key credentials require an explicit shared-key SAS opt-in.");
                }

                if (hasClientId)
                {
                    return Invalid(
                        "An Azure user-assigned client ID requires the managed-identity credential mode.");
                }

                break;
            default:
                return Invalid("The selected Azure credential mode is unsupported.");
        }

        if (options.AllowDefaultCredentialOutsideDevelopment &&
            options.CredentialMode != MediaAzureCredentialMode.DefaultCredential)
        {
            return Invalid(
                "The reviewed default-credential opt-in applies only to the default credential mode.");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Decides whether the environment must review an ambient developer
    /// credential before it is used. Only an explicit development environment
    /// is exempt: an unnamed, absent, or custom environment such as
    /// <c>Test</c>, <c>QA</c>, or <c>Preview</c> cannot prove it is local, so
    /// it fails closed and reaches a developer credential chain only through
    /// the reviewed opt-in.
    /// </summary>
    private bool RequiresReviewedDefaultCredential() =>
        environment is null || !environment.IsDevelopment();

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
        !string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) ||
        options.AllowDefaultCredentialOutsideDevelopment ||
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

/// <summary>
/// Creates the real provider credentials and image processor for a composition
/// root. Azure credentials are cached, so one composition shares a single
/// credential instance and its token cache instead of re-authenticating per
/// client.
/// </summary>
public sealed class DefaultMediaRuntimeDependencies : IMediaRuntimeDependencies
{
    private readonly Lock gate = new();
    private TokenCredential? developerCredential;
    private TokenCredential? managedIdentityCredential;
    private string? managedIdentityClientId;

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

    public TokenCredential CreateAzureCredential()
    {
        lock (gate)
        {
            return developerCredential ??= new DefaultAzureCredential();
        }
    }

    public TokenCredential CreateAzureCredential(MediaAzureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.CredentialMode switch
        {
            MediaAzureCredentialMode.ManagedIdentity =>
                CreateManagedIdentityCredential(options.ManagedIdentityClientId),
            MediaAzureCredentialMode.DefaultCredential => CreateAzureCredential(),
            _ => throw new InvalidOperationException(
                "The configured Azure credential mode does not use a token credential."),
        };
    }

    public IImageProcessor CreateImageProcessor() => new NetVipsImageProcessor();

    /// <summary>
    /// Builds the user-assigned managed identity credential directly. Nothing
    /// chains to a developer tool credential such as the Azure CLI, so a host
    /// that cannot reach its identity endpoint fails instead of borrowing an
    /// operator identity.
    /// </summary>
    private TokenCredential CreateManagedIdentityCredential(string? clientId)
    {
        if (!MediaAzureOptions.IsUserAssignedClientId(clientId))
        {
            throw new InvalidOperationException(
                "Azure managed-identity mode requires a user-assigned client ID in hyphenated GUID form.");
        }

        lock (gate)
        {
            if (managedIdentityCredential is null ||
                !string.Equals(managedIdentityClientId, clientId, StringComparison.Ordinal))
            {
                managedIdentityCredential = new ManagedIdentityCredential(
                    ManagedIdentityId.FromUserAssignedClientId(clientId!));
                managedIdentityClientId = clientId;
            }

            return managedIdentityCredential;
        }
    }
}
