using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Admin;
using Vistara.Storage.Azure;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// An Azure candidate names its host indirectly, through an account name and an
/// endpoint suffix, so a hostile suffix can construct a host that merely looks
/// like Azure. Ambient managed identity would then hand a real token to that
/// host, so trust is decided by the production allowlist before any credential
/// is constructed.
/// </summary>
public sealed class AzureValidationTrustTests
{
    [Theory]
    [InlineData("core.windows.net")]
    [InlineData("CORE.WINDOWS.NET")]
    [InlineData("core.usgovcloudapi.net")]
    [InlineData("core.chinacloudapi.cn")]
    [InlineData("core.cloudapi.de")]
    [InlineData("storage.azure.net")]
    public async Task Managed_identity_is_allowed_on_a_trusted_azure_cloud(string suffix)
    {
        var factory = new CountingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate = Candidate(
            $"https://vistaramedia.blob.{suffix}",
            AzureCredentialKind.ManagedIdentity);
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.True(outcome.Valid);
        Assert.Equal(1, factory.Constructions);
    }

    [Theory]
    [InlineData("https://vistaramedia.blob.core.windows.net.attacker.example")]
    [InlineData("https://vistaramedia.blob.evilcore.windows.net")]
    [InlineData("https://vistaramedia.blob.core.windows.net.")]
    [InlineData("https://vistaramedia.blob.core.windows.nettrap.example")]
    [InlineData("https://attacker.example/vistaramedia.blob.core.windows.net")]
    [InlineData("https://vistaramediablobcorewindowsnet.attacker.example")]
    [InlineData("https://xn--vistaramedia-blob-core-windows-nt-4nb.example")]
    [InlineData("https://93.184.216.34")]
    [InlineData("https://vistaramedia.blob.core.windows.net:8443")]
    public async Task Managed_identity_never_reaches_an_untrusted_host(string endpoint)
    {
        var factory = new CountingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate = Candidate(
            endpoint,
            AzureCredentialKind.ManagedIdentity);
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(
            StorageValidationDetails.AmbientCredentialRefused,
            outcome.Checks[0].Detail);
        Assert.Equal(0, factory.Constructions);
    }

    [Fact]
    public async Task Managed_identity_is_refused_even_on_an_operator_allowlisted_host()
    {
        var factory = new CountingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(
            factory,
            trustedHost: "storage.internal.example");

        using StorageValidationCandidate candidate = Candidate(
            "https://storage.internal.example",
            AzureCredentialKind.ManagedIdentity);
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(
            StorageValidationDetails.AmbientCredentialRefused,
            outcome.Checks[0].Detail);
        Assert.Equal(0, factory.Constructions);
    }

    [Fact]
    public async Task An_account_key_is_refused_on_a_host_that_is_not_azure_or_allowlisted()
    {
        var factory = new CountingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate = Candidate(
            "https://vistaramedia.blob.core.windows.net.attacker.example",
            AzureCredentialKind.AccountKey,
            RedactedSecret.From("azure-account-key-sentinel"));
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(
            StorageValidationDetails.EndpointRejected,
            outcome.Checks[0].Detail);
        Assert.Equal(0, factory.Constructions);
    }

    [Fact]
    public async Task An_account_key_may_still_reach_an_operator_allowlisted_host()
    {
        var factory = new CountingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(
            factory,
            trustedHost: "storage.internal.example");

        using StorageValidationCandidate candidate = Candidate(
            "https://storage.internal.example",
            AzureCredentialKind.AccountKey,
            RedactedSecret.From("azure-account-key-sentinel"));
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.True(outcome.Valid);
        Assert.Equal(1, factory.Constructions);
    }

    [Fact]
    public void The_trust_check_is_the_production_allowlist()
    {
        Assert.True(AzureBlobStoreOptions.IsTrustedBlobEndpoint(
            "vistaramedia",
            new Uri("https://vistaramedia.blob.core.windows.net")));
        Assert.True(AzureBlobStoreOptions.IsTrustedBlobEndpoint(
            "vistaramedia",
            new Uri("https://vistaramedia.privatelink.blob.core.windows.net")));
        Assert.False(AzureBlobStoreOptions.IsTrustedBlobEndpoint(
            "vistaramedia",
            new Uri("https://vistaramedia.blob.core.windows.net.attacker.example")));
        Assert.False(AzureBlobStoreOptions.IsTrustedBlobEndpoint(
            "other",
            new Uri("https://vistaramedia.blob.core.windows.net")));
        Assert.False(AzureBlobStoreOptions.IsTrustedBlobEndpoint(
            "vistaramedia",
            new Uri("http://vistaramedia.blob.core.windows.net")));
        Assert.False(AzureBlobStoreOptions.IsTrustedBlobEndpoint(
            "vistaramedia",
            new Uri("https://vistaramedia.blob.core.windows.net/admin")));
    }

    private static StorageValidationCandidate Candidate(
        string endpoint,
        AzureCredentialKind credential,
        RedactedSecret? accountKey = null) =>
        new(
            StorageCandidateKind.AzureBlob,
            "azureBlob",
            endpoint: new Uri(endpoint),
            container: "private-media",
            accountName: "vistaramedia",
            azureCredential: credential,
            accountKey: accountKey);

    private static PlatformStorageValidationAdapter CreatePort(
        IStorageValidationClientFactory factory,
        string? trustedHost = null)
    {
        var media = new MediaOptions();
        if (trustedHost is not null)
        {
            media.Storage.S3.AllowedEndpointHosts = [trustedHost];
        }

        return new PlatformStorageValidationAdapter(factory, Options.Create(media));
    }

    /// <summary>
    /// Counts client construction. A rejected candidate must never reach it,
    /// because that is where a token credential would be built and a token
    /// acquired.
    /// </summary>
    private sealed class CountingClientFactory : IStorageValidationClientFactory
    {
        public int Constructions { get; private set; }

        public ValueTask<IStorageValidationClient> CreateAsync(
            StorageValidationCandidate candidate,
            CancellationToken cancellationToken)
        {
            Constructions++;
            return ValueTask.FromResult<IStorageValidationClient>(new PassingClient());
        }
    }

    private sealed class PassingClient : IStorageValidationClient
    {
        public ValueTask<StorageValidationOutcome> ProbeAsync(
            string probeKey,
            CancellationToken cancellationToken)
        {
            var recorder = new StorageProbeRecorder();
            recorder.Pass(StorageCheckId.Reachable);
            recorder.Pass(StorageCheckId.Authenticated);
            recorder.Pass(StorageCheckId.Read);
            recorder.Pass(StorageCheckId.Write);
            recorder.Pass(StorageCheckId.Delete);
            return ValueTask.FromResult(
                recorder.Complete(StorageValidationDetails.ValidMessage));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
