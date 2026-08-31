using System.Security.Cryptography;
using System.Xml.Linq;
using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Security;
using Xunit;

namespace Vistara.IntegrationTests.AdapterComposition;

public sealed class DataProtectionCompositionTests
{
    private const string BlobHost = "vistarakeys.blob.core.windows.net";
    private const string VaultHost = "vistara-keyring.vault.azure.net";
    private const string ContainerName = "vistara-key-ring";
    private const string KeyBlobName = "vistara-api/keys.xml";
    private const string KeyName = "vistara-data-protection";
    private const string ClientId = "1f9d5a3c-6b8e-4d21-9f57-0c2a8b6e4d13";
    private const string Discriminator = "vistara-api-hosted";

    [Fact]
    public async Task Shared_key_ring_is_readable_by_every_hosted_replica()
    {
        var repository = new InMemoryXmlRepository();
        using IHost first = BuildHost(EnabledSettings(), new FakeRuntime(repository));
        using IHost second = BuildHost(EnabledSettings(), new FakeRuntime(repository));

        await first.StartAsync();
        await second.StartAsync();

        IDataProtector origin = first.Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("vistara.session");
        IDataProtector replica = second.Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("vistara.session");

        string payload = replica.Unprotect(origin.Protect("replica-payload"));

        Assert.Equal("replica-payload", payload);
        Assert.Equal(1, repository.StoredElementCount);
        Assert.All(
            repository.GetAllElements(),
            element => Assert.NotNull(element.Descendants("protected").FirstOrDefault()));
    }

    [Fact]
    public async Task Application_discriminator_isolates_key_rings_that_share_storage()
    {
        var repository = new InMemoryXmlRepository();
        Dictionary<string, string?> other = EnabledSettings();
        other["Security:DataProtection:ApplicationDiscriminator"] = "vistara-worker-hosted";
        using IHost api = BuildHost(EnabledSettings(), new FakeRuntime(repository));
        using IHost worker = BuildHost(other, new FakeRuntime(repository));

        await api.StartAsync();
        await worker.StartAsync();

        Assert.Equal(
            Discriminator,
            api.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator);
        Assert.Equal(
            "vistara-worker-hosted",
            worker.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator);

        byte[] protectedPayload = api.Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("vistara.session")
            .Protect("replica-payload"u8.ToArray());

        Assert.Throws<CryptographicException>(() => worker.Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("vistara.session")
            .Unprotect(protectedPayload));
    }

    [Fact]
    public async Task One_managed_identity_credential_serves_persistence_and_protection()
    {
        var runtime = new FakeRuntime(new InMemoryXmlRepository());
        using IHost host = BuildHost(EnabledSettings(), runtime);

        await host.StartAsync();
        _ = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("vistara.session")
            .Protect("payload");

        Assert.Equal(1, runtime.PersistenceConfigurations);
        Assert.Equal(1, runtime.ProtectionConfigurations);
        Assert.Equal(1, runtime.CredentialCreations);
        Assert.Equal([ClientId], runtime.RequestedClientIds);
        Assert.Same(
            host.Services.GetRequiredService<DataProtectionCredentialSource>().Credential,
            host.Services.GetRequiredService<DataProtectionCredentialSource>().Credential);
    }

