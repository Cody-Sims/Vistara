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
        ["reachable", "provider", "code", "message"];

    private const string ValidS3 =
        """
        {"provider":"s3","s3":{"bucketName":"private-media","region":"eu-central-1",
         "serviceUrl":"https://storage.example.com","forcePathStyle":true}}
        """;

    [Fact]
    public async Task A_successful_probe_answers_the_fixed_shape_only()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(port, ValidS3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal(
            ResponseMembers,
            json.RootElement.EnumerateObject().Select(member => member.Name).ToArray());
        Assert.True(json.RootElement.GetProperty("reachable").GetBoolean());
        Assert.Equal("s3", json.RootElement.GetProperty("provider").GetString());
        Assert.Equal("s3", port.Target!.Provider);
        Assert.Equal("private-media", port.Target.Container);
    }

    [Fact]
    public async Task A_secret_is_never_echoed_and_never_reaches_the_probe()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            """
            {"provider":"s3","s3":{"bucketName":"private-media","region":"eu-central-1",
             "serviceUrl":"https://storage.example.com",
             "accessKeyId":"AKIAEXAMPLESECRET","secretAccessKey":"super-secret-value",
             "sessionToken":"session-secret"}}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (string secret in new[]
                 {
                     "AKIAEXAMPLESECRET",
                     "super-secret-value",
                     "session-secret",
                 })
        {
            Assert.DoesNotContain(
                secret,
                response.Body,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotNull(port.Target);
        string serialized = JsonSerializer.Serialize(port.Target);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_provider_failure_message_is_replaced_by_a_stable_code()
    {
        var port = new FakeValidationPort
        {
            Outcome = new StorageValidationOutcome(
                false,
                "storage.unreachable",
                "The storage target did not answer."),
        };

        TestResponse response = await SendAsync(port, ValidS3);

        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.False(json.RootElement.GetProperty("reachable").GetBoolean());
        Assert.Equal(
            "storage.unreachable",
            json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(
            "storage.example.com",
            response.Body,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"provider":"s3"}""")]
    [InlineData("""{"provider":"s3","filesystem":{"rootPath":"/srv"},"s3":{"bucketName":"a-bucket","region":"eu","serviceUrl":"https://a.example.com"}}""")]
    [InlineData("""{"provider":"gcs","s3":{"bucketName":"a-bucket","region":"eu","serviceUrl":"https://a.example.com"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucketName":"A","region":"eu","serviceUrl":"https://a.example.com"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucketName":"a-bucket","region":"eu","serviceUrl":"ftp://a.example.com"}}""")]
    [InlineData("""{"provider":"s3","s3":{"bucketName":"a-bucket","region":"eu","serviceUrl":"https://user:pw@a.example.com"}}""")]
    [InlineData("""{"provider":"filesystem","filesystem":{"rootPath":"relative/path"}}""")]
    [InlineData("""{"provider":"filesystem","filesystem":{"rootPath":"/srv/../etc"}}""")]
    [InlineData("""{"provider":"azure","azure":{"accountName":"acct","containerName":"c","serviceUri":"https://a.example.com"}}""")]
    public async Task An_unacceptable_candidate_is_refused_before_any_probe(string body)
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(port, body);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Null(port.Target);
    }

    [Fact]
    public async Task A_malformed_body_is_refused()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(port, "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(port.Target);
    }

    [Fact]
    public async Task Anonymous_callers_never_reach_the_probe()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            ValidS3,
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(port.Target);
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
        Assert.Null(port.Target);
    }

    [Fact]
    public async Task A_throttled_caller_is_told_to_retry_without_probing()
    {
        var port = new FakeValidationPort();

        TestResponse response = await SendAsync(
            port,
            ValidS3,
            rateLimit: new DenyingRateLimitHook());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("30", response.RetryAfter);
        Assert.Null(port.Target);
    }

    [Fact]
    public async Task A_probe_that_never_answers_is_reported_as_a_timeout()
    {
        var port = new HangingValidationPort();

        TestResponse response = await SendAsync(port, ValidS3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.False(json.RootElement.GetProperty("reachable").GetBoolean());
        Assert.Equal(
            "storage.timed_out",
            json.RootElement.GetProperty("code").GetString());
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
                candidate.RoutePattern.RawText == "/api/v1/admin/storage/validate");
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

    private sealed class FakeValidationPort : IStorageValidationPort
    {
        public StorageValidationTarget? Target { get; private set; }

        public StorageValidationOutcome? Outcome { get; init; }

        public ValueTask<StorageValidationOutcome> ValidateAsync(
            StorageValidationTarget target,
            CancellationToken cancellationToken)
        {
            Target = target;
            return ValueTask.FromResult(
                Outcome ?? StorageValidationOutcome.Reached);
        }
    }

    private sealed class HangingValidationPort : IStorageValidationPort
    {
        public async ValueTask<StorageValidationOutcome> ValidateAsync(
            StorageValidationTarget target,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return StorageValidationOutcome.Reached;
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
