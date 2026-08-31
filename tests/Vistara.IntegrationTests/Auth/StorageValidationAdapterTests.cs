using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Media;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Admin;
using Vistara.Persistence;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Exercises the shipped storage validation adapter with a fake provider client
/// factory: network policy, credential handling, redaction, absence of
/// persistence, and disposal on cancellation.
/// </summary>
public sealed class StorageValidationAdapterTests
{
    private const string AccessKeySentinel = "AKIAINTEGRATIONSENTINEL";
    private const string SecretSentinel = "integration-secret-sentinel";
    private const string SessionSentinel = "integration-session-sentinel";
    private const string AccountKeySentinel = "integration-account-key-sentinel";

    private static readonly string[] Sentinels =
    [
        AccessKeySentinel,
        SecretSentinel,
        SessionSentinel,
        AccountKeySentinel,
    ];

    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.1.2.3")]
    [InlineData("https://192.168.4.5")]
    [InlineData("https://172.16.9.9")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fd00::1]")]
    [InlineData("https://100.64.1.1")]
    public async Task A_private_or_link_local_endpoint_never_builds_a_client(
        string endpoint)
    {
        var factory = new RecordingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate = S3Candidate(endpoint);
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(
            StorageValidationDetails.EndpointRejected,
            outcome.Checks[0].Detail);
        Assert.False(factory.WasCalled);
    }

    [Fact]
    public async Task An_insecure_endpoint_never_builds_a_client()
    {
        var factory = new RecordingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate =
            S3Candidate("http://93.184.216.34");
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(
            StorageValidationDetails.EndpointRejected,
            outcome.Checks[0].Detail);
        Assert.False(factory.WasCalled);
    }

    [Fact]
    public async Task The_submitted_credential_reaches_only_the_client_factory()
    {
        var factory = new RecordingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate =
            S3Candidate("https://93.184.216.34");
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.True(outcome.Valid);
        Assert.Equal(AccessKeySentinel, factory.RevealedAccessKeyId);
        Assert.Equal(SecretSentinel, factory.RevealedSecretAccessKey);
        Assert.Equal(SessionSentinel, factory.RevealedSessionToken);
        AssertNoSentinel(Render(outcome));
        AssertNoSentinel(candidate.ToString());
    }

    [Fact]
    public async Task Managed_identity_is_validated_without_any_secret()
    {
        var factory = new RecordingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(
            factory,
            trustedHost: "vistaramedia.blob.core.windows.net");

        using var candidate = new StorageValidationCandidate(
            StorageCandidateKind.AzureBlob,
            "azureBlob",
            endpoint: new Uri("https://vistaramedia.blob.core.windows.net"),
            container: "private-media",
            accountName: "vistaramedia",
            azureCredential: AzureCredentialKind.ManagedIdentity);
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.True(outcome.Valid);
        Assert.True(factory.WasCalled);
        Assert.Null(factory.RevealedAccountKey);
        Assert.Equal(AzureCredentialKind.ManagedIdentity, factory.AzureCredential);
    }

    [Fact]
    public async Task An_azure_account_key_reaches_only_the_client_factory()
    {
        var factory = new RecordingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(
            factory,
            trustedHost: "vistaramedia.blob.core.windows.net");

        using var candidate = new StorageValidationCandidate(
            StorageCandidateKind.AzureBlob,
            "azureBlob",
            endpoint: new Uri("https://vistaramedia.blob.core.windows.net"),
            container: "private-media",
            accountName: "vistaramedia",
            azureCredential: AzureCredentialKind.AccountKey,
            accountKey: RedactedSecret.From(AccountKeySentinel));
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.True(outcome.Valid);
        Assert.Equal(AccountKeySentinel, factory.RevealedAccountKey);
        AssertNoSentinel(Render(outcome));
    }

    [Fact]
    public async Task An_anonymous_s3_candidate_is_refused_unless_the_host_is_trusted()
    {
        var factory = new RecordingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using var candidate = new StorageValidationCandidate(
            StorageCandidateKind.S3,
            "s3",
            endpoint: new Uri("https://93.184.216.34"),
            container: "private-media",
            region: "eu-central-1",
            s3Credential: S3CredentialKind.Anonymous);
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(
            StorageValidationDetails.CredentialMissing,
            outcome.Checks[0].Detail);
        Assert.False(factory.WasCalled);
    }

    [Fact]
    public async Task An_invalid_credential_yields_a_stable_redacted_answer()
    {
        var factory = new RecordingClientFactory
        {
            Outcome = Denied(),
        };
        PlatformStorageValidationAdapter port = CreatePort(factory);

        StorageValidationOutcome first;
        StorageValidationOutcome second;
        using (StorageValidationCandidate one = S3Candidate("https://93.184.216.34"))
        {
            first = await port.ValidateAsync(one, default);
        }

        using (StorageValidationCandidate two = S3Candidate("https://93.184.216.34"))
        {
            second = await port.ValidateAsync(two, default);
        }

        Assert.Equal(Render(first), Render(second));
        Assert.False(first.Valid);
        Assert.Equal(
            StorageValidationDetails.CredentialRejected,
            first.Checks[1].Detail);
        AssertNoSentinel(Render(first));
    }