    [Fact]
    public async Task Disabled_configuration_preserves_the_local_ephemeral_key_ring()
    {
        var runtime = new FakeRuntime(new InMemoryXmlRepository());
        var services = new ServiceCollection();
        using IHost host = BuildHost(
            new Dictionary<string, string?>(),
            runtime,
            collected: services);

        await host.StartAsync();

        Assert.Equal(0, runtime.PersistenceConfigurations);
        Assert.Equal(0, runtime.ProtectionConfigurations);
        Assert.Equal(0, runtime.CredentialCreations);
        Assert.Null(host.Services.GetService<DataProtectionCredentialSource>());
        Assert.Null(host.Services.GetService<IDataProtectionRuntimeDependencies>());
        Assert.DoesNotContain(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IDataProtectionProvider) ||
                descriptor.ServiceType == typeof(IPostConfigureOptions<DataProtectionOptions>) ||
                descriptor.ServiceType == typeof(IConfigureOptions<KeyManagementOptions>));
    }

    [Fact]
    public async Task Disabled_but_configured_key_ring_fails_fast_outside_development()
    {
        Dictionary<string, string?> settings = EnabledSettings();
        settings["Security:DataProtection:Enabled"] = "false";

        await AssertInvalidAsync(settings);

        using IHost development = BuildHost(
            settings,
            new FakeRuntime(new InMemoryXmlRepository()),
            environmentName: Environments.Development);
        await development.StartAsync();

        Assert.Null(development.Services.GetService<DataProtectionCredentialSource>());
    }

    [Fact]
    public async Task Blob_persistence_and_key_vault_protection_must_be_configured_together()
    {
        Dictionary<string, string?> blobOnly = EnabledSettings();
        blobOnly.Remove("Security:DataProtection:KeyVaultKeyIdentifier");
        await AssertInvalidAsync(blobOnly);

        Dictionary<string, string?> vaultOnly = EnabledSettings();
        vaultOnly.Remove("Security:DataProtection:BlobServiceUri");
        vaultOnly.Remove("Security:DataProtection:BlobContainerName");
        vaultOnly.Remove("Security:DataProtection:KeyBlobName");
        await AssertInvalidAsync(vaultOnly);

        Dictionary<string, string?> neither = EnabledSettings();
        neither.Remove("Security:DataProtection:KeyVaultKeyIdentifier");
        neither.Remove("Security:DataProtection:BlobServiceUri");
        neither.Remove("Security:DataProtection:BlobContainerName");
        neither.Remove("Security:DataProtection:KeyBlobName");
        await AssertInvalidAsync(neither);
    }

    [Fact]
    public async Task Untrusted_or_non_default_azure_endpoints_are_rejected()
    {
        await AssertInvalidAsync(
            WithSetting("BlobServiceUri", $"http://{BlobHost}"));
        await AssertInvalidAsync(
            WithSetting("BlobServiceUri", $"https://{BlobHost}:8443"));
        await AssertInvalidAsync(
            WithSetting("BlobServiceUri", $"https://{BlobHost}/{ContainerName}"));
        await AssertInvalidAsync(
            WithSetting("BlobServiceUri", $"https://{BlobHost}/?sv=2024-01-01"));
        await AssertInvalidAsync(
            WithSetting("BlobServiceUri", "https://vistarakeys.example.com"));
        await AssertInvalidAsync(
            WithSetting("KeyVaultKeyIdentifier", $"http://{VaultHost}/keys/{KeyName}"));
        await AssertInvalidAsync(
            WithSetting("KeyVaultKeyIdentifier", $"https://{VaultHost}:8443/keys/{KeyName}"));
        await AssertInvalidAsync(
            WithSetting("KeyVaultKeyIdentifier", $"https://{VaultHost}/secrets/{KeyName}"));
        await AssertInvalidAsync(
            WithSetting("KeyVaultKeyIdentifier", "https://vistara-keyring.example.com/keys/k"));
    }

    [Fact]
    public async Task Versioned_key_identifiers_and_invalid_names_are_rejected()
    {
        await AssertInvalidAsync(WithSetting(
            "KeyVaultKeyIdentifier",
            $"https://{VaultHost}/keys/{KeyName}/0123456789abcdef0123456789abcdef"));
        await AssertInvalidAsync(WithSetting("ApplicationDiscriminator", ""));
        await AssertInvalidAsync(WithSetting("ApplicationDiscriminator", "vistara api"));
        await AssertInvalidAsync(WithSetting("ManagedIdentityClientId", "not-a-guid"));
        await AssertInvalidAsync(WithSetting(
            "ManagedIdentityClientId",
            "00000000-0000-0000-0000-000000000000"));
        await AssertInvalidAsync(WithSetting("BlobContainerName", "Data--Protection"));
        await AssertInvalidAsync(WithSetting("BlobContainerName", "dp"));
        await AssertInvalidAsync(WithSetting("KeyBlobName", "../keys.xml"));
        await AssertInvalidAsync(WithSetting("KeyBlobName", "keys\\ring.xml"));
    }

    [Fact]
    public async Task Validation_failures_never_disclose_endpoints_or_identifiers()
    {
        Dictionary<string, string?> settings = EnabledSettings();
        settings["Security:DataProtection:BlobServiceUri"] = $"http://{BlobHost}";
        settings["Security:DataProtection:KeyVaultKeyIdentifier"] =
            $"https://{VaultHost}/keys/{KeyName}/0123456789abcdef0123456789abcdef";

        OptionsValidationException error = await AssertInvalidAsync(settings);
        string rendered = error.ToString();

        foreach (string secret in new[]
                 {
                     BlobHost,
                     VaultHost,
                     ContainerName,
                     KeyBlobName,
                     KeyName,
                     ClientId,
                     Discriminator,
                 })
        {
            Assert.DoesNotContain(secret, rendered, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            SecurityDataProtectionOptions.SectionName,
            rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_is_idempotent_and_wired_through_api_security()
    {
        var runtime = new FakeRuntime(new InMemoryXmlRepository());
        HostApplicationBuilder builder = CreateBuilder(
            EnabledSettings(),
            Environments.Production);
        builder.Services.AddSingleton<IDataProtectionRuntimeDependencies>(runtime);
        builder.Services.AddVistaraApiSecurity(
            builder.Configuration,
            builder.Environment);
        builder.Services.AddVistaraApiSecurity(
            builder.Configuration,
            builder.Environment);
        builder.Services.AddVistaraApiDataProtection(
            builder.Configuration,
            builder.Environment,
            runtime);

        Assert.Equal(
            1,
            builder.Services.Count(descriptor =>
                descriptor.ServiceType == typeof(IVistaraDataProtectionRegistration)));

        using IHost host = builder.Build();
        await host.StartAsync();

        Assert.Equal(1, runtime.PersistenceConfigurations);
        Assert.Equal(1, runtime.ProtectionConfigurations);
        Assert.Equal(
            Discriminator,
            host.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator);
        Assert.Same(
            runtime,
            host.Services.GetRequiredService<IDataProtectionRuntimeDependencies>());
    }

    private static async Task<OptionsValidationException> AssertInvalidAsync(
        IReadOnlyDictionary<string, string?> settings)
    {
        return await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            using IHost host = BuildHost(
                settings,
                new FakeRuntime(new InMemoryXmlRepository()));
            await host.StartAsync();
        });
    }

    private static Dictionary<string, string?> WithSetting(string key, string value)
    {
        Dictionary<string, string?> settings = EnabledSettings();
        settings[$"Security:DataProtection:{key}"] = value;
        return settings;
    }

    private static Dictionary<string, string?> EnabledSettings() => new()
    {
        ["Security:DataProtection:Enabled"] = "true",
        ["Security:DataProtection:ApplicationDiscriminator"] = Discriminator,
        ["Security:DataProtection:BlobServiceUri"] = $"https://{BlobHost}",
        ["Security:DataProtection:BlobContainerName"] = ContainerName,
        ["Security:DataProtection:KeyBlobName"] = KeyBlobName,
        ["Security:DataProtection:KeyVaultKeyIdentifier"] =
            $"https://{VaultHost}/keys/{KeyName}",
        ["Security:DataProtection:ManagedIdentityClientId"] = ClientId,
    };

    private static HostApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> settings,
        string environmentName)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = environmentName,
                ApplicationName = "Vistara.DataProtectionTests",
            });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static IHost BuildHost(
        IReadOnlyDictionary<string, string?> settings,
        FakeRuntime runtime,
        string? environmentName = null,
        IServiceCollection? collected = null)
    {
        HostApplicationBuilder builder = CreateBuilder(
            settings,
            environmentName ?? Environments.Production);
        builder.Services.AddVistaraApiDataProtection(
            builder.Configuration,
            builder.Environment,
            runtime);
        if (collected is not null)
        {
            foreach (ServiceDescriptor descriptor in builder.Services)
            {
                collected.Add(descriptor);
            }
        }

        return builder.Build();
    }

    private sealed class FakeRuntime(InMemoryXmlRepository repository) :
        IDataProtectionRuntimeDependencies
    {
        public int CredentialCreations { get; private set; }

        public int PersistenceConfigurations { get; private set; }

        public int ProtectionConfigurations { get; private set; }

        public List<string> RequestedClientIds { get; } = [];

        public TokenCredential CreateManagedIdentityCredential(string clientId)
        {
            CredentialCreations++;
            RequestedClientIds.Add(clientId);
            return new FakeTokenCredential();
        }

        public void ConfigureKeyPersistence(IDataProtectionBuilder builder)
        {
            PersistenceConfigurations++;
            builder.Services.AddOptions<KeyManagementOptions>()
                .PostConfigure<IServiceProvider>((options, provider) =>
                {
                    _ = provider
                        .GetRequiredService<DataProtectionCredentialSource>()
                        .Credential;
                    options.XmlRepository = repository;
                });
        }

        public void ConfigureKeyProtection(IDataProtectionBuilder builder)
        {
            ProtectionConfigurations++;
            builder.Services.AddOptions<KeyManagementOptions>()
                .PostConfigure<IServiceProvider>((options, provider) =>
                {
                    _ = provider
                        .GetRequiredService<DataProtectionCredentialSource>()
                        .Credential;
                    options.XmlEncryptor = new RecordingXmlEncryptor();
                });
        }
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];
        private readonly Lock gate = new();

        public int StoredElementCount
        {
            get
            {
                lock (gate)
                {
                    return elements.Count;
                }
            }
        }

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (gate)
            {
                return elements.Select(element => new XElement(element)).ToList();
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (gate)
            {
                elements.Add(new XElement(element));
            }
        }
    }

    private sealed class RecordingXmlEncryptor : IXmlEncryptor
    {
        public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
            new(
                new XElement("protected", new XElement(plaintextElement)),
                typeof(RecordingXmlDecryptor));
    }

    public sealed class RecordingXmlDecryptor : IXmlDecryptor
    {
        public XElement Decrypt(XElement encryptedElement)
        {
            ArgumentNullException.ThrowIfNull(encryptedElement);
            return new XElement(encryptedElement.Elements().Single());
        }
    }
}
