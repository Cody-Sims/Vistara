using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Jobs;
using Vistara.Application.Jobs;
using Vistara.Domain.Jobs;
using Xunit;

namespace Vistara.Api.ContractTests.Jobs;

public sealed class JobStatusEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009f2");

    private static readonly Guid JobIdentifier =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000a01");

    [Fact]
    public void Mapping_registers_the_authenticated_versioned_detail_route()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraJobStatus();
        WebApplication app = builder.Build();

        app.MapVistaraJobStatus();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/jobs/{jobId:guid}");
        Assert.Equal("/api/v1/jobs/{jobId:guid}", endpoint.RoutePattern.RawText);
        Assert.Contains(
            "GET",
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        IAuthorizeData authorization =
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(JobStatusEndpointMapping.PolicyName, authorization.Policy);
    }

    [Fact]
    public async Task Authorized_reads_return_the_redacted_job_state()
    {
        var reader = new FakeJobStatusReader(Snapshot(JobState.Leased));

        TestResponse response = await SendAsync(JobIdentifier, reader);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal("\"v4\"", response.ETag);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement root = json.RootElement;
        Assert.Equal(JobIdentifier, root.GetProperty("id").GetGuid());
        Assert.Equal("asset.ingest", root.GetProperty("type").GetString());
        Assert.Equal("leased", root.GetProperty("state").GetString());
        Assert.Equal(2, root.GetProperty("attempts").GetInt32());
        Assert.Equal(5, root.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(4, root.GetProperty("version").GetInt64());
        Assert.Equal(TenantId, reader.RequestedTenantId);
        Assert.Equal(JobIdentifier, reader.RequestedJobId);
        foreach (string leaked in new[] { "payload", "traceParent", "leaseOwner" })
        {
            Assert.False(
                root.TryGetProperty(leaked, out _),
                $"The job status response must not expose '{leaked}'.");
        }

        Assert.DoesNotContain(
            "worker-01",
            response.Body,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "assetId",
            response.Body,
            StringComparison.Ordinal);
        Assert.False(root.GetProperty("actions").GetProperty("cancel").GetBoolean());
    }

    [Fact]
    public async Task Dead_lettered_jobs_report_the_stable_failure_code()
    {
        var reader = new FakeJobStatusReader(Snapshot(JobState.DeadLettered));

        TestResponse response = await SendAsync(JobIdentifier, reader);

        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement failure = json.RootElement.GetProperty("failure");
        Assert.Equal("deadLettered", json.RootElement.GetProperty("state").GetString());
        Assert.Equal("jobs.media_decode_failed", failure.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("summary").GetString()));
    }

    [Fact]
    public async Task Unknown_and_cross_tenant_jobs_are_concealed_as_not_found()
    {
        var reader = new FakeJobStatusReader(null);

        TestResponse response = await SendAsync(JobIdentifier, reader);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal("jobs_not_found", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reads_are_scoped_to_the_authenticated_tenant()
    {
        var reader = new FakeJobStatusReader(null);

        TestResponse response = await SendAsync(
            JobIdentifier,
            reader,
            tenantId: OtherTenantId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(OtherTenantId, reader.RequestedTenantId);
    }

    [Fact]
    public async Task Jobs_owned_by_another_tenant_are_concealed()
    {
        var reader = new FakeJobStatusReader(Snapshot(JobState.Completed));

        TestResponse response = await SendAsync(
            JobIdentifier,
            reader,
            tenantId: OtherTenantId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal("jobs_not_found", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected_before_any_lookup()
    {
        var reader = new FakeJobStatusReader(Snapshot(JobState.Pending));

        TestResponse response = await SendAsync(
            JobIdentifier,
            reader,
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(reader.RequestedJobId);
    }

    [Fact]
    public async Task Authenticated_callers_without_read_scope_are_forbidden()
    {
        var reader = new FakeJobStatusReader(Snapshot(JobState.Pending));

        TestResponse response = await SendAsync(
            JobIdentifier,
            reader,
            principal: new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", TenantId.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, TenantId.ToString("D")),
                ],
                "test")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(reader.RequestedJobId);
    }

    [Fact]
    public async Task Non_uuid7_job_identifiers_are_concealed()
    {
        var reader = new FakeJobStatusReader(Snapshot(JobState.Pending));

        TestResponse response = await SendAsync(
            Guid.Parse("01990a2a-bc00-4000-8000-000000000a01"),
            reader);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(reader.RequestedJobId);
    }

    [Fact]
    public async Task Unchanged_versions_answer_conditional_requests_with_not_modified()
    {
        var reader = new FakeJobStatusReader(Snapshot(JobState.Leased));

        TestResponse first = await SendAsync(JobIdentifier, reader);
        TestResponse second = await SendAsync(
            JobIdentifier,
            reader,
            configure: context => context.Request.Headers.IfNoneMatch = first.ETag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(string.Empty, second.Body);
    }

    [Fact]
    public async Task Request_cancellation_is_forwarded_to_the_reader()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var reader = new FakeJobStatusReader(Snapshot(JobState.Pending));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(
                JobIdentifier,
                reader,
                authorizationPort: new PermissiveJobStatusAuthorizationPort(TenantId),
                cancellationToken: cancellation.Token));

        Assert.True(reader.CancellationObserved);
    }

    private static JobSnapshot Snapshot(JobState state)
    {
        DateTimeOffset created = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        return new JobSnapshot(
            new JobId(JobIdentifier),
            new JobTenantId(TenantId),
            new JobType("asset.ingest"),
            """{"assetId":"01990a2a-bc00-7000-8000-000000000b01"}""",
            1,
            new JobDedupeKey("asset.ingest:01990a2a-bc00-7000-8000-000000000b01"),
            0,
            5,
            created,
            created,
            "00-11111111111111111111111111111111-2222222222222222-01",
            state,
            2,
            new JobVersion(4),
            state == JobState.Leased
                ? new JobLease(
                    new JobId(JobIdentifier),
                    new JobLeaseOwner("worker-01"),
                    created,
                    created.AddMinutes(5),
                    new JobVersion(4))
                : null,
            state == JobState.DeadLettered
                ? new JobFailure(JobFailureReason.MediaDecodeFailed)
                : null,
            state == JobState.Completed ? created.AddMinutes(1) : null);
    }

    private static async Task<TestResponse> SendAsync(
        Guid jobId,
        IJobStatusReader reader,
        Guid? tenantId = null,
        ClaimsPrincipal? principal = null,
        IJobStatusAuthorizationPort? authorizationPort = null,
        Action<HttpContext>? configure = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(reader);
        if (authorizationPort is not null)
        {
            builder.Services.AddSingleton(authorizationPort);
        }

        builder.Services.AddVistaraJobStatus();
        WebApplication app = builder.Build();
        app.MapVistaraJobStatus();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/jobs/{jobId:guid}");
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", (tenantId ?? TenantId).ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, TenantId.ToString("D")),
                    new Claim("scope", "assets.read"),
                ],
                "test")),
        };
        context.Request.RouteValues["jobId"] = jobId.ToString("D");
        context.Response.Body = new MemoryStream();
        configure?.Invoke(context);

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.ETag.ToString(),
            body);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        string CacheControl,
        string ETag,
        string Body);

    private sealed class PermissiveJobStatusAuthorizationPort(Guid tenantId)
        : IJobStatusAuthorizationPort
    {
        public ValueTask<JobAccess> AuthorizeAsync(
            HttpContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JobAccess.Authorized(tenantId));
    }

    private sealed class FakeJobStatusReader(JobSnapshot? snapshot) : IJobStatusReader
    {
        public Guid? RequestedTenantId { get; private set; }

        public Guid? RequestedJobId { get; private set; }

        public bool CancellationObserved { get; private set; }

        public ValueTask<JobSnapshot?> FindAsync(
            Guid tenantId,
            JobId id,
            CancellationToken cancellationToken)
        {
            CancellationObserved = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            RequestedTenantId = tenantId;
            RequestedJobId = id.Value;
            return ValueTask.FromResult(snapshot);
        }
    }
}
