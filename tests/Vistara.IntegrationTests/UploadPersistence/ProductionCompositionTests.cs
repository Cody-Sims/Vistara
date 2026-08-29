using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Persistence;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Ingest;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Xunit;

namespace Vistara.IntegrationTests.UploadPersistence;

public sealed class ProductionCompositionTests
{
    [Fact]
    public void Api_persistence_composition_resolves_a_real_upload_adapter()
    {
        ServiceCollection services = [];
        services.AddSingleton(Fake<IBlobStore>());
        IConfiguration configuration = PersistenceConfiguration();
        services.AddVistaraApiPlatform(configuration);
        services.AddVistaraApiPersistence(configuration);

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });

        provider.ValidateVistaraApiPlatformComposition();
        using IServiceScope scope = provider.CreateScope();
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>());
    }

    [Fact]
    public void Worker_production_composition_resolves_real_ingest_adapters()
    {
        ServiceCollection services = [];
        services.AddSingleton<IBlobStore>(new TestBlobStore());
        services.AddSingleton<IImageProcessor>(NoopImageProcessor.Instance);
        services.AddVistaraWorkerPlatform(PersistenceConfiguration(includeWorker: true));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });

        provider.ValidateVistaraWorkerPlatformComposition();
        using IServiceScope scope = provider.CreateScope();
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<IIngestTransactionPort>());
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<IAssetIngestUnitOfWork>());
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<
                IUploadReconciliationStatePort>());
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<
                IUploadReconciliationStoragePort>());
        Assert.NotNull(
            provider.GetRequiredService<UploadReconciliationScheduleMetadata>());
    }

    [Fact]
    public void Api_persistence_composition_preserves_an_explicit_test_override()
    {
        IUploadApplicationPort replacement = Fake<IUploadApplicationPort>();
        ServiceCollection services = [];
        services.AddScoped(_ => replacement);
        services.AddVistaraApiPlatform(PersistenceConfiguration());
        services.AddVistaraApiPersistence(PersistenceConfiguration());

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.Same(
            replacement,
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>());
    }

    private static IConfiguration PersistenceConfiguration(
        bool includeWorker = false) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = "Data Source=:memory:",
                ["Worker:InstanceId"] = includeWorker
                    ? "upload-persistence-composition"
                    : null,
                ["Worker:Jobs:MaximumConcurrency"] = includeWorker ? "1" : null,
            })
            .Build();

    private static T Fake<T>()
        where T : class =>
        DispatchProxy.Create<T, NoInvocationProxy>();

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
