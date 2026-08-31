using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Assets.Ingest;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Application.Gallery.Curation;
using Vistara.Application.Lifecycle;
using Vistara.Persistence;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Lifecycle;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Features.Gallery;
using Vistara.Worker.Features.Ingest;
using Vistara.Worker.Features.Lifecycle;
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
        IServiceProvider scoped = scope.ServiceProvider;
        scoped.GetRequiredService<IMutableTenantScope>()
            .Establish(Guid.CreateVersion7());
        _ = scoped.GetRequiredService<IAssetIngestUnitOfWork>();
        _ = scoped.GetRequiredService<IIngestTransactionPort>();
        _ = scoped.GetRequiredService<AssetIngestService>();
        _ = scoped.GetRequiredService<IngestService>();
        _ = scoped.GetRequiredService<IngestJobHandler>();
        _ = scoped.GetRequiredService<IDerivativeStatePort>();
        _ = scoped.GetRequiredService<DerivativeService>();
        _ = scoped.GetRequiredService<DerivativeJobHandler>();
        _ = scoped.GetRequiredService<UploadReconciliationService>();
        _ = scoped.GetRequiredService<UploadReconciliationJobHandler>();
        _ = scoped.GetRequiredService<RelationalLifecycleWorkerStore>();
        _ = scoped.GetRequiredService<ILifecycleWorkerStore>();
        _ = scoped.GetRequiredService<LifecyclePurgeService>();
        _ = scoped.GetRequiredService<LifecycleRestoreService>();
        _ = scoped.GetRequiredService<LifecyclePurgeJobHandler>();
        _ = scoped.GetRequiredService<LifecycleRestoreJobHandler>();
        _ = scoped.GetRequiredService<IGalleryCurationBulkExecutor>();
        _ = scoped.GetRequiredService<GalleryCurationBulkService>();
        _ = scoped.GetRequiredService<GalleryCurationBulkJobHandler>();
        _ = scoped.GetServices<IJobHandler>().ToArray();
        _ = services.GetRequiredService<UploadReconciliationScheduler>();
        return services;
    }
}
