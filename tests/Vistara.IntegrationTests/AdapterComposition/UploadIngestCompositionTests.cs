using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Ingest;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.AdapterComposition;

public sealed class UploadIngestCompositionTests
{
    [Fact]
    public void Api_validation_names_the_missing_upload_application_port()
    {
        ServiceCollection services = [];
        services.AddVistaraApiPlatform(new ConfigurationManager());

        using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => provider.ValidateVistaraApiPlatformComposition());

        Assert.Contains(nameof(IUploadApplicationPort), error.Message);
    }

    [Fact]
    public void Worker_validation_names_each_missing_ingest_persistence_port()
    {
        ServiceCollection missingUnitOfWork = [];
        missingUnitOfWork.AddVistaraWorkerPlatform(WorkerConfiguration());
        using ServiceProvider firstProvider =
            missingUnitOfWork.BuildServiceProvider();
        InvalidOperationException unitOfWorkError =
            Assert.Throws<InvalidOperationException>(
                () => firstProvider.ValidateVistaraWorkerPlatformComposition());
        Assert.Contains(nameof(IAssetIngestUnitOfWork), unitOfWorkError.Message);

        ServiceCollection missingTransactions = [];
        missingTransactions.AddScoped(_ => Fake<IAssetIngestUnitOfWork>());
        missingTransactions.AddVistaraWorkerPlatform(WorkerConfiguration());
        using ServiceProvider secondProvider =
            missingTransactions.BuildServiceProvider();
        InvalidOperationException transactionsError =
            Assert.Throws<InvalidOperationException>(
                () => secondProvider.ValidateVistaraWorkerPlatformComposition());
        Assert.Contains(nameof(IIngestTransactionPort), transactionsError.Message);
    }

    [Fact]
    public async Task Production_composition_resolves_upload_and_ingest_graphs_with_overrides()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        IUploadApplicationPort uploadApplication = Fake<IUploadApplicationPort>();
        WebApplicationBuilder apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Services.AddScoped(_ => uploadApplication);
        apiBuilder.Services.AddScoped<IPlatformTenantContext>(
            _ => new FixedTenantContext(tenantId));
        apiBuilder.Services.AddVistaraApiPlatform(apiBuilder.Configuration);

        await using WebApplication api = apiBuilder.Build();
        api.Services.ValidateVistaraApiPlatformComposition();
        api.UseVistaraPlatform();
        api.MapVistaraPlatformEndpoints();

        await using (AsyncServiceScope scope = api.Services.CreateAsyncScope())
        {
            IUploadAuthorizationPort authorization = scope.ServiceProvider
                .GetRequiredService<IUploadAuthorizationPort>();
            Assert.Same(
                uploadApplication,
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<
                Vistara.Application.Common.IClock>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<
                Vistara.Application.Common.IUuid7Generator>());

            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, actorId.ToString("D")),
                    new Claim("tenant_id", tenantId.ToString("D")),
                    new Claim("scope", "assets.upload"),
                ], "composition-test")),
            };
            UploadAccess access = await authorization.AuthorizeCreateAsync(
                context,
                CancellationToken.None);
            Assert.Equal(UploadAccessStatus.Authorized, access.Status);
            Assert.Equal(tenantId, access.TenantId);
            Assert.Equal(actorId, access.ActorId);
        }

        IAuthorizationPolicyProvider policyProvider = api.Services
            .GetRequiredService<IAuthorizationPolicyProvider>();
        Assert.NotNull(await policyProvider.GetPolicyAsync(
            UploadEndpointMapping.UploadPolicyName));

        string[] routes = ((IEndpointRouteBuilder)api).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(route => route?.StartsWith(
                "/api/v1/uploads",
                StringComparison.Ordinal) == true)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "/api/v1/uploads",
            "/api/v1/uploads/{id:guid}",
            "/api/v1/uploads/{id:guid}",
            "/api/v1/uploads/{id:guid}/commit",
            "/api/v1/uploads/{id:guid}/content",
            "/api/v1/uploads/{id:guid}/parts",
        ],
            routes);

        IIngestTransactionPort ingestTransactions = Fake<IIngestTransactionPort>();
        IAssetIngestUnitOfWork assetIngestUnitOfWork =
            Fake<IAssetIngestUnitOfWork>();
        ServiceCollection workerServices = [];
        workerServices.AddScoped(_ => ingestTransactions);
        workerServices.AddScoped(_ => assetIngestUnitOfWork);
        workerServices.AddSingleton(Fake<IBlobStore>());
        workerServices.AddSingleton(Fake<IImageProcessor>());
        workerServices.AddVistaraWorkerPlatform(WorkerConfiguration());

        using ServiceProvider worker = workerServices.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
        worker.ValidateVistaraWorkerPlatformComposition();
        using IServiceScope workerScope = worker.CreateScope();
        Assert.Same(
            ingestTransactions,
            workerScope.ServiceProvider.GetRequiredService<IIngestTransactionPort>());
        Assert.Same(
            assetIngestUnitOfWork,
            workerScope.ServiceProvider.GetRequiredService<IAssetIngestUnitOfWork>());
        Assert.NotNull(
            workerScope.ServiceProvider.GetRequiredService<AssetIngestService>());
        Assert.NotNull(workerScope.ServiceProvider.GetRequiredService<IngestService>());
        Assert.NotNull(
            workerScope.ServiceProvider.GetRequiredService<IngestJobHandler>());
        Assert.Single(
            workerServices,
            descriptor =>
                descriptor.ServiceType == typeof(IJobHandler) &&
                descriptor.ImplementationType == typeof(IngestJobHandler));
    }

    private static IConfiguration WorkerConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = "Data Source=:memory:",
                ["Worker:InstanceId"] = "upload-ingest-composition",
                ["Worker:Jobs:MaximumConcurrency"] = "1",
            })
            .Build();

    private static T Fake<T>()
        where T : class =>
        DispatchProxy.Create<T, NoInvocationProxy>();

    private sealed class FixedTenantContext(Guid tenantId) : IPlatformTenantContext
    {
        public Guid? TenantId { get; } = tenantId;
    }

    [SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy generates a runtime subclass.")]
    private class NoInvocationProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            throw new InvalidOperationException(
                $"{targetMethod?.Name ?? "Unknown"} should not be invoked.");
    }
}
