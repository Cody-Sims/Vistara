using Azure.Core;
using Azure.Identity;
using Vistara.Storage.Azure;

namespace Vistara.Storage.ConformanceTests.Azure;

public sealed class AzureBlobStoreConfigurationTests
{
    private static readonly Uri ServiceUri =
        new("https://account123.blob.core.windows.net");

    [Fact]
    public void Azure_uses_default_token_credential_unless_explicitly_overridden()
    {
        RecordingFactory factory = new();

        _ = new AzureBlobStore(
            new AzureBlobStoreOptions("account123", "media", ServiceUri),
            factory);

        Assert.IsType<DefaultAzureCredential>(factory.TokenCredential);
        Assert.Null(factory.ConnectionString);
    }

    [Fact]
    public void Azure_accepts_injected_workload_identity_credential()
    {
        RecordingFactory factory = new();
        TokenCredential credential = new TestTokenCredential();
        AzureBlobStoreOptions options =
            new("account123", "media", ServiceUri)
            {
                TokenCredential = credential,
            };

        _ = new AzureBlobStore(options, factory);

        Assert.Same(credential, factory.TokenCredential);
    }

    [Fact]
    public void Azure_connection_string_authentication_must_be_explicit_and_redacted()
    {
        const string secret =
            "DefaultEndpointsProtocol=https;AccountName=account123;AccountKey=secret";
        RecordingFactory factory = new();
        AzureBlobStoreOptions options =
            new("account123", "media", ServiceUri)
            {
                CredentialMode = AzureBlobCredentialMode.ConnectionString,
                ConnectionString = secret,
                SasMode = AzureBlobSasMode.SharedKey,
                AllowSharedKeySas = true,
            };

        _ = new AzureBlobStore(options, factory);

        Assert.Equal(secret, factory.ConnectionString);
        Assert.Null(factory.TokenCredential);
        Assert.DoesNotContain("secret", options.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Account")]
    [InlineData("ab")]
    [InlineData("account-name")]
    public void Azure_rejects_invalid_account_names(string accountName)
    {
        Assert.Throws<ArgumentException>(
            () => new AzureBlobStoreOptions(accountName, "media", ServiceUri));
    }

    [Theory]
    [InlineData("Media")]
    [InlineData("ab")]
    [InlineData("-media")]
    [InlineData("media-")]
    [InlineData("media--private")]
    public void Azure_rejects_invalid_container_names(string containerName)
    {
        Assert.Throws<ArgumentException>(
            () => new AzureBlobStoreOptions("account123", containerName, ServiceUri));
    }

    [Fact]
    public void Azure_rejects_http_except_for_explicit_emulator_mode()
    {
        Uri endpoint = new("http://127.0.0.1:10000/devstoreaccount1");

        Assert.Throws<ArgumentException>(
            () => new AzureBlobStoreOptions("devstoreaccount1", "media", endpoint));

        AzureBlobStoreOptions options =
            new(
                "devstoreaccount1",
                "media",
                endpoint,
                emulatorMode: true);
        RecordingFactory factory = new();
        _ = new AzureBlobStore(options, factory);

        Assert.Equal(endpoint, factory.ServiceUri);
        Assert.True(factory.EmulatorMode);
    }

    [Fact]
    public void Azure_rejects_endpoint_account_mismatches()
    {
        Assert.Throws<ArgumentException>(
            () => new AzureBlobStore(
                new AzureBlobStoreOptions(
                    "account123",
                    "media",
                    new Uri("https://different.blob.core.windows.net")),
                new RecordingFactory()));
        Assert.Throws<ArgumentException>(
            () => new AzureBlobStoreOptions(
                "devstoreaccount1",
                "media",
                new Uri("http://127.0.0.1:10000/different"),
                emulatorMode: true));
    }

    [Theory]
    [InlineData("https://account123.blob.core.windows.net.evil.example")]
    [InlineData("https://account123.evil.blob.core.windows.net")]
    [InlineData("https://account123.blob.core.windows.net:444")]
    [InlineData("https://account123.example.com")]
    public void Azure_rejects_untrusted_token_credential_endpoints_before_creating_a_client(
        string endpoint)
    {
        RecordingFactory factory = new();
        AzureBlobStoreOptions options =
            new("account123", "media", new Uri(endpoint));

        Assert.Throws<ArgumentException>(() => new AzureBlobStore(options, factory));
        Assert.Equal(0, factory.CreateCalls);
    }

    [Theory]
    [InlineData("https://account123.blob.core.windows.net")]
    [InlineData("https://account123.privatelink.blob.core.windows.net")]
    [InlineData("https://account123.blob.core.usgovcloudapi.net")]
    [InlineData("https://account123.blob.core.chinacloudapi.cn")]
    public void Azure_accepts_trusted_cloud_and_private_link_endpoints(string endpoint)
    {
        RecordingFactory factory = new();

        _ = new AzureBlobStore(
            new AzureBlobStoreOptions("account123", "media", new Uri(endpoint)),
            factory);

        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public void Azure_accepts_an_exact_explicitly_allowlisted_token_endpoint()
    {
        Uri endpoint = new("https://blob.internal.example:8443");
        RecordingFactory factory = new();
        AzureBlobStoreOptions options =
            new("account123", "media", endpoint)
            {
                AllowedEndpointOrigins = [endpoint],
            };

        _ = new AzureBlobStore(options, factory);

        Assert.Equal(endpoint, factory.ServiceUri);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public void Azure_rejects_implicit_connection_strings_and_shared_key_sas()
    {
        AzureBlobStoreOptions implicitConnectionString =
            new("account123", "media", ServiceUri)
            {
                ConnectionString = "AccountName=account123;AccountKey=secret",
            };
        AzureBlobStoreOptions implicitSharedKey =
            new("account123", "media", ServiceUri)
            {
                CredentialMode = AzureBlobCredentialMode.ConnectionString,
                ConnectionString = "AccountName=account123;AccountKey=secret",
                SasMode = AzureBlobSasMode.SharedKey,
            };

        Assert.Throws<ArgumentException>(
            () => new AzureBlobStore(implicitConnectionString, new RecordingFactory()));
        Assert.Throws<ArgumentException>(
            () => new AzureBlobStore(implicitSharedKey, new RecordingFactory()));
    }

    [Fact]
    public void Azure_reports_only_implemented_capabilities()
    {
        AzureBlobStore store = new(
            new AzureBlobStoreOptions("account123", "media", ServiceUri),
            new RecordingFactory());

        Assert.Equal("azure", store.Name);
        Assert.True(store.Capabilities.SupportsDirectUpload);
        Assert.True(store.Capabilities.SupportsMultipartUpload);
        Assert.True(store.Capabilities.SupportsRangeReads);
        Assert.True(store.Capabilities.SupportsConditionalRead);
        Assert.True(store.Capabilities.SupportsConditionalCreate);
        Assert.True(store.Capabilities.SupportsConditionalReplace);
        Assert.True(store.Capabilities.SupportsConditionalCopy);
        Assert.True(store.Capabilities.SupportsConditionalDelete);
        Assert.True(store.Capabilities.SupportsConditionalMultipartCompletion);
        Assert.True(store.Capabilities.SupportsServerSideCopy);
        Assert.False(store.Capabilities.SupportsObjectVersioning);
        Assert.True(store.Capabilities.SupportsSignedRead);
    }

    private sealed class RecordingFactory : IAzureBlobClientFactory
    {
        public TokenCredential? TokenCredential { get; private set; }

        public string? ConnectionString { get; private set; }

        public Uri? ServiceUri { get; private set; }

        public bool EmulatorMode { get; private set; }

        public int CreateCalls { get; private set; }

        public IAzureBlobClient CreateWithTokenCredential(
            Uri serviceUri,
            string accountName,
            string containerName,
            TokenCredential credential,
            bool emulatorMode)
        {
            CreateCalls++;
            ServiceUri = serviceUri;
            TokenCredential = credential;
            EmulatorMode = emulatorMode;
            return new UnusedAzureBlobClient();
        }

        public IAzureBlobClient CreateWithConnectionString(
            string connectionString,
            Uri serviceUri,
            string accountName,
            string containerName,
            bool emulatorMode)
        {
            CreateCalls++;
            ConnectionString = connectionString;
            ServiceUri = serviceUri;
            EmulatorMode = emulatorMode;
            return new UnusedAzureBlobClient();
        }
    }

    private sealed class TestTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("test", DateTimeOffset.MaxValue));
    }

    private sealed class UnusedAzureBlobClient : AzureBlobClientBase;
}