    [Fact]
    public async Task A_provider_exception_never_escapes_as_text()
    {
        var factory = new ThrowingClientFactory(
            $"connect to https://storage.internal:9000 failed for {AccessKeySentinel}");
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate =
            S3Candidate("https://93.184.216.34");
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(StorageValidationDetails.Unreachable, outcome.Checks[0].Detail);
        AssertNoSentinel(Render(outcome));
        Assert.DoesNotContain(
            "storage.internal",
            Render(outcome),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_while_building_the_client_is_reported_as_rejected()
    {
        var factory = new FailingConstructionFactory(AccessKeySentinel);
        PlatformStorageValidationAdapter port = CreatePort(factory);

        using StorageValidationCandidate candidate =
            S3Candidate("https://93.184.216.34");
        StorageValidationOutcome outcome = await port.ValidateAsync(candidate, default);

        Assert.False(outcome.Valid);
        Assert.Equal(
            StorageValidationDetails.CredentialRejected,
            outcome.Checks[0].Detail);
        AssertNoSentinel(Render(outcome));
    }

    [Fact]
    public async Task A_cancelled_validation_disposes_the_client_and_propagates()
    {
        var factory = new RecordingClientFactory { Hang = true };
        PlatformStorageValidationAdapter port = CreatePort(factory);
        using var cancellation = new CancellationTokenSource();

        using StorageValidationCandidate candidate =
            S3Candidate("https://93.184.216.34");
        Task probing = Task.Run(
            async () => await port.ValidateAsync(candidate, cancellation.Token),
            CancellationToken.None);
        await factory.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probing);
        Assert.True(factory.Client!.Disposed);
    }

    [Fact]
    public async Task A_candidate_disposed_after_validation_cannot_reveal_a_secret()
    {
        var factory = new RecordingClientFactory();
        PlatformStorageValidationAdapter port = CreatePort(factory);
        StorageValidationCandidate candidate = S3Candidate("https://93.184.216.34");

        _ = await port.ValidateAsync(candidate, default);
        candidate.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => candidate.SecretAccessKey!.Reveal());
    }

    [Fact]
    public async Task Validation_writes_nothing_to_the_database()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        long before = await CountRowsAsync(harness, owner.TenantId);
        PlatformStorageValidationAdapter port = CreatePort(new RecordingClientFactory());

        using (StorageValidationCandidate reachable =
            S3Candidate("https://93.184.216.34"))
        {
            _ = await port.ValidateAsync(reachable, default);
        }

        using (StorageValidationCandidate blocked = S3Candidate("https://10.0.0.1"))
        {
            _ = await port.ValidateAsync(blocked, default);
        }

