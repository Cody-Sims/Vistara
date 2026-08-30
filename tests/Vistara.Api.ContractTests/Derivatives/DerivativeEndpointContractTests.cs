using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Derivatives;
using Vistara.Contracts.Derivatives;
using Vistara.Contracts.Idempotency;
using Xunit;

namespace Vistara.Api.ContractTests.Derivatives;

public sealed class DerivativeEndpointContractTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000201");
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000202");
    private static readonly Guid RequestId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000203");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

    internal static Guid TenantIdForFakes => TenantId;

    internal static Guid AssetIdForFakes => AssetId;

    [Fact]
    public async Task Preset_catalog_lists_active_and_historical_revisions()
    {
        var application = new FakeDerivativeApplicationPort
        {
            Presets =
            [
                new DerivativePresetDefinition(
                    "viewer",
                    ActiveRevision: 2,
                    [
                        new DerivativePresetRevisionDefinition(
                            Revision: 1,
                            IsActive: false,
                            new DerivativeParameterBounds(
                                1_024,
                                1_600,
                                1_024,
                                1_600,
                                70,
                                90,
                                ["contain"],
                                ["jpeg", "webp"])),
                        new DerivativePresetRevisionDefinition(
                            Revision: 2,
                            IsActive: true,
                            new DerivativeParameterBounds(
                                1_024,
                                2_400,
                                1_024,
                                2_400,
                                70,
                                90,
                                ["contain"],
                                ["jpeg", "webp"])),
                    ]),
            ],
        };

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "GET",
            "/api/v1/derivative-presets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = response.Json();
        JsonElement preset = Assert.Single(
            body.RootElement.GetProperty("presets").EnumerateArray());
        Assert.Equal("viewer", preset.GetProperty("name").GetString());
        Assert.Equal(2, preset.GetProperty("activeRevision").GetInt32());
        Assert.Equal(2, preset.GetProperty("revisions").GetArrayLength());
        Assert.DoesNotContain("fingerprint", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        """{"preset":"missing","revision":1,"parameters":{"width":1024,"height":1024,"format":"webp"}}""",
        DerivativeCanonicalizationStatus.PresetNotFound,
        "derivative_preset_not_allowed")]
    [InlineData(
        """{"preset":"viewer","revision":1,"parameters":{"width":1024,"height":1024,"format":"webp"}}""",
        DerivativeCanonicalizationStatus.RevisionNotActive,
        "derivative_preset_revision_not_active")]
    [InlineData(
        """{"preset":"viewer","revision":2,"parameters":{"width":1500,"height":1500,"format":"webp"}}""",
        DerivativeCanonicalizationStatus.ParametersNotAllowed,
        "derivative_parameters_not_allowed")]
    public async Task Preset_revision_and_policy_failures_are_safe_validation_problems(
        string requestBody,
        DerivativeCanonicalizationStatus status,
        string expectedCode)
    {
        var application = new FakeDerivativeApplicationPort
        {
            Canonicalization = DerivativeCanonicalizationResult.Rejected(status),
        };

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            requestBody,
            idempotencyKey: "request-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Equal(expectedCode, response.Json().RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("recipe", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"preset":"viewer","revision":2,"parameters":{"width":0}}""")]
    [InlineData("""{"preset":"viewer","revision":2,"parameters":{"height":8193}}""")]
    [InlineData("""{"preset":"viewer","revision":2,"parameters":{"quality":101}}""")]
    [InlineData("""{"preset":"viewer","revision":2,"parameters":{"format":"avif"}}""")]
    [InlineData("""{"preset":"viewer","revision":2,"operations":[{"resize":1}]}""")]
    public async Task Invalid_bounds_formats_and_transform_dsl_are_rejected_before_canonicalization(
        string requestBody)
    {
        var application = new FakeDerivativeApplicationPort();

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            requestBody,
            idempotencyKey: "request-2");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("derivative_request_invalid", ProblemCode(response));
        Assert.Equal(0, application.CanonicalizeCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("unicode-\u2603")]
    public async Task Missing_or_malformed_idempotency_keys_are_rejected(string? key)
    {
        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            new FakeDerivativeApplicationPort(),
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKey: key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_idempotency_key", ProblemCode(response));
    }

    [Fact]
    public async Task Oversized_or_repeated_idempotency_keys_are_rejected()
    {
        var application = new FakeDerivativeApplicationPort();
        TestResponse oversized = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKey: new string('a', 129));
        TestResponse repeated = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKeys: ["one", "two"]);

        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(0, application.CanonicalizeCalls);
    }

    [Fact]
    public async Task Queued_request_returns_status_location_version_and_no_store()
    {
        var application = new FakeDerivativeApplicationPort
        {
            Submission = DerivativeSubmissionResult.Accepted(
                Snapshot(DerivativeWorkState.Queued, version: 3)),
        };

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKey: "request-3");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            $"/api/v1/assets/{AssetId:D}/derivatives/{RequestId:D}",
            response.Headers.Location.ToString());
        Assert.Equal("\"v3\"", response.Headers.ETag.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.Equal("queued", response.Json().RootElement.GetProperty("state").GetString());
        Assert.Equal("request-3", application.LastIdempotencyKey?.Value);
        Assert.Equal(1, application.CanonicalizeCalls);
    }

    [Fact]
    public async Task Ready_identical_work_is_reused_without_internal_keys_or_urls()
    {
        var application = new FakeDerivativeApplicationPort
        {
            Submission = DerivativeSubmissionResult.Ready(
                Snapshot(DerivativeWorkState.Ready, version: 5, ready: true),
                reusedExisting: true),
        };

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKey: "request-4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", response.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal("\"v5\"", response.Headers.ETag.ToString());
        Assert.Equal("ready", response.Json().RootElement.GetProperty("state").GetString());
        Assert.Contains("\"contentType\":\"image/webp\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("cacheKey", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signed", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task In_progress_identical_work_is_reused_as_accepted()
    {
        var application = new FakeDerivativeApplicationPort
        {
            Submission = DerivativeSubmissionResult.Accepted(
                Snapshot(DerivativeWorkState.Processing, version: 4),
                reusedExisting: true),
        };

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKey: "request-5");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("true", response.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal("processing", response.Json().RootElement.GetProperty("state").GetString());
        Assert.Equal(
            $"/api/v1/assets/{AssetId:D}/derivatives/{RequestId:D}",
            response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Stable_replay_reuses_the_original_response_and_conflicts_on_changed_request()
    {
        var application = new IdempotentFakeDerivativeApplicationPort();
        var authorization = new FakeDerivativeAuthorizationPort();

        TestResponse first = await SendAsync(
            authorization,
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKey: "stable-key");
        TestResponse replay = await SendAsync(
            authorization,
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody,
            idempotencyKey: "stable-key");
        TestResponse conflict = await SendAsync(
            authorization,
            application,
            "POST",
            "/api/v1/assets/{assetId:guid}/derivatives",
            ValidRequestBody.Replace("1024", "1600", StringComparison.Ordinal),
            idempotencyKey: "stable-key");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.Body, replay.Body);
        Assert.Equal("true", replay.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_key_conflict", ProblemCode(conflict));
    }

    [Fact]
    public async Task List_and_status_endpoints_return_only_authorized_asset_work()
    {
        var application = new FakeDerivativeApplicationPort
        {
            Items =
            [
                Snapshot(DerivativeWorkState.Processing, version: 4),
                Snapshot(
                    DerivativeWorkState.Ready,
                    version: 5,
                    ready: true,
                    requestId: Guid.Parse("01990a2a-bc00-7000-8000-000000000204")),
            ],
        };

        TestResponse list = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets/{assetId:guid}/derivatives");
        TestResponse status = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets/{assetId:guid}/derivatives/{requestId:guid}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(2, list.Json().RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("\"v4\"", status.Headers.ETag.ToString());
        Assert.Equal("processing", status.Json().RootElement.GetProperty("state").GetString());
        Assert.All(application.ObservedScopes, scope => Assert.Equal(AssetId, scope.AssetId));
    }

    [Theory]
    [InlineData(DerivativeAccessStatus.Unauthenticated, HttpStatusCode.Unauthorized)]
    [InlineData(DerivativeAccessStatus.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(DerivativeAccessStatus.Concealed, HttpStatusCode.NotFound)]
    public async Task Authorization_happens_before_asset_existence_or_work_disclosure(
        DerivativeAccessStatus accessStatus,
        HttpStatusCode expectedStatus)
    {
        var authorization = new FakeDerivativeAuthorizationPort
        {
            AssetAccess = DerivativeAccess.Denied(accessStatus),
        };
        var application = new FakeDerivativeApplicationPort();

        TestResponse response = await SendAsync(
            authorization,
            application,
            "GET",
            "/api/v1/assets/{assetId:guid}/derivatives");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(0, application.TotalCalls);
        Assert.DoesNotContain(AssetId.ToString(), response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unexpected_service_errors_are_rfc9457_safe()
    {
        var application = new FakeDerivativeApplicationPort
        {
            Exception = new InvalidOperationException(
                "secret-cache-key derivatives/v1/aa/signed-url"),
        };

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets/{assetId:guid}/derivatives");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Equal("derivative_service_unavailable", ProblemCode(response));
        Assert.DoesNotContain("secret-cache-key", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-url", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_status_sanitizes_non_public_failure_codes()
    {
        var application = new FakeDerivativeApplicationPort
        {
            Items =
            [
                Snapshot(DerivativeWorkState.Failed, version: 6) with
                {
                    FailureCode = "storage/key=derivatives/private",
                },
            ],
        };

        TestResponse response = await SendAsync(
            new FakeDerivativeAuthorizationPort(),
            application,
            "GET",
            "/api/v1/assets/{assetId:guid}/derivatives");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"failureCode\":\"derivative_failed\"", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("storage/key", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_cancellation_is_forwarded_without_becoming_a_problem_response()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var application = new FakeDerivativeApplicationPort();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(
                new FakeDerivativeAuthorizationPort(),
                application,
                "GET",
                "/api/v1/assets/{assetId:guid}/derivatives",
                cancellationToken: cancellation.Token));

        Assert.True(application.CancellationObserved);
    }

    private const string ValidRequestBody =
        """{"preset":"viewer","revision":2,"parameters":{"width":1024,"height":1024,"fit":"contain","format":"webp","quality":82}}""";

    private static string ProblemCode(TestResponse response) =>
        response.Json().RootElement.GetProperty("code").GetString()!;

    private static DerivativeWorkSnapshot Snapshot(
        DerivativeWorkState state,
        long version,
        bool ready = false,
        Guid? requestId = null) =>
        new(
            requestId ?? RequestId,
            "viewer",
            2,
            new CanonicalDerivativeParameters(
                1_024,
                1_024,
                "contain",
                "webp",
                82,
                null,
                null),
            state,
            version,
            Now,
            Now,
            ready
                ? new DerivativeReadyRepresentation(
                    1_024,
                    1_024,
                    "webp",
                    "image/webp",
                    "\"sha256-representation\"")
                : null,
            state == DerivativeWorkState.Failed ? "processing_failed" : null);

    internal static DerivativeWorkSnapshot SnapshotForFakes(
        DerivativeWorkState state,
        long version) =>
        Snapshot(state, version);

    private static async Task<TestResponse> SendAsync(
        IDerivativeAuthorizationPort authorization,
        IDerivativeApplicationPort application,
        string method,
        string route,
        string? body = null,
        string? idempotencyKey = null,
        IReadOnlyList<string>? idempotencyKeys = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped(_ => authorization);
        builder.Services.AddScoped(_ => application);
        await using WebApplication app = builder.Build();
        app.MapVistaraDerivatives();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains(method, StringComparer.Ordinal));

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
        };
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-derivatives";
        context.Request.RouteValues["assetId"] = AssetId.ToString("D");
        context.Request.RouteValues["requestId"] = RequestId.ToString("D");
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }

        if (idempotencyKeys is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKeys.ToArray();
        }
        else if (idempotencyKey is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers,
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        IHeaderDictionary Headers,
        string Body)
    {
        public JsonDocument Json() => JsonDocument.Parse(Body);
    }
}

internal sealed class FakeDerivativeAuthorizationPort : IDerivativeAuthorizationPort
{
    public DerivativeAccess CatalogAccess { get; init; } =
        DerivativeAccess.AuthorizedCatalog(DerivativeEndpointContractTests.TenantIdForFakes);

    public DerivativeAccess AssetAccess { get; init; } =
        DerivativeAccess.AuthorizedAsset(
            DerivativeEndpointContractTests.TenantIdForFakes,
            DerivativeEndpointContractTests.AssetIdForFakes);

    public ValueTask<DerivativeAccess> AuthorizeCatalogAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(CatalogAccess);
    }

    public ValueTask<DerivativeAccess> AuthorizeAssetAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(AssetAccess);
    }
}

internal class FakeDerivativeApplicationPort : IDerivativeApplicationPort
{
    public IReadOnlyList<DerivativePresetDefinition> Presets { get; init; } = [];
    public DerivativeCanonicalizationResult Canonicalization { get; init; } =
        DerivativeCanonicalizationResult.Accepted(
            new CanonicalDerivativeRequest(
                "viewer",
                2,
                new CanonicalDerivativeParameters(
                    1_024,
                    1_024,
                    "contain",
                    "webp",
                    82,
                    null,
                    null),
                "canonical-request-hash"));
    public DerivativeSubmissionResult Submission { get; init; } =
        DerivativeSubmissionResult.Accepted(
            DerivativeEndpointContractTests.SnapshotForFakes(
                DerivativeWorkState.Queued,
                version: 1));
    public IReadOnlyList<DerivativeWorkSnapshot> Items { get; init; } =
        [
            DerivativeEndpointContractTests.SnapshotForFakes(
                DerivativeWorkState.Processing,
                version: 4),
        ];
    public Exception? Exception { get; init; }
    public int CanonicalizeCalls { get; private set; }
    public int TotalCalls { get; private set; }
    public bool CancellationObserved { get; private set; }
    public IdempotencyKey? LastIdempotencyKey { get; private set; }
    public List<DerivativeAssetScope> ObservedScopes { get; } = [];

    public virtual ValueTask<IReadOnlyList<DerivativePresetDefinition>> ListPresetsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        Observe(cancellationToken);
        return ValueTask.FromResult(Presets);
    }

    public virtual ValueTask<DerivativeCanonicalizationResult> CanonicalizeAsync(
        DerivativeAssetScope scope,
        DerivativeRequestContract request,
        CancellationToken cancellationToken)
    {
        Observe(scope, cancellationToken);
        CanonicalizeCalls++;
        return ValueTask.FromResult(Canonicalization);
    }

    public virtual ValueTask<DerivativeSubmissionResult> RequestAsync(
        DerivativeAssetScope scope,
        CanonicalDerivativeRequest request,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        Observe(scope, cancellationToken);
        LastIdempotencyKey = idempotencyKey;
        return ValueTask.FromResult(Submission);
    }

    public virtual ValueTask<IReadOnlyList<DerivativeWorkSnapshot>> ListAsync(
        DerivativeAssetScope scope,
        CancellationToken cancellationToken)
    {
        Observe(scope, cancellationToken);
        return ValueTask.FromResult(Items);
    }

    public virtual ValueTask<DerivativeWorkSnapshot?> GetStatusAsync(
        DerivativeAssetScope scope,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        Observe(scope, cancellationToken);
        return ValueTask.FromResult<DerivativeWorkSnapshot?>(
            Items.Count == 0 ? null : Items[0]);
    }

    protected void Observe(
        DerivativeAssetScope scope,
        CancellationToken cancellationToken)
    {
        ObservedScopes.Add(scope);
        Observe(cancellationToken);
    }

    protected void Observe(CancellationToken cancellationToken)
    {
        TotalCalls++;
        if (cancellationToken.IsCancellationRequested)
        {
            CancellationObserved = true;
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (Exception is not null)
        {
            throw Exception;
        }
    }
}

internal sealed class IdempotentFakeDerivativeApplicationPort
    : FakeDerivativeApplicationPort
{
    private readonly Dictionary<string, (string Hash, DerivativeSubmissionResult Result)> _requests =
        new(StringComparer.Ordinal);

    public override ValueTask<DerivativeCanonicalizationResult> CanonicalizeAsync(
        DerivativeAssetScope scope,
        DerivativeRequestContract request,
        CancellationToken cancellationToken)
    {
        CanonicalDerivativeParameters parameters = new(
            request.Parameters?.Width ?? 1_024,
            request.Parameters?.Height ?? 1_024,
            request.Parameters?.Fit ?? "contain",
            request.Parameters?.Format ?? "webp",
            request.Parameters?.Quality ?? 82,
            request.Parameters?.FocalPoint,
            request.Parameters?.Crop);
        return ValueTask.FromResult(
            DerivativeCanonicalizationResult.Accepted(
                new CanonicalDerivativeRequest(
                    request.Preset,
                    request.Revision,
                    parameters,
                    $"{request.Preset}:{request.Revision}:{parameters.Width}:{parameters.Height}:{parameters.Format}")));
    }

    public override ValueTask<DerivativeSubmissionResult> RequestAsync(
        DerivativeAssetScope scope,
        CanonicalDerivativeRequest request,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (_requests.TryGetValue(idempotencyKey.Value, out var existing))
        {
            return ValueTask.FromResult(
                existing.Hash == request.RequestHash
                    ? existing.Result with { Replayed = true }
                    : DerivativeSubmissionResult.Conflict());
        }

        DerivativeSubmissionResult result = DerivativeSubmissionResult.Accepted(
            DerivativeEndpointContractTests.SnapshotForFakes(
                DerivativeWorkState.Queued,
                version: 1));
        _requests.Add(idempotencyKey.Value, (request.RequestHash, result));
        return ValueTask.FromResult(result);
    }
}
