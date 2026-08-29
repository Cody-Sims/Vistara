using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Persistence.Ingest;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Features.Ingest;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Composition.Platform;

public static class WorkerIngestServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraWorkerIngest(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        bool hasProductionDependencies =
            services.Any(descriptor =>
                descriptor.ServiceType == typeof(RelationalIngestStore)) &&
            services.Any(descriptor =>
                descriptor.ServiceType == typeof(IBlobStore));
        if (hasProductionDependencies)
        {
            services.TryAddScoped<IAssetIngestUnitOfWork>(
                static provider => provider.GetRequiredService<
                    RelationalAssetIngestUnitOfWork>());
            services.TryAddScoped<
                IIngestTransactionPort,
                WorkerIngestPersistenceAdapter>();
        }
        else
        {
            services.TryAddScoped<IAssetIngestUnitOfWork>(
                static _ => throw new InvalidOperationException(
                    "No production implementation of IAssetIngestUnitOfWork is registered. " +
                    "Register a persistence-backed asset ingest unit of work before " +
                    "validating the Worker composition."));
            services.TryAddScoped<IIngestTransactionPort>(
                static _ => throw new InvalidOperationException(
                    "No production implementation of IIngestTransactionPort is registered. " +
                    "Register a persistence-backed ingest transaction adapter before " +
                    "validating the Worker composition."));
        }

        services.TryAddScoped<AssetIngestService>();
        services.TryAddScoped<IngestService>(static provider =>
            new IngestService(
                provider.GetRequiredService<IIngestTransactionPort>(),
                provider.GetRequiredService<IBlobStore>(),
                provider.GetRequiredService<IImageProcessor>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<ImageDecodeLimits>()));
        services.TryAddScoped<IngestJobHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, IngestJobHandler>());
        return services;
    }

    public static IServiceProvider ValidateVistaraWorkerPlatformComposition(
        this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using IServiceScope scope = services.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IAssetIngestUnitOfWork>();
        _ = scope.ServiceProvider.GetRequiredService<IIngestTransactionPort>();
        _ = scope.ServiceProvider.GetRequiredService<AssetIngestService>();
        _ = scope.ServiceProvider.GetRequiredService<IngestService>();
        _ = scope.ServiceProvider.GetRequiredService<IngestJobHandler>();
        _ = scope.ServiceProvider.GetRequiredService<IDerivativeStatePort>();
        _ = scope.ServiceProvider.GetRequiredService<DerivativeService>();
        _ = scope.ServiceProvider.GetRequiredService<DerivativeJobHandler>();
        _ = scope.ServiceProvider.GetRequiredService<UploadReconciliationService>();
        _ = scope.ServiceProvider.GetRequiredService<UploadReconciliationJobHandler>();
        _ = services.GetRequiredService<UploadReconciliationScheduler>();
        return services;
    }
}
