using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Admin;
using Vistara.Persistence;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Exercises the shipped storage validation adapter with a fake transport:
/// network policy, redaction, absence of persistence, and cancellation.
/// </summary>
public sealed class StorageValidationAdapterTests
{
    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://10.1.2.3")]
    [InlineData("https://192.168.4.5")]
    [InlineData("https://172.16.9.9")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fd00::1]")]
    [InlineData("https://100.64.1.1")]
    public async Task A_private_or_link_local_endpoint_is_never_probed(string endpoint)
    {
        var probe = new RecordingProbe();
        PlatformStorageValidationAdapter port = CreatePort(probe);

        StorageValidationOutcome outcome = await port.ValidateAsync(
            Target(endpoint),
            default);

        Assert.False(outcome.Reachable);
        Assert.Equal("storage.blocked_endpoint", outcome.Code);
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public async Task An_insecure_endpoint_is_refused_unless_the_operator_allows_it()
    {
        var probe = new RecordingProbe();
        PlatformStorageValidationAdapter refusing = CreatePort(probe);

        StorageValidationOutcome refused = await refusing.ValidateAsync(
            Target("http://storage.example.com"),
            default);

        Assert.False(refused.Reachable);
        Assert.Equal("storage.insecure_endpoint", refused.Code);
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public async Task A_public_endpoint_reaches_the_probe()
    {
        var probe = new RecordingProbe();
        PlatformStorageValidationAdapter port = CreatePort(probe);

        StorageValidationOutcome outcome = await port.ValidateAsync(
            Target("https://93.184.216.34"),
            default);

        Assert.True(outcome.Reachable);
        Assert.True(probe.WasCalled);
        Assert.Equal("s3", probe.Target!.Provider);
    }

    [Fact]
    public async Task A_provider_exception_never_escapes_as_text()
    {
        var probe = new ThrowingProbe(
            "connect to https://storage.internal:9000 failed for AKIASECRET");
        PlatformStorageValidationAdapter port = CreatePort(probe);

        StorageValidationOutcome outcome = await port.ValidateAsync(
            Target("https://93.184.216.34"),
            default);

        Assert.False(outcome.Reachable);
        Assert.Equal("storage.unreachable", outcome.Code);
        Assert.DoesNotContain("AKIASECRET", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "storage.internal",
            outcome.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_probe_propagates_instead_of_reporting_success()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        PlatformStorageValidationAdapter port = CreatePort(new RecordingProbe());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await port.ValidateAsync(
                Target("https://93.184.216.34"),
                cancellation.Token));
    }

    [Fact]
    public async Task Validation_writes_nothing_to_the_database()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        long before = await CountRowsAsync(harness, owner.TenantId);
        PlatformStorageValidationAdapter port = CreatePort(new RecordingProbe());

        _ = await port.ValidateAsync(Target("https://93.184.216.34"), default);
        _ = await port.ValidateAsync(Target("https://10.0.0.1"), default);

        Assert.Equal(before, await CountRowsAsync(harness, owner.TenantId));
    }

    [Fact]
    public void The_target_carries_no_credential_member()
    {
        Assert.DoesNotContain(
            typeof(StorageValidationTarget)
                .GetProperties()
                .Select(property => property.Name),
            name =>
                name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask<long> CountRowsAsync(
        AccountSurfaceHarness harness,
        Guid tenantId)
    {
        await using VistaraDbContext context = harness.CreateContext(tenantId);
        return await context.AuditEvents.CountAsync(default) +
            await context.Tenants.CountAsync(default) +
            await context.Blobs.CountAsync(default);
    }

    private static StorageValidationTarget Target(string endpoint) =>
        new(
            StorageCandidateKind.S3,
            "s3",
            null,
            new Uri(endpoint),
            "private-media");

    private static PlatformStorageValidationAdapter CreatePort(IStorageValidationProbe probe) =>
        new PlatformStorageValidationAdapter(
            probe,
            Options.Create(new MediaOptions()));

    private sealed class RecordingProbe : IStorageValidationProbe
    {
        public bool WasCalled { get; private set; }

        public StorageValidationTarget? Target { get; private set; }

        public ValueTask<StorageValidationOutcome> ProbeAsync(
            StorageValidationTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            Target = target;
            return ValueTask.FromResult(StorageValidationOutcome.Reached);
        }
    }

    private sealed class ThrowingProbe(string message) : IStorageValidationProbe
    {
        public ValueTask<StorageValidationOutcome> ProbeAsync(
            StorageValidationTarget target,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
    }
}
