using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Admin;
using Xunit;

namespace Vistara.Api.ContractTests.Account;

public sealed class StorageValidationEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid UserId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000701");

    private static readonly string[] ResponseMembers =
        ["valid", "provider", "checks", "message"];

    private static readonly string[] SupportedProviders =
        ["filesystem", "azureBlob", "s3"];

    private static readonly string[] CheckIds =
        ["reachable", "authenticated", "read", "write", "delete"];

    private static readonly string[] Sentinels =
    [
        "AKIAEXAMPLESENTINEL",
        "s3-secret-sentinel-value",
        "session-sentinel-value",
        "azure-account-key-sentinel",
        "sv=sas-sentinel-value",
    ];

    private const string ValidS3 =
        """
        {"provider":"s3","s3":{"bucket":"private-media","region":"eu-central-1",
         "endpoint":"https://storage.example.com","forcePathStyle":true,
         "accessKeyId":"AKIAEXAMPLESENTINEL",
         "secretAccessKey":"s3-secret-sentinel-value"}}
        """;

    [Fact]
    public async Task A_successful_validation_answers_the_agreed_shape_only()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(port, ValidS3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal(
            ResponseMembers,
            json.RootElement.EnumerateObject().Select(member => member.Name).ToArray());
        Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal("s3", json.RootElement.GetProperty("provider").GetString());
        Assert.Equal(
            CheckIds,
            json.RootElement.GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("id").GetString())
                .ToArray());
        Assert.All(
            json.RootElement.GetProperty("checks").EnumerateArray(),
            check => Assert.Equal("passed", check.GetProperty("status").GetString()));
        Assert.Equal("s3", port.Provider);
        Assert.Equal("private-media", port.Container);
    }

    [Fact]
    public async Task The_submitted_credential_reaches_the_port_but_never_the_response()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            """
            {"provider":"s3","s3":{"bucket":"private-media","region":"eu-central-1",
             "endpoint":"https://storage.example.com",
             "accessKeyId":"AKIAEXAMPLESENTINEL",
             "secretAccessKey":"s3-secret-sentinel-value",
             "sessionToken":"session-sentinel-value"}}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AKIAEXAMPLESENTINEL", port.RevealedAccessKeyId);
        Assert.Equal("s3-secret-sentinel-value", port.RevealedSecretAccessKey);
        Assert.Equal("session-sentinel-value", port.RevealedSessionToken);
        AssertNoSentinel(response.Body);
        AssertNoSentinel(port.CandidateText!);
    }

    [Fact]
    public async Task An_azure_account_key_reaches_the_port_but_never_the_response()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            """
            {"provider":"azureBlob","azureBlob":{"accountName":"vistaramedia",
             "container":"private-media","credentialKind":"accountKey",
             "accountKey":"azure-account-key-sentinel"}}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AzureCredentialKind.AccountKey, port.AzureCredential);
        Assert.Equal("azure-account-key-sentinel", port.RevealedAccountKey);
        Assert.Equal(
            "https://vistaramedia.blob.core.windows.net/",
            port.Endpoint!.ToString());
        AssertNoSentinel(response.Body);
        AssertNoSentinel(port.CandidateText!);
    }

    [Fact]
    public async Task Managed_identity_is_accepted_without_any_secret()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            """
            {"provider":"azureBlob","azureBlob":{"accountName":"vistaramedia",
             "container":"private-media","credentialKind":"managedIdentity"}}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AzureCredentialKind.ManagedIdentity, port.AzureCredential);
        Assert.Null(port.RevealedAccountKey);
        Assert.Null(port.RevealedSasToken);
    }

    [Fact]
    public async Task A_sas_token_is_accepted_as_a_credential_kind()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            """
            {"provider":"azureBlob","azureBlob":{"accountName":"vistaramedia",
             "container":"private-media","credentialKind":"sasToken",
             "sasToken":"sv=sas-sentinel-value"}}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AzureCredentialKind.SasToken, port.AzureCredential);
        Assert.Equal("sv=sas-sentinel-value", port.RevealedSasToken);
        AssertNoSentinel(response.Body);
    }

    [Fact]
    public async Task The_credential_is_disposed_once_the_request_ends()
    {
        var port = new FakeValidationPort();

        _ = await SendAsync(port, ValidS3);

        Assert.NotNull(port.Candidate);
        Assert.Throws<ObjectDisposedException>(
            () => port.Candidate!.SecretAccessKey!.Reveal());
    }

    [Fact]
    public async Task Repeated_validations_retain_nothing_between_calls()
    {
        var first = new FakeValidationPort();
        var second = new FakeValidationPort();

        TestResponse one = await SendAsync(first, ValidS3);
        TestResponse two = await SendAsync(
            second,
            """
            {"provider":"s3","s3":{"bucket":"private-media","region":"eu-central-1",
             "endpoint":"https://storage.example.com"}}
            """);

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(S3CredentialKind.Anonymous, second.S3Credential);
        Assert.Null(second.RevealedAccessKeyId);
        AssertNoSentinel(two.Body);
    }

    [Fact]
    public async Task A_failed_check_carries_only_catalogued_detail()
    {
        var port = new FakeValidationPort
        {
            Outcome = StorageValidationOutcome.Rejected(
                StorageValidationDetails.RejectedMessage,
                StorageValidationDetails.CredentialRejected),
        };

        TestResponse response = await SendAsync(port, ValidS3);

        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.False(json.RootElement.GetProperty("valid").GetBoolean());
        JsonElement reachable = json.RootElement.GetProperty("checks")[0];
        Assert.Equal("failed", reachable.GetProperty("status").GetString());
        Assert.Equal(
            StorageValidationDetails.CredentialRejected,
            reachable.GetProperty("detail").GetString());
        Assert.DoesNotContain(
            "storage.example.com",
            response.Body,
            StringComparison.OrdinalIgnoreCase);
        AssertNoSentinel(response.Body);
    }

    [Theory]
    [InlineData("""{"provider":"s3"}""")]
    [InlineData("""{"provider":"s3","filesystem":{"rootPath":"/srv"},"s3":{"bucket":"a-bucket","region":"eu","endpoint":"https://a.example.com"}}""")]
    [InlineData("""{"provider":"gcs","s3":{"bucket":"a-bucket","region":"eu","endpoint":"https://a.example.com"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucket":"A","region":"eu","endpoint":"https://a.example.com"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucket":"a-bucket","region":"eu","endpoint":"ftp://a.example.com"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucket":"a-bucket","region":"eu","endpoint":"******a.example.com"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucket":"a-bucket","region":"eu","endpoint":"https://a.example.com","accessKeyId":"AKIAEXAMPLESENTINEL"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucket":"a-bucket","region":"eu","endpoint":"https://a.example.com","sessionToken":"session-sentinel-value"}}""")]
    [InlineData("""{"provider":"filesystem","filesystem":{"rootPath":"relative/path"}}""")]
    [InlineData("""{"provider":"filesystem","filesystem":{"rootPath":"/srv/../etc"}}""")]
    [InlineData("""{"provider":"azure","azure":{"accountName":"acct","containerName":"c","serviceUri":"https://a.example.com"}}""")]
    [InlineData("""{"provider":"azureBlob","azureBlob":{"accountName":"vistaramedia","container":"private-media","credentialKind":"accountKey"}}""")]
    [InlineData("""{"provider":"azureBlob","azureBlob":{"accountName":"vistaramedia","container":"private-media","credentialKind":"connectionString","connectionString":"x"}}""")]
    [InlineData("""{"provider":"azureBlob","azureBlob":{"accountName":"VistaraMedia","container":"private-media"}}""")]
    public async Task An_unacceptable_candidate_is_refused_before_any_validation(
        string body)
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(port, body);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Null(port.Candidate);
        AssertNoSentinel(response.Body);
    }

    [Fact]
    public async Task A_malformed_body_is_refused()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(port, "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(port.Candidate);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_parsing()
    {
        var port = new FakeValidationPort();
        string padded = string.Concat(
            "{\"provider\":\"s3\",\"s3\":{\"bucket\":\"private-media\",",
            "\"region\":\"eu\",\"endpoint\":\"https://a.example.com\",",
            "\"secretAccessKey\":\"",
            new string('a', StorageValidationEndpoint.MaximumBodyBytes),
            "\"}}");

        TestResponse response = await SendAsync(port, padded);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(port.Candidate);
    }

    [Fact]
    public async Task An_oversized_secret_field_is_refused()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            string.Concat(
                "{\"provider\":\"s3\",\"s3\":{\"bucket\":\"private-media\",",
                "\"region\":\"eu-central-1\",",
                "\"endpoint\":\"https://storage.example.com\",",
                "\"accessKeyId\":\"AKIAEXAMPLESENTINEL\",\"secretAccessKey\":\"",
                new string('b', StorageValidationEndpoint.MaximumSecretLength + 1),
                "\"}}"));

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Null(port.Candidate);
    }

    [Fact]
    public async Task Anonymous_callers_never_reach_the_validation()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            ValidS3,
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(port.Candidate);
    }

    [Theory]
    [InlineData("TenantAdmin", "quotas.manage")]
    [InlineData("TenantOwner", "members.manage")]
    [InlineData("Member", "quotas.manage")]
    public async Task Only_a_tenant_owner_with_the_quota_scope_may_validate(
        string role,
        string scope)
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            ValidS3,
            principal: Principal(role, scope));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(port.Candidate);
    }

    [Fact]
    public async Task A_throttled_caller_is_told_to_retry_without_validating()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            ValidS3,
            rateLimit: new DenyingRateLimitHook());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("30", response.RetryAfter);
        Assert.Null(port.Candidate);
    }

    [Fact]
    public async Task A_validation_that_never_answers_is_reported_as_a_timeout()
    {
        var port = new HangingValidationPort();

        TestResponse response = await SendAsync(port, ValidS3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.False(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(
            StorageValidationDetails.TimedOut,
            json.RootElement.GetProperty("checks")[0].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_cancelled_request_is_not_converted_into_a_timeout()
    {
        var port = new HangingValidationPort();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(port, ValidS3, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void A_redacted_secret_never_prints_its_value()
    {
        using RedactedSecret secret = RedactedSecret.From("s3-secret-sentinel-value")!;

        Assert.Equal("[REDACTED]", secret.ToString());
        Assert.Equal("[REDACTED]", $"{secret}");
        AssertNoSentinel(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}",
            secret));
    }

    [Fact]
    public async Task The_deployment_publishes_that_it_can_test_a_credential()
    {
        TestResponse response = await DescribeAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.True(json.RootElement.GetProperty("supported").GetBoolean());
        Assert.Equal(
            SupportedProviders,
            json.RootElement.GetProperty("providers")
                .EnumerateArray()
                .Select(provider => provider.GetString() ?? string.Empty)
                .ToArray());
    }

    [Fact]
    public async Task Support_is_not_published_to_a_non_owner()
    {
        TestResponse response = await DescribeAsync(Principal("Member", "quotas.manage"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static void AssertNoSentinel(string text)
    {
        foreach (string sentinel in Sentinels)
        {
            Assert.DoesNotContain(sentinel, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ClaimsPrincipal Principal(string role, params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", TenantId.ToString("D")),
            new(ClaimTypes.NameIdentifier, UserId.ToString("D")),
            new(ClaimTypes.Role, role),
            new("vistara_auth_kind", "Cookie"),
        };
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<TestResponse> DescribeAsync(
        ClaimsPrincipal? principal = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IStorageValidationPort>(new FakeValidationPort());
        builder.Services.AddSingleton<IAdminPort>(new UnusedAdminPort());
        builder.Services.AddVistaraAdministration();
        WebApplication app = builder.Build();
        app.MapVistaraAdministration();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/admin/storage/validate" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("GET"));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal ?? Principal("TenantOwner", "quotas.manage"),
        };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.RetryAfter.ToString(),
            responseBody);
    }

    private static async Task<TestResponse> SendAsync(
        IStorageValidationPort port,
        string body,
        ClaimsPrincipal? principal = null,
        IPlatformRateLimitHook? rateLimit = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(port);
        if (rateLimit is not null)
        {
            builder.Services.AddSingleton(rateLimit);
        }

        builder.Services.AddSingleton<IAdminPort>(new UnusedAdminPort());
        builder.Services.AddVistaraAdministration();
        WebApplication app = builder.Build();
        app.MapVistaraAdministration();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/admin/storage/validate" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!
                    .HttpMethods.Contains("POST"));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
            User = principal ?? Principal("TenantOwner", "quotas.manage"),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.RetryAfter.ToString(),
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        string CacheControl,
        string RetryAfter,
        string Body);

    /// <summary>
    /// Stands in for the shipped adapter and records exactly what the endpoint
    /// handed it, including the revealed credential, so the tests can prove the
    /// secret travelled no further.
    /// </summary>
    private sealed class FakeValidationPort : IStorageValidationPort
    {
        public StorageValidationCandidate? Candidate { get; private set; }

        public string? Provider { get; private set; }

        public string? Container { get; private set; }

        public Uri? Endpoint { get; private set; }

        public AzureCredentialKind? AzureCredential { get; private set; }

        public S3CredentialKind? S3Credential { get; private set; }

        public string? RevealedAccessKeyId { get; private set; }

        public string? RevealedSecretAccessKey { get; private set; }

        public string? RevealedSessionToken { get; private set; }

        public string? RevealedAccountKey { get; private set; }

        public string? RevealedSasToken { get; private set; }

        public string? CandidateText { get; private set; }

        public StorageValidationOutcome? Outcome { get; init; }

        public ValueTask<StorageValidationOutcome> ValidateAsync(
            StorageValidationCandidate candidate,
            CancellationToken cancellationToken)
        {
            Candidate = candidate;
            Provider = candidate.Provider;
            Container = candidate.Container;
            Endpoint = candidate.Endpoint;
            AzureCredential = candidate.AzureCredential;
            S3Credential = candidate.S3Credential;
            RevealedAccessKeyId = candidate.AccessKeyId?.Reveal();
            RevealedSecretAccessKey = candidate.SecretAccessKey?.Reveal();
            RevealedSessionToken = candidate.SessionToken?.Reveal();
            RevealedAccountKey = candidate.AccountKey?.Reveal();
            RevealedSasToken = candidate.SasToken?.Reveal();
            CandidateText = candidate.ToString();
            return ValueTask.FromResult(Outcome ?? AllPassed());
        }

        private static StorageValidationOutcome AllPassed()
        {
            var recorder = new StorageProbeRecorder();
            recorder.Pass(StorageCheckId.Reachable);
            recorder.Pass(StorageCheckId.Authenticated);
            recorder.Pass(StorageCheckId.Read);
            recorder.Pass(StorageCheckId.Write);
            recorder.Pass(StorageCheckId.Delete);
            return recorder.Complete(StorageValidationDetails.ValidMessage);
        }
    }

    private sealed class HangingValidationPort : IStorageValidationPort
    {
        public async ValueTask<StorageValidationOutcome> ValidateAsync(
            StorageValidationCandidate candidate,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return StorageValidationOutcome.Rejected("unreachable");
        }
    }

    private sealed class DenyingRateLimitHook : IPlatformRateLimitHook
    {
        public ValueTask<PlatformRateLimitDecision> CheckAsync(
            HttpContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                PlatformRateLimitDecision.Reject(TimeSpan.FromSeconds(30)));
    }

    private sealed class UnusedAdminPort : IAdminPort
    {
        public ValueTask<StorageSummaryView> GetStorageAsync(
            Guid tenantId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Domain.Common.Result<TenantPolicyView>> GetPolicyAsync(
            Guid tenantId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Domain.Common.Result<TenantPolicyView>> UpdatePolicyAsync(
            Guid tenantId,
            Guid actorUserId,
            TenantPolicyPatch patch,
            long expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AuditPage> ReadAuditAsync(
            AuditQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
