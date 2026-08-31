using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Jobs;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Xunit;

namespace Vistara.Api.ContractTests.Jobs;

public sealed class JobAdministrationEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000901");

    private static readonly Guid OtherTenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-0000000009f2");

    private static readonly Guid JobIdentifier =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000a01");

    private static readonly string[] ExpectedStates =
        ["DeadLettered", "RetryScheduled"];

    [Fact]
    public void Mapping_registers_the_collection_and_operator_routes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraJobStatus();
        WebApplication app = builder.Build();

        app.MapVistaraJobStatus();

        string[] routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToArray();
        Assert.Contains("/api/v1/jobs", routes);
        Assert.Contains("/api/v1/jobs/{jobId:guid}", routes);
        Assert.Contains("/api/v1/jobs/{jobId:guid}/retry", routes);
        Assert.Contains("/api/v1/jobs/{jobId:guid}/cancel", routes);
    }

    [Fact]
    public async Task Listing_returns_a_page_with_actions_and_a_cursor()
    {
        var administration = new FakeJobAdministrationPort();

        TestResponse response = await SendAsync("list", administration);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.CacheControl);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement item = Assert.Single(
            json.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(JobIdentifier, item.GetProperty("id").GetGuid());
        Assert.True(item.GetProperty("actions").GetProperty("retry").GetBoolean());
        Assert.False(item.GetProperty("actions").GetProperty("cancel").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            json.RootElement.GetProperty("nextCursor").GetString()));
        Assert.Equal(TenantId, administration.Query!.TenantId);
        Assert.DoesNotContain("payload", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-01", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_accepts_the_published_lower_camel_states()
    {
        var administration = new FakeJobAdministrationPort();

        await SendAsync(
            "list",
            administration,
            query: "?states=deadLettered&states=retryScheduled&type=asset.ingest&limit=25");

        Assert.Equal(ExpectedStates, administration.Query!.States);
        Assert.Equal("asset.ingest", administration.Query.Type);
        Assert.Equal(25, administration.Query.Limit);
    }

    [Fact]
    public async Task Listing_still_accepts_the_legacy_pascal_case_states()
    {
        var administration = new FakeJobAdministrationPort();

        await SendAsync(
            "list",
            administration,
            query: "?states=DeadLettered&states=RetryScheduled");

        Assert.Equal(ExpectedStates, administration.Query!.States);
    }

    [Fact]
    public async Task A_listed_state_matches_the_filter_vocabulary()
    {
        var administration = new FakeJobAdministrationPort();

        TestResponse response = await SendAsync(
            "list",
            administration,
            query: "?states=deadLettered");

        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement item = Assert.Single(
            json.RootElement.GetProperty("items").EnumerateArray().ToArray());
        string state = item.GetProperty("state").GetString()!;
        Assert.Equal("deadLettered", state);

        var echo = new FakeJobAdministrationPort();
        TestResponse round = await SendAsync(
            "list",
            echo,
            query: $"?states={state}");
        Assert.Equal(HttpStatusCode.OK, round.StatusCode);
        Assert.Equal("DeadLettered", Assert.Single(echo.Query!.States));
    }

    [Theory]
    [InlineData("?states=Nope", "states")]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=201", "limit")]
    [InlineData("?limit=abc", "limit")]
    public async Task Listing_rejects_an_invalid_query(string query, string field)
    {
        var administration = new FakeJobAdministrationPort();

        TestResponse response = await SendAsync("list", administration, query: query);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.True(
            problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Null(administration.Query);
    }

    [Fact]
    public async Task A_cursor_from_another_tenant_or_query_is_a_conflict()
    {
        var administration = new FakeJobAdministrationPort();
        TestResponse first = await SendAsync("list", administration);
        using JsonDocument json = JsonDocument.Parse(first.Body);
        string cursor = json.RootElement.GetProperty("nextCursor").GetString()!;

        TestResponse crossTenant = await SendAsync(
            "list",
            administration,
            query: $"?cursor={Uri.EscapeDataString(cursor)}",
            tenantId: OtherTenantId);
        TestResponse crossQuery = await SendAsync(
            "list",
            administration,
            query: $"?type=other&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.Conflict, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, crossQuery.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(crossTenant.Body);
        Assert.Equal(
            "jobs_cursor_mismatch",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_cursor_round_trips_within_the_same_tenant_and_query()
    {
        var administration = new FakeJobAdministrationPort();
        TestResponse first = await SendAsync("list", administration);
        using JsonDocument json = JsonDocument.Parse(first.Body);
        string cursor = json.RootElement.GetProperty("nextCursor").GetString()!;

        TestResponse second = await SendAsync(
            "list",
            administration,
            query: $"?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotNull(administration.Query!.AfterJobId);
        Assert.NotNull(administration.Query.AfterCreatedAtUtc);
    }

    [Fact]
    public async Task Retrying_requires_the_exact_version_and_returns_the_new_tag()
    {
        var administration = new FakeJobAdministrationPort();

        TestResponse response = await SendAsync(
            "retry",
            administration,
            ifMatch: "\"v4\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v5\"", response.ETag);
        Assert.Equal(4, administration.RetryVersion);
        Assert.Equal(JobIdentifier, administration.RetryJobId);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.PreconditionRequired)]
    [InlineData("*", HttpStatusCode.PreconditionRequired)]
    [InlineData("bad", HttpStatusCode.BadRequest)]
    public async Task Retrying_without_a_usable_precondition_is_refused(
        string? ifMatch,
        HttpStatusCode expected)
    {
        var administration = new FakeJobAdministrationPort();

        TestResponse response = await SendAsync(
            "retry",
            administration,
            ifMatch: ifMatch);

        Assert.Equal(expected, response.StatusCode);
        Assert.Null(administration.RetryJobId);
    }

    [Fact]
    public async Task Retrying_a_stale_job_answers_precondition_failed()
    {
        var administration = new FakeJobAdministrationPort
        {
            RetryResult = Result.Failure<JobSnapshot>(ResultError.Conflict(
                "jobs.version_conflict",
                "The job changed since it was read.")),
        };

        TestResponse response = await SendAsync(
            "retry",
            administration,
            ifMatch: "\"v1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Retrying_a_job_that_is_not_retryable_is_a_conflict()
    {
        var administration = new FakeJobAdministrationPort
        {
            RetryResult = Result.Failure<JobSnapshot>(ResultError.Conflict(
                "jobs.not_retryable",
                "Only a dead-lettered or retry-scheduled job can be requeued.")),
        };

        TestResponse response = await SendAsync(
            "retry",
            administration,
            ifMatch: "\"v4\"");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "jobs_not_retryable",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancelling_reports_that_the_action_is_unavailable()
    {
        var administration = new FakeJobAdministrationPort();

        TestResponse response = await SendAsync(
            "cancel",
            administration,
            ifMatch: "\"v4\"");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "jobs_cancel_unsupported",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("retry")]
    [InlineData("cancel")]
    public async Task Anonymous_callers_are_rejected(string operation)
    {
        var administration = new FakeJobAdministrationPort();

        TestResponse response = await SendAsync(
            operation,
            administration,
            ifMatch: "\"v4\"",
            principal: new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(administration.Query);
        Assert.Null(administration.RetryJobId);
    }

    private static async Task<TestResponse> SendAsync(
        string operation,
        IJobAdministrationPort administration,
        string? query = null,
        string? ifMatch = null,
        Guid? tenantId = null,
        ClaimsPrincipal? principal = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(administration);
        builder.Services.AddVistaraJobStatus();
        WebApplication app = builder.Build();
        app.MapVistaraJobStatus();

        string route = operation switch
        {
            "list" => "/api/v1/jobs",
            "retry" => "/api/v1/jobs/{jobId:guid}/retry",
            "cancel" => "/api/v1/jobs/{jobId:guid}/cancel",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", (tenantId ?? TenantId).ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, TenantId.ToString("D")),
                    new Claim("scope", "assets.read"),
                ],
                "test")),
        };
        context.Request.Method = operation == "list" ? HttpMethods.Get : HttpMethods.Post;
        context.Request.RouteValues["jobId"] = JobIdentifier.ToString("D");
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body, Encoding.UTF8)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.ETag.ToString(),
            body);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string CacheControl,
        string ETag,
        string Body);

    private sealed class FakeJobAdministrationPort : IJobAdministrationPort
    {
        public JobListQuery? Query { get; private set; }

        public Guid? RetryJobId { get; private set; }

        public long? RetryVersion { get; private set; }

        public Result<JobSnapshot>? RetryResult { get; init; }

        public ValueTask<JobListPage> ListAsync(
            JobListQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            JobSnapshot snapshot = Snapshot(JobState.DeadLettered, 4);
            return ValueTask.FromResult(new JobListPage(
                [snapshot],
                snapshot.CreatedAtUtc,
                snapshot.Id.Value));
        }

        public ValueTask<Result<JobSnapshot>> RetryAsync(
            Guid tenantId,
            Guid jobId,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            RetryJobId = jobId;
            RetryVersion = expectedVersion;
            return ValueTask.FromResult(
                RetryResult ?? Result.Success(
                    Snapshot(JobState.Pending, expectedVersion + 1)));
        }

        private static JobSnapshot Snapshot(JobState state, long version)
        {
            DateTimeOffset created = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
            return new JobSnapshot(
                new JobId(JobIdentifier),
                new JobTenantId(TenantId),
                new JobType("asset.ingest"),
                """{"assetId":"01990a2a-bc00-7000-8000-000000000b01"}""",
                1,
                new JobDedupeKey("asset.ingest:1"),
                0,
                5,
                created,
                created,
                "00-11111111111111111111111111111111-2222222222222222-01",
                state,
                state == JobState.Pending ? 0 : 5,
                new JobVersion(version),
                null,
                state == JobState.DeadLettered
                    ? new JobFailure(JobFailureReason.MediaDecodeFailed)
                    : null,
                null);
        }
    }
}
