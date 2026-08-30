using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Assets;
using Vistara.Api.Features.Lifecycle;
using Vistara.Application.Common;
using Vistara.Application.Lifecycle;
using Vistara.IntegrationTests.Persistence;
using Vistara.Persistence.Lifecycle;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Lifecycle;

public sealed class LifecycleApiTests
{
    private static readonly DateTimeOffset Now =
        new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public void Lifecycle_routes_match_the_frozen_gallery_contract()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(
                LifecycleEndpointMapping.LifecyclePolicyName,
                policy => policy.RequireAuthenticatedUser()));
        WebApplication app = builder.Build();
        app.MapVistaraLifecycle();

        string[] routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
                $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} " +
                endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "GET /api/v1/trash",
                "GET /api/v1/trash/purge/{batchId:guid}",
                "POST /api/v1/trash/purge",
                "POST /api/v1/trash/purge/{batchId:guid}/confirm",
                "POST /api/v1/trash/restore",
            ],
            routes);
    }

    [Fact]
    public async Task Asset_bulk_trash_route_mutates_through_lifecycle_and_replays_safely()
    {
        Guid tenantId = LifecyclePersistenceTests.Id(610);
        Guid actorId = LifecyclePersistenceTests.Id(611);
        Guid assetId = LifecyclePersistenceTests.Id(612);
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        LifecyclePersistenceTests.SeededAsset seeded =
            await LifecyclePersistenceTests.SeedAssetAsync(
                database.Context,
                tenantId,
                actorId,
                assetId,
                includeRelationships: false);
        var ids = new LifecyclePersistenceTests.SequenceUuid7Generator(
            Now.AddSeconds(1));
        var clock = new LifecyclePersistenceTests.MutableClock(Now);
        var authorization = new FixedLifecycleAuthorization(
            LifecycleAccess.Authorized(LifecycleActorContext.Human(
                tenantId,
                actorId,
                LifecycleRights.Trash,
                Now)));

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<ILifecycleAuthorizationPort>(_ => authorization);
        builder.Services.AddScoped<ILifecycleStore>(_ =>
            new RelationalLifecycleStore(database.Context, ids));
        builder.Services.AddScoped<LifecycleService>();
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<IUuid7Generator>(ids);
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
        string requestBody = $$"""
        {
          "items": [
            {
              "id": "{{assetId:D}}",
              "version": {{seeded.AssetVersion}}
            }
          ],
          "action": {
            "kind": "trash",
            "reason": "cleanup"
          }
        }
        """;

        TestResponse first = await SendAsync(
            app,
            endpoint,
            requestBody,
            "trash-integration");
        TestResponse replay = await SendAsync(
            app,
            endpoint,
            requestBody,
            "trash-integration");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.Body, replay.Body);
        Assert.Equal("true", replay.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal("no-store", replay.Headers.CacheControl.ToString());
        database.Context.ChangeTracker.Clear();
        AssetRow asset = await database.Context.Assets.SingleAsync();
        TrashEntryRow trash = await database.Context.TrashEntries.SingleAsync();
        Assert.Equal("Trashed", asset.Status);
        Assert.Equal(Now, trash.DeletedAtUtc);
        Assert.Equal(Now.AddDays(30), trash.PurgeAtUtc);
        Assert.Equal(actorId, trash.DeletedByUserId);
    }

    private static async Task<TestResponse> SendAsync(
        WebApplication app,
        RouteEndpoint endpoint,
        string body,
        string idempotencyKey)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/assets/bulk";
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-lifecycle-integration";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/json";
        context.Request.Headers["Idempotency-Key"] = idempotencyKey;

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
        string Body);

    private sealed class FixedLifecycleAuthorization(LifecycleAccess access) :
        ILifecycleAuthorizationPort
    {
        public ValueTask<LifecycleAccess> AuthorizeAsync(
            HttpContext context,
            LifecycleApiOperation operation,
            CancellationToken cancellationToken)
        {
            Assert.Equal(LifecycleApiOperation.Trash, operation);
            return ValueTask.FromResult(access);
        }
    }
}