        Assert.Equal(before, await CountRowsAsync(harness, owner.TenantId));
        await AssertNoSentinelInDatabaseAsync(harness, owner.TenantId);
    }

    [Fact]
    public async Task A_request_through_the_real_surface_logs_and_traces_no_secret()
    {
        var factory = new RecordingClientFactory();
        var logs = new CapturingLoggerProvider();
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await using AccountSurfaceHarness harness = await AccountSurfaceHarness.CreateAsync(
            services =>
            {
                services.AddSingleton<IStorageValidationClientFactory>(factory);
                services.AddSingleton<ILoggerProvider>(logs);
                services.Configure<MediaOptions>(_ => { });
            });
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        await using AsyncServiceScope scope = harness.CreateTenantScope(owner.TenantId);
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = Owner(owner.TenantId, owner.UserId),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(
            "{\"provider\":\"s3\",\"s3\":{\"bucket\":\"private-media\",",
            "\"region\":\"eu-central-1\",\"endpoint\":\"https://93.184.216.34\",",
            "\"forcePathStyle\":true,\"accessKeyId\":\"",
            AccessKeySentinel,
            "\",\"secretAccessKey\":\"",
            SecretSentinel,
            "\"}}")));
        context.Response.Body = new MemoryStream();

        await StorageValidationEndpoint.ValidateAsync(
            context,
            scope.ServiceProvider.GetRequiredService<IAccountAuthorizationPort>(),
            scope.ServiceProvider.GetRequiredService<IStorageValidationPort>(),
            scope.ServiceProvider.GetRequiredService<IPlatformRateLimitHook>(),
            default);

        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(AccessKeySentinel, factory.RevealedAccessKeyId);
        AssertNoSentinel(body);
        AssertNoSentinel(logs.Text);
        AssertNoSentinel(string.Join(
            '\n',
            activities.Select(activity => string.Join(
                ';',
                activity.Tags.Select(tag => $"{tag.Key}={tag.Value}")))));
        await AssertNoSentinelInDatabaseAsync(harness, owner.TenantId);
    }

    [Fact]
    public void The_shipped_surface_resolves_the_client_factory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVistaraAdministration();
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        Assert.IsType<PlatformStorageValidationClientFactory>(
            provider.GetRequiredService<IStorageValidationClientFactory>());
    }

    private static StorageValidationOutcome Denied()
    {
        var recorder = new StorageProbeRecorder();
        recorder.Pass(StorageCheckId.Reachable);
        return recorder.Fail(
            StorageCheckId.Authenticated,
            StorageValidationDetails.CredentialRejected,
            StorageValidationDetails.RejectedMessage);
    }

    private static ClaimsPrincipal Owner(Guid tenantId, Guid userId) =>
        new(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString("D")),
                new Claim(ClaimTypes.Role, "TenantOwner"),
                new Claim("vistara_auth_kind", "Cookie"),
                new Claim("scope", "quotas.manage"),
            ],
            "test"));

    private static string Render(StorageValidationOutcome outcome) =>
        $"{outcome.Valid}|{outcome.Message}|" + string.Join(
            ',',
            outcome.Checks.Select(check =>
                $"{check.Id}:{check.Status}:{check.Detail}"));

    private static void AssertNoSentinel(string text)
    {
        foreach (string sentinel in Sentinels)
        {
            Assert.DoesNotContain(sentinel, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AssertNoSentinelInDatabaseAsync(
        AccountSurfaceHarness harness,
        Guid tenantId)
    {
        await using VistaraDbContext context = harness.CreateContext(tenantId);
        string audit = string.Join(
            '\n',
            await context.AuditEvents
                .Select(entry =>
                    entry.Action + " " + entry.BeforeJson + " " + entry.AfterJson)
                .ToListAsync(default));
        AssertNoSentinel(audit);
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

    private static StorageValidationCandidate S3Candidate(string endpoint) =>
        new(
            StorageCandidateKind.S3,
            "s3",
            endpoint: new Uri(endpoint),
            container: "private-media",
            region: "eu-central-1",
            forcePathStyle: true,
            s3Credential: S3CredentialKind.AccessKey,
            accessKeyId: RedactedSecret.From(AccessKeySentinel),
            secretAccessKey: RedactedSecret.From(SecretSentinel),
            sessionToken: RedactedSecret.From(SessionSentinel));

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
    /// Stands in for the real provider clients and records the credential it was
    /// given, so the tests can prove the secret reached here and nowhere else.
    /// </summary>
    private sealed class RecordingClientFactory : IStorageValidationClientFactory
    {
        public bool WasCalled { get; private set; }

        public bool Hang { get; init; }

        public StorageValidationOutcome? Outcome { get; init; }

        public AzureCredentialKind? AzureCredential { get; private set; }

        public string? RevealedAccessKeyId { get; private set; }

        public string? RevealedSecretAccessKey { get; private set; }

        public string? RevealedSessionToken { get; private set; }

        public string? RevealedAccountKey { get; private set; }

        public RecordingClient? Client { get; private set; }

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IStorageValidationClient> CreateAsync(
            StorageValidationCandidate candidate,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            AzureCredential = candidate.AzureCredential;
            RevealedAccessKeyId = candidate.AccessKeyId?.Reveal();
            RevealedSecretAccessKey = candidate.SecretAccessKey?.Reveal();
            RevealedSessionToken = candidate.SessionToken?.Reveal();
            RevealedAccountKey = candidate.AccountKey?.Reveal();
            Client = new RecordingClient(Hang, Outcome, Entered);
            return ValueTask.FromResult<IStorageValidationClient>(Client);
        }
    }

    private sealed class RecordingClient(
        bool hang,
        StorageValidationOutcome? outcome,
        TaskCompletionSource entered) : IStorageValidationClient
    {
        public bool Disposed { get; private set; }

        public async ValueTask<StorageValidationOutcome> ProbeAsync(
            string probeKey,
            CancellationToken cancellationToken)
        {
            Assert.StartsWith(
                StorageProbeNaming.Prefix,
                probeKey,
                StringComparison.Ordinal);
            entered.TrySetResult();
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (outcome is not null)
            {
                return outcome;
            }

            var recorder = new StorageProbeRecorder();
            recorder.Pass(StorageCheckId.Reachable);
            recorder.Pass(StorageCheckId.Authenticated);
            recorder.Pass(StorageCheckId.Read);
            recorder.Pass(StorageCheckId.Write);
            recorder.Pass(StorageCheckId.Delete);
            return recorder.Complete(StorageValidationDetails.ValidMessage);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingClientFactory(string message)
        : IStorageValidationClientFactory
    {
        public ValueTask<IStorageValidationClient> CreateAsync(
            StorageValidationCandidate candidate,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IStorageValidationClient>(
                new ThrowingClient(message));
    }

    private sealed class ThrowingClient(string message) : IStorageValidationClient
    {
        public ValueTask<StorageValidationOutcome> ProbeAsync(
            string probeKey,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingConstructionFactory(string message)
        : IStorageValidationClientFactory
    {
        public ValueTask<IStorageValidationClient> CreateAsync(
            StorageValidationCandidate candidate,
            CancellationToken cancellationToken) =>
            throw new FormatException(message);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines =
            new();

        public string Text => string.Join('\n', _lines);

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(_lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lines.Enqueue($"{formatter(state, exception)} {exception}");
            }
        }
    }
}
