using System.Collections.Concurrent;
using Amazon.Runtime;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private const string UserAssignedClientId = "8ec1a4d5-42d1-4d84-9d2a-9a9a2f3f9a11";

    private const string OtherUserAssignedClientId =
        "1b0d6a1e-9f5e-4a7d-8a2c-42f0c9d3b7a4";

    private const string SharedKeyAccountKey = "do-not-print-this-account-key";

    private const string SharedKeyConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=vistaratest;AccountKey=" +
        SharedKeyAccountKey;

    private const string AzureServiceUri = "https://vistaratest.blob.core.windows.net";

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
    public async Task Azure_registration_prefers_user_assigned_managed_identity(
        CompositionRole role)
    {
        var runtime = new FakeRuntimeDependencies();
        var azureFactory = new FakeAzureBlobClientFactory();
        using IHost host = BuildHost(
            role,
            AzureSettings(),
            runtime,
            azureFactory,
            Environments.Production);

        await host.StartAsync();

        IBlobStore store = host.Services.GetRequiredService<IBlobStore>();
        Assert.IsType<AzureBlobStore>(store);
        Assert.Equal("azure", store.Name);
        Assert.True(store.Capabilities.SupportsConditionalMultipartCompletion);
        Assert.Equal(1, runtime.AzureCredentialRequests);
        Assert.Equal(0, runtime.AmbientAzureCredentialRequests);
        Assert.Equal("ManagedIdentity", runtime.LastAzureCredentialMode);
        Assert.Equal(UserAssignedClientId, runtime.LastManagedIdentityClientId);
        Assert.Equal(1, azureFactory.TokenCredentialCreations);
        Assert.Equal(0, azureFactory.ConnectionStringCreations);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Azure_default_credentials_remain_available_for_local_development(
        CompositionRole role)
    {
        var runtime = new FakeRuntimeDependencies();
        var azureFactory = new FakeAzureBlobClientFactory();
        using IHost host = BuildHost(
            role,
            AzureDefaultCredentialSettings(),
            runtime,
            azureFactory,
            Environments.Development);

        await host.StartAsync();

        Assert.IsType<AzureBlobStore>(host.Services.GetRequiredService<IBlobStore>());
        Assert.Equal(1, runtime.AzureCredentialRequests);
        Assert.Equal("DefaultCredential", runtime.LastAzureCredentialMode);
        Assert.Null(runtime.LastManagedIdentityClientId);
        Assert.Equal(1, azureFactory.TokenCredentialCreations);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Azure_default_credentials_require_a_reviewed_deployment_opt_in(
        CompositionRole role)
    {
        Dictionary<string, string?> settings = AzureDefaultCredentialSettings();
        settings["Media:Storage:Azure:AllowDefaultCredentialOutsideDevelopment"] = "true";
        var runtime = new FakeRuntimeDependencies();
        using IHost host = BuildHost(
            role,
            settings,
            runtime,
            new FakeAzureBlobClientFactory(),
            Environments.Production);

        await host.StartAsync();

        Assert.Equal("DefaultCredential", runtime.LastAzureCredentialMode);
    }

    private static readonly string?[] ReviewedEnvironmentNames =
    [
        Environments.Production,
        Environments.Staging,
        "Test",
        "QA",
        "Preview",
        "Local",
        "",
        null,
    ];

    public static TheoryData<CompositionRole, string?> DeployedEnvironments
    {
        get
        {
            TheoryData<CompositionRole, string?> data = [];
            foreach (CompositionRole role in new[]
            {
                CompositionRole.Api,
                CompositionRole.Worker,
            })
            {
                foreach (string? environmentName in ReviewedEnvironmentNames)
                {
                    data.Add(role, environmentName);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DeployedEnvironments))]
    public async Task Azure_default_credentials_are_rejected_outside_development(
        CompositionRole role,
        string? environmentName)
    {
        await AssertInvalidOptionsAsync(
            role,
            AzureDefaultCredentialSettings(),
            environmentName,
            "Azure default credentials are limited to local development");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Azure_default_credentials_stay_available_in_development_only(
        CompositionRole role)
    {
        foreach (string environmentName in new[] { "Development", "development" })
        {
            var runtime = new FakeRuntimeDependencies();
            using IHost host = BuildHost(
                role,
                AzureDefaultCredentialSettings(),
                runtime,
                new FakeAzureBlobClientFactory(),
                environmentName);

            await host.StartAsync();

            Assert.Equal("DefaultCredential", runtime.LastAzureCredentialMode);
        }
    }

    [Theory]
    [MemberData(nameof(DeployedEnvironments))]
    public async Task Managed_identity_needs_no_review_in_any_environment(
        CompositionRole role,
        string? environmentName)
    {
        var runtime = new FakeRuntimeDependencies();
        using IHost host = BuildHost(
            role,
            AzureSettings(),
            runtime,
            new FakeAzureBlobClientFactory(),
            environmentName);

        await host.StartAsync();

        Assert.Equal("ManagedIdentity", runtime.LastAzureCredentialMode);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Runtime_dependencies_without_mode_awareness_cannot_serve_managed_identity(
        CompositionRole role)
    {
        var runtime = new AmbientOnlyRuntimeDependencies();
        using IHost host = BuildHost(
            role,
            AzureSettings(),
            runtime,
            new FakeAzureBlobClientFactory(),
            Environments.Production);

        NotSupportedException error =
            await Assert.ThrowsAsync<NotSupportedException>(
                async () => await host.StartAsync());

        Assert.Contains("ManagedIdentity", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, runtime.AmbientCredentialRequests);
        Assert.Null(runtime.LastCredential);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Runtime_dependencies_without_mode_awareness_still_serve_development(
        CompositionRole role)
    {
        var runtime = new AmbientOnlyRuntimeDependencies();
        using IHost host = BuildHost(
            role,
            AzureDefaultCredentialSettings(),
            runtime,
            new FakeAzureBlobClientFactory(),
            Environments.Development);

        await host.StartAsync();

        Assert.Equal(1, runtime.AmbientCredentialRequests);
        Assert.IsType<DefaultAzureCredential>(runtime.LastCredential);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void Azure_default_credentials_fail_closed_without_a_host_environment(
        CompositionRole role)
    {
        Assert.Throws<OptionsValidationException>(
            () => ResolveMediaOptions(role, AzureDefaultCredentialSettings()));

        Assert.Null(
            Record.Exception(() => ResolveMediaOptions(role, AzureSettings())));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Managed_identity_requires_a_well_formed_user_assigned_client_id(
        CompositionRole role)
    {
        Dictionary<string, string?> missing = AzureSettings();
        missing.Remove("Media:Storage:Azure:ManagedIdentityClientId");
        await AssertInvalidOptionsAsync(
            role,
            missing,
            expectedFailureFragment:
            "Azure managed-identity mode requires an explicit user-assigned client ID.");

        foreach (string malformed in new[]
        {
            "not-a-guid",
            "{8ec1a4d5-42d1-4d84-9d2a-9a9a2f3f9a11}",
            "8ec1a4d542d14d849d2a9a9a2f3f9a11",
            " 8ec1a4d5-42d1-4d84-9d2a-9a9a2f3f9a11 ",
            "00000000-0000-0000-0000-000000000000",
            "   ",
        })
        {
            Dictionary<string, string?> settings = AzureSettings();
            settings["Media:Storage:Azure:ManagedIdentityClientId"] = malformed;
            await AssertInvalidOptionsAsync(
                role,
                settings,
                expectedFailureFragment: "client ID");
        }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Azure_credential_mode_and_client_id_combinations_are_validated(
        CompositionRole role)
    {
        Dictionary<string, string?> defaultWithClientId = AzureDefaultCredentialSettings();
        defaultWithClientId["Media:Storage:Azure:ManagedIdentityClientId"] =
            UserAssignedClientId;
        await AssertInvalidOptionsAsync(
            role,
            defaultWithClientId,
            Environments.Development,
            "requires the managed-identity credential mode");

        Dictionary<string, string?> sharedKeyWithClientId = SharedKeySettings();
        sharedKeyWithClientId["Media:Storage:Azure:ManagedIdentityClientId"] =
            UserAssignedClientId;
        await AssertInvalidOptionsAsync(
            role,
            sharedKeyWithClientId,
            expectedFailureFragment: "requires the managed-identity credential mode");

        Dictionary<string, string?> managedIdentityWithConnectionString = AzureSettings();
        managedIdentityWithConnectionString["Media:Storage:Azure:ConnectionString"] =
            SharedKeyConnectionString;
        await AssertInvalidOptionsAsync(
            role,
            managedIdentityWithConnectionString,
            expectedFailureFragment:
            "Azure shared-key settings cannot be combined with managed-identity credentials.");

        Dictionary<string, string?> managedIdentityWithSharedKeySas = AzureSettings();
        managedIdentityWithSharedKeySas["Media:Storage:Azure:AllowSharedKeySas"] = "true";
        await AssertInvalidOptionsAsync(
            role,
            managedIdentityWithSharedKeySas,
            expectedFailureFragment:
            "Azure shared-key settings cannot be combined with managed-identity credentials.");

        Dictionary<string, string?> managedIdentityWithReviewFlag = AzureSettings();
        managedIdentityWithReviewFlag[
            "Media:Storage:Azure:AllowDefaultCredentialOutsideDevelopment"] = "true";
        await AssertInvalidOptionsAsync(
            role,
            managedIdentityWithReviewFlag,
            expectedFailureFragment:
            "The reviewed default-credential opt-in applies only to the default credential mode.");

        Dictionary<string, string?> sharedKeyWithReviewFlag = SharedKeySettings();
        sharedKeyWithReviewFlag[
            "Media:Storage:Azure:AllowDefaultCredentialOutsideDevelopment"] = "true";
        await AssertInvalidOptionsAsync(
            role,
            sharedKeyWithReviewFlag,
            expectedFailureFragment:
            "The reviewed default-credential opt-in applies only to the default credential mode.");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Ambient_azure_credentials_require_first_party_endpoints(
        CompositionRole role)
    {
        Dictionary<string, string?> managedIdentity = AzureSettings();
        managedIdentity["Media:Storage:Azure:ServiceUri"] =
            "https://vistaratest.blob.core.windows.net.attacker.example";
        await AssertInvalidOptionsAsync(
            role,
            managedIdentity,
            expectedFailureFragment: "first-party Azure Blob endpoint");

        Dictionary<string, string?> otherAccount = AzureSettings();
        otherAccount["Media:Storage:Azure:ServiceUri"] =
            "https://someoneelse.blob.core.windows.net";
        await AssertInvalidOptionsAsync(
            role,
            otherAccount,
            expectedFailureFragment: "first-party Azure Blob endpoint");

        Dictionary<string, string?> defaultCredential = AzureDefaultCredentialSettings();
        defaultCredential["Media:Storage:Azure:ServiceUri"] =
            "https://vistaratest.blob.core.windows.net.attacker.example";
        await AssertInvalidOptionsAsync(
            role,
            defaultCredential,
            Environments.Development,
            "first-party Azure Blob endpoint");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Startup_logs_describe_credentials_without_disclosing_material(
        CompositionRole role)
    {
        var logs = new RecordingLoggerProvider();
        using IHost managedIdentityHost = BuildHost(
            role,
            AzureSettings(),
            new FakeRuntimeDependencies(),
            new FakeAzureBlobClientFactory(),
            Environments.Production,
            logs);

        await managedIdentityHost.StartAsync();

        string managedIdentityLog = logs.Text;
        Assert.Contains("ManagedIdentity", managedIdentityLog, StringComparison.Ordinal);
        Assert.DoesNotContain(
            UserAssignedClientId,
            managedIdentityLog,
            StringComparison.OrdinalIgnoreCase);

        var sharedKeyLogs = new RecordingLoggerProvider();
        using IHost sharedKeyHost = BuildHost(
            role,
            SharedKeySettings(),
            new FakeRuntimeDependencies(),
            new FakeAzureBlobClientFactory(),
            Environments.Production,
            sharedKeyLogs);

        await sharedKeyHost.StartAsync();

        Assert.Contains("SharedKey", sharedKeyLogs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SharedKeyAccountKey,
            sharedKeyLogs.Text,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SharedKeyConnectionString,
            sharedKeyLogs.Text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void Runtime_dependencies_create_one_shared_user_assigned_credential(
        CompositionRole role)
    {
        object runtime = CreateRuntimeDependencies(role);

        TokenCredential first = InvokeAzureCredential(
            runtime,
            ManagedIdentityOptions(role, UserAssignedClientId));
        TokenCredential second = InvokeAzureCredential(
            runtime,
            ManagedIdentityOptions(role, UserAssignedClientId));

        Assert.IsType<ManagedIdentityCredential>(first);
        Assert.Same(first, second);

        TokenCredential other = InvokeAzureCredential(
            runtime,
            ManagedIdentityOptions(role, OtherUserAssignedClientId));
        Assert.IsType<ManagedIdentityCredential>(other);
        Assert.NotSame(first, other);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void Runtime_dependencies_never_fall_back_to_developer_credentials(
        CompositionRole role)
    {
        object runtime = CreateRuntimeDependencies(role);

        TokenCredential managedIdentity = InvokeAzureCredential(
            runtime,
            ManagedIdentityOptions(role, UserAssignedClientId));

        Assert.IsNotType<DefaultAzureCredential>(managedIdentity);
        Assert.IsNotType<AzureCliCredential>(managedIdentity);
        Assert.IsNotType<ChainedTokenCredential>(managedIdentity);

        TokenCredential development = InvokeAzureCredential(
            runtime,
            DefaultCredentialOptions(role));
        Assert.IsType<DefaultAzureCredential>(development);
        Assert.Same(
            development,
            InvokeAzureCredential(runtime, DefaultCredentialOptions(role)));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void Runtime_dependencies_reject_malformed_or_unsupported_credential_modes(
        CompositionRole role)
    {
        object runtime = CreateRuntimeDependencies(role);

        Assert.Throws<InvalidOperationException>(
            () => InvokeAzureCredential(runtime, ManagedIdentityOptions(role, "not-a-guid")));
        Assert.Throws<InvalidOperationException>(
            () => InvokeAzureCredential(runtime, ManagedIdentityOptions(role, null)));
        Assert.Throws<InvalidOperationException>(
            () => InvokeAzureCredential(runtime, SharedKeyOptions(role)));
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

        Dictionary<string, string?> azure = AzureSettings();
        azure["Media:Storage:Azure:ConnectionString"] = SharedKeyConnectionString;

        OptionsValidationException azureError =
            await Assert.ThrowsAsync<OptionsValidationException>(
                async () => await StartHostAsync(
                    role,
                    azure,
                    new FakeRuntimeDependencies()));

        Assert.DoesNotContain(
            SharedKeyAccountKey,
            azureError.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            UserAssignedClientId,
            azureError.ToString(),
            StringComparison.Ordinal);
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
        Dictionary<string, string?> settings = SharedKeySettings();
        settings["Media:Storage:Azure:AllowSharedKeySas"] = "false";

        await AssertInvalidOptionsAsync(role, settings);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Shared_key_azure_credentials_are_used_only_after_explicit_opt_in(
        CompositionRole role)
    {
        Dictionary<string, string?> settings = SharedKeySettings();
        var runtime = new FakeRuntimeDependencies();
        var azureFactory = new FakeAzureBlobClientFactory();
        using IHost host = BuildHost(role, settings, runtime, azureFactory);

        await host.StartAsync();

        Assert.Equal(0, runtime.AzureCredentialRequests);
        Assert.Equal(0, runtime.AmbientAzureCredentialRequests);
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
        IReadOnlyDictionary<string, string?> settings,
        string? environmentName = null,
        string? expectedFailureFragment = null)
    {
        OptionsValidationException error =
            await Assert.ThrowsAsync<OptionsValidationException>(
                async () => await StartHostAsync(
                    role,
                    settings,
                    new FakeRuntimeDependencies(),
                    environmentName));
        if (expectedFailureFragment is not null)
        {
            Assert.Contains(
                expectedFailureFragment,
                string.Join(" ", error.Failures),
                StringComparison.Ordinal);
        }
    }

    private static async Task StartHostAsync(
        CompositionRole role,
        IReadOnlyDictionary<string, string?> settings,
        FakeRuntimeDependencies runtime,
        string? environmentName = null)
    {
        using IHost host = BuildHost(
            role,
            settings,
            runtime,
            azureFactory: null,
            environmentName);
        await host.StartAsync();
    }

    private static IHost BuildHost(
        CompositionRole role,
        IReadOnlyDictionary<string, string?> settings,
        object runtime,
        IAzureBlobClientFactory? azureFactory = null,
        string? environmentName = null,
        RecordingLoggerProvider? loggerProvider = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = environmentName,
            });
        builder.Configuration.AddInMemoryCollection(settings);
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        if (azureFactory is not null)
        {
            builder.Services.AddSingleton(azureFactory);
        }

        switch (role)
        {
            case CompositionRole.Api:
                builder.Services.AddSingleton(
                    (ApiMedia.IMediaRuntimeDependencies)runtime);
                ApiMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
                    builder.Services,
                    builder.Configuration);
                break;
            case CompositionRole.Worker:
                builder.Services.AddSingleton(
                    (WorkerMedia.IMediaRuntimeDependencies)runtime);
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
        settings["Media:Storage:Azure:CredentialMode"] = "ManagedIdentity";
        settings["Media:Storage:Azure:ManagedIdentityClientId"] = UserAssignedClientId;
        return settings;
    }

    private static Dictionary<string, string?> AzureDefaultCredentialSettings()
    {
        Dictionary<string, string?> settings = AzureSettings();
        settings["Media:Storage:Azure:CredentialMode"] = "DefaultCredential";
        settings.Remove("Media:Storage:Azure:ManagedIdentityClientId");
        return settings;
    }

    private static Dictionary<string, string?> SharedKeySettings()
    {
        Dictionary<string, string?> settings = AzureSettings();
        settings["Media:Storage:Azure:CredentialMode"] = "SharedKey";
        settings.Remove("Media:Storage:Azure:ManagedIdentityClientId");
        settings["Media:Storage:Azure:ConnectionString"] = SharedKeyConnectionString;
        settings["Media:Storage:Azure:AllowSharedKeySas"] = "true";
        return settings;
    }

    private static void ResolveMediaOptions(
        CompositionRole role,
        IReadOnlyDictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        ServiceCollection services = [];
        services.AddSingleton<ApiMedia.IMediaRuntimeDependencies>(
            new FakeRuntimeDependencies());
        services.AddSingleton<WorkerMedia.IMediaRuntimeDependencies>(
            new FakeRuntimeDependencies());
        services.AddSingleton<IAzureBlobClientFactory>(new FakeAzureBlobClientFactory());
        switch (role)
        {
            case CompositionRole.Api:
                ApiMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
                    services,
                    configuration);
                break;
            case CompositionRole.Worker:
                WorkerMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
                    services,
                    configuration);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role));
        }

        using ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IBlobStore>();
    }

    private static object CreateRuntimeDependencies(CompositionRole role) =>
        role switch
        {
            CompositionRole.Api => new ApiMedia.DefaultMediaRuntimeDependencies(),
            CompositionRole.Worker => new WorkerMedia.DefaultMediaRuntimeDependencies(),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private static TokenCredential InvokeAzureCredential(object runtime, object options) =>
        (runtime, options) switch
        {
            (ApiMedia.IMediaRuntimeDependencies api, ApiMedia.MediaAzureOptions apiOptions) =>
                api.CreateAzureCredential(apiOptions),
            (WorkerMedia.IMediaRuntimeDependencies worker,
                WorkerMedia.MediaAzureOptions workerOptions) =>
                worker.CreateAzureCredential(workerOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(runtime)),
        };

    private static object ManagedIdentityOptions(CompositionRole role, string? clientId) =>
        role switch
        {
            CompositionRole.Api => new ApiMedia.MediaAzureOptions
            {
                AccountName = "vistaratest",
                ContainerName = "media",
                ServiceUri = AzureServiceUri,
                CredentialMode = ApiMedia.MediaAzureCredentialMode.ManagedIdentity,
                ManagedIdentityClientId = clientId,
            },
            CompositionRole.Worker => new WorkerMedia.MediaAzureOptions
            {
                AccountName = "vistaratest",
                ContainerName = "media",
                ServiceUri = AzureServiceUri,
                CredentialMode = WorkerMedia.MediaAzureCredentialMode.ManagedIdentity,
                ManagedIdentityClientId = clientId,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private static object DefaultCredentialOptions(CompositionRole role) =>
        role switch
        {
            CompositionRole.Api => new ApiMedia.MediaAzureOptions
            {
                AccountName = "vistaratest",
                ContainerName = "media",
                ServiceUri = AzureServiceUri,
                CredentialMode = ApiMedia.MediaAzureCredentialMode.DefaultCredential,
            },
            CompositionRole.Worker => new WorkerMedia.MediaAzureOptions
            {
                AccountName = "vistaratest",
                ContainerName = "media",
                ServiceUri = AzureServiceUri,
                CredentialMode = WorkerMedia.MediaAzureCredentialMode.DefaultCredential,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private static object SharedKeyOptions(CompositionRole role) =>
        role switch
        {
            CompositionRole.Api => new ApiMedia.MediaAzureOptions
            {
                AccountName = "vistaratest",
                ContainerName = "media",
                ServiceUri = AzureServiceUri,
                CredentialMode = ApiMedia.MediaAzureCredentialMode.SharedKey,
                ConnectionString = SharedKeyConnectionString,
                AllowSharedKeySas = true,
            },
            CompositionRole.Worker => new WorkerMedia.MediaAzureOptions
            {
                AccountName = "vistaratest",
                ContainerName = "media",
                ServiceUri = AzureServiceUri,
                CredentialMode = WorkerMedia.MediaAzureCredentialMode.SharedKey,
                ConnectionString = SharedKeyConnectionString,
                AllowSharedKeySas = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

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

        public int AmbientAzureCredentialRequests { get; private set; }

        public string? LastAzureCredentialMode { get; private set; }

        public string? LastManagedIdentityClientId { get; private set; }

        public AWSCredentials CreateS3Credentials(
            ApiMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public AWSCredentials CreateS3Credentials(
            WorkerMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public TokenCredential CreateAzureCredential()
        {
            AmbientAzureCredentialRequests++;
            return new FakeTokenCredential();
        }

        public TokenCredential CreateAzureCredential(ApiMedia.MediaAzureOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Record(
                options.CredentialMode?.ToString(),
                options.ManagedIdentityClientId);
        }

        public TokenCredential CreateAzureCredential(WorkerMedia.MediaAzureOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Record(
                options.CredentialMode?.ToString(),
                options.ManagedIdentityClientId);
        }

        public IImageProcessor CreateImageProcessor()
        {
            if (ImageProcessorError is not null)
            {
                throw ImageProcessorError;
            }

            return new FakeImageProcessor();
        }

        private FakeTokenCredential Record(string? credentialMode, string? clientId)
        {
            AzureCredentialRequests++;
            LastAzureCredentialMode = credentialMode;
            LastManagedIdentityClientId = clientId;
            return new FakeTokenCredential();
        }
    }

    /// <summary>
    /// A runtime dependency that only implements the ambient development seam.
    /// It stands in for a custom composition that has not adopted the
    /// mode-aware factory.
    /// </summary>
    private sealed class AmbientOnlyRuntimeDependencies :
        ApiMedia.IMediaRuntimeDependencies,
        WorkerMedia.IMediaRuntimeDependencies
    {
        public int AmbientCredentialRequests { get; private set; }

        public TokenCredential? LastCredential { get; private set; }

        public AWSCredentials CreateS3Credentials(ApiMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public AWSCredentials CreateS3Credentials(WorkerMedia.MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public TokenCredential CreateAzureCredential()
        {
            AmbientCredentialRequests++;
            LastCredential = new DefaultAzureCredential();
            return LastCredential;
        }

        public IImageProcessor CreateImageProcessor() => new FakeImageProcessor();
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries =
            new();

        public string Text => string.Join(Environment.NewLine, entries);

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string categoryName,
            ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                entries.Enqueue(
                    $"{categoryName} {logLevel} {formatter(state, exception)} {exception}");
            }
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
