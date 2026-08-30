using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Assets;
using Vistara.Api.Features.Lifecycle;
using Vistara.Application.Common;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.Api.ContractTests.AssetQueries;

public sealed class AssetTrashEndpointContractTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000721");
    private static readonly Guid ActorId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000722");
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000723");

    [Fact]
    public void Mapping_registers_the_frozen_bulk_asset_route()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();

        app.MapVistaraAssetQueries();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == "/api/v1/assets/bulk" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains("POST", StringComparer.Ordinal));
        Assert.Equal(
            "bulkMutateAssets",
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        IAuthorizeData authorization =
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(AssetEndpointMapping.AssetQueryPolicyName, authorization.Policy);
    }

    [Fact]
    public async Task Authorization_conceals_assets_before_lifecycle_mutation()
    {
        var store = new RecordingLifecycleStore();
        var authorization = new FakeLifecycleAuthorization
        {
            Access = LifecycleAccess.Denied(LifecycleAccessStatus.Concealed),
        };

        TestResponse response = await SendAsync(
            store,
            authorization,
            Body(),
            idempotencyKey: "trash-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("lifecycle_not_found", response.ProblemCode());
        Assert.Equal(0, store.TrashCalls);
        Assert.DoesNotContain(
            AssetId.ToString("D"),
            response.Body,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trash_requires_idempotency_and_versioned_targets()
    {
        var store = new RecordingLifecycleStore();

        TestResponse missingKey = await SendAsync(
            store,
            new FakeLifecycleAuthorization(),
            Body());
        TestResponse invalidVersion = await SendAsync(
            store,
            new FakeLifecycleAuthorization(),
            Body(version: 0),
            idempotencyKey: "trash-2");

        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal("invalid_idempotency_key", missingKey.ProblemCode());
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);
        Assert.Equal("request_invalid", invalidVersion.ProblemCode());
        Assert.Equal(0, store.TrashCalls);
    }

    [Fact]
    public async Task Trash_uses_authenticated_scope_default_retention_and_returns_statuses()
    {
        var store = new RecordingLifecycleStore
        {
            StoreResult = Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(
                [new(AssetId, "trashed", 4, null)]),
        };

        TestResponse response = await SendAsync(
            store,
            new FakeLifecycleAuthorization(),
            Body(
                reason: "  cleanup  ",
                extraActionFields:
                    $""","tenantId":"{Guid.CreateVersion7():D}","ownerId":"{Guid.CreateVersion7():D}","retentionDays":1"""),
            idempotencyKey: "trash-3");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        JsonElement result = Assert.Single(response.Json().RootElement.EnumerateArray());
        Assert.Equal(AssetId, result.GetProperty("assetId").GetGuid());
        Assert.Equal("trashed", result.GetProperty("status").GetString());
        Assert.Equal(4, result.GetProperty("version").GetInt64());
        Assert.False(result.TryGetProperty("errorCode", out _));
        Assert.NotNull(store.LastTrash);
        Assert.Equal(TenantId, store.LastTrash.TenantId);
        Assert.Equal(ActorId, store.LastTrash.ActorId);
        Assert.Equal(AssetId, Assert.Single(store.LastTrash.Targets).AssetId);
        Assert.Equal(3, Assert.Single(store.LastTrash.Targets).Version);
        Assert.Equal("cleanup", store.LastTrash.Reason);
        Assert.Equal(Now, store.LastTrash.DeletedAtUtc);
        Assert.Equal(Now.AddDays(30), store.LastTrash.PurgeAtUtc);
    }

    [Fact]
    public async Task Replay_returns_the_same_status_and_conflicts_are_safe()
    {
        var store = new RecordingLifecycleStore
        {
            StoreResult = Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(
                [new(AssetId, "trashed", 4, null)]),
        };
        var authorization = new FakeLifecycleAuthorization();

        TestResponse first = await SendAsync(
            store,
            authorization,
            Body(),
            idempotencyKey: "trash-4");
        store.StoreResult = Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(
            [new(AssetId, "alreadyTrashed", 4, null)]);
        TestResponse replay = await SendAsync(
            store,
            authorization,
            Body(),
            idempotencyKey: "trash-4");
        store.StoreResult = Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(
            [new(
                AssetId,
                "versionConflict",
                5,
                "lifecycle.version_conflict")]);
        TestResponse conflict = await SendAsync(
            store,
            authorization,
            Body(),
            idempotencyKey: "trash-5");

        Assert.Equal(first.Body, replay.Body);
        Assert.Equal("true", replay.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal(HttpStatusCode.Accepted, conflict.StatusCode);
        JsonElement conflictResult =
            Assert.Single(conflict.Json().RootElement.EnumerateArray());
        Assert.Equal("versionConflict", conflictResult.GetProperty("status").GetString());
        Assert.False(conflictResult.TryGetProperty("version", out _));
        Assert.Equal(
            "lifecycle.version_conflict",
            conflictResult.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Invalid_state_is_returned_as_a_bounded_item_status()
    {
        var store = new RecordingLifecycleStore
        {
            StoreResult = Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(
                [new(
                    AssetId,
                    "invalidState",
                    3,
                    "lifecycle.invalid_state")]),
        };

        TestResponse response = await SendAsync(
            store,
            new FakeLifecycleAuthorization(),
            Body(),
            idempotencyKey: "trash-6");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement result = Assert.Single(response.Json().RootElement.EnumerateArray());
        Assert.Equal("invalidState", result.GetProperty("status").GetString());
        Assert.Equal(
            "lifecycle.invalid_state",
            result.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("notFound")]
    [InlineData("forbidden")]
    public async Task Missing_and_unowned_assets_are_concealed(string storeStatus)
    {
        var store = new RecordingLifecycleStore
        {
            StoreResult = Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(
                [new(AssetId, storeStatus, 9, "lifecycle.forbidden")]),
        };

        TestResponse response = await SendAsync(
            store,
            new FakeLifecycleAuthorization(),
            Body(),
            idempotencyKey: "trash-7");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement result = Assert.Single(response.Json().RootElement.EnumerateArray());
        Assert.Equal("notFound", result.GetProperty("status").GetString());
        Assert.False(result.TryGetProperty("version", out _));
        Assert.Equal(
            "lifecycle.not_found",
            result.GetProperty("errorCode").GetString());
    }

    private static string Body(
        long version = 3,
        string reason = "cleanup",
        string extraActionFields = "") =>
        $$"""
        {
          "items": [
            {
              "id": "{{AssetId:D}}",
              "version": {{version}}
            }
          ],
          "action": {
            "kind": "trash",
            "reason": "{{reason}}"{{extraActionFields}}
          }
        }
        """;

    private static async Task<TestResponse> SendAsync(
        RecordingLifecycleStore store,
        FakeLifecycleAuthorization authorization,
        string body,
        string? idempotencyKey = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<ILifecycleAuthorizationPort>(_ => authorization);
        builder.Services.AddScoped<ILifecycleStore>(_ => store);
        builder.Services.AddScoped<LifecycleService>();
        builder.Services.AddSingleton<IClock>(new FixedClock(Now));
        builder.Services.AddSingleton<IUuid7Generator>(
            new FixedUuid7Generator(Guid.CreateVersion7(Now)));
        await using WebApplication app = builder.Build();
        app.MapVistaraAssetQueries();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == "/api/v1/assets/bulk" &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains("POST", StringComparer.Ordinal));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/assets/bulk";
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-asset-trash";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/json";
        if (idempotencyKey is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return new(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.Headers,
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        IHeaderDictionary Headers,
        string Body)
    {
        public JsonDocument Json() => JsonDocument.Parse(Body);

        public string ProblemCode() =>
            Json().RootElement.GetProperty("code").GetString()!;
    }

    private sealed class FakeLifecycleAuthorization : ILifecycleAuthorizationPort
    {
        public LifecycleAccess Access { get; init; } =
            LifecycleAccess.Authorized(LifecycleActorContext.Human(
                TenantId,
                ActorId,
                LifecycleRights.Trash,
                Now));

        public ValueTask<LifecycleAccess> AuthorizeAsync(
            HttpContext context,
            LifecycleApiOperation operation,
            CancellationToken cancellationToken)
        {
            Assert.Equal(LifecycleApiOperation.Trash, operation);
            return ValueTask.FromResult(Access);
        }
    }

    private sealed class RecordingLifecycleStore : ILifecycleStore
    {
        public Result<IReadOnlyList<LifecycleAssetMutationResult>> StoreResult { get; set; } =
            Result.Success<IReadOnlyList<LifecycleAssetMutationResult>>(
                [new(AssetId, "trashed", 4, null)]);

        public int TrashCalls { get; private set; }

        public LifecycleTrashCommand? LastTrash { get; private set; }

        public ValueTask<Result<LifecycleTrashPage>> ListTrashAsync(
            LifecycleTrashQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<LifecycleAssetMutationResult>>> TrashAsync(
            LifecycleTrashCommand command,
            CancellationToken cancellationToken)
        {
            TrashCalls++;
            LastTrash = command;
            return ValueTask.FromResult(StoreResult);
        }

        public ValueTask<Result<LifecyclePurgeBatchSnapshot>> ConfirmPurgeAsync(
            LifecycleConfirmPurgeCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleJobSubmission>> SubmitRestoreAsync(
            LifecycleRestoreCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecyclePurgeDryRunSnapshot>> CreatePurgeDryRunAsync(
            LifecycleCreatePurgeDryRunCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecyclePurgeBatchSnapshot>> GetPurgeBatchAsync(
            Guid tenantId,
            Guid actorId,
            Guid batchId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleHoldSnapshot>> PlaceHoldAsync(
            LifecyclePlaceHoldCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleHoldSnapshot>> ReleaseHoldAsync(
            LifecycleReleaseHoldCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedUuid7Generator(Guid id) : IUuid7Generator
    {
        public Guid NewId() => id;
    }
}
