using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Derivatives.Worker;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Lifecycle;
using Vistara.Persistence.Outbox;
using Vistara.Worker.Features.Derivatives;
using Vistara.Worker.Features.Lifecycle;
using Vistara.Worker.Features.Reconciliation.Derivatives;
using Vistara.Worker.Features.Reconciliation.Lifecycle;
using Vistara.Worker.Features.Reconciliation.Storage;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Runtime.Jobs;
using Vistara.Worker.Runtime.Reconciliation;

namespace Vistara.Worker.Composition.Platform;

public sealed class WorkerPlatformOptions
{
    public const string SectionName = "Worker";

    public string? InstanceId { get; set; }
    public JobWorkerOptions Jobs { get; set; } = new();
    public WorkerOutboxOptions Outbox { get; set; } = new();
    public WorkerImagingLimitsOptions ImagingLimits { get; set; } = new();
    public TimeSpan DerivativeOwnershipDuration { get; set; } =
        TimeSpan.FromMinutes(5);
}

public sealed class WorkerOutboxOptions
{
    public int BatchSize { get; set; } = 100;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class WorkerImagingLimitsOptions
{
    public const long HardMaxEncodedBytes = 50L * 1024 * 1024;
    public const int HardMaxDimension = 20_000;
    public const long HardMaxAggregatePixels = 40_000_000;
    public const int HardMaxFrames = 1;
    public const long HardMaxEstimatedDecodedBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan HardMaxProcessingDeadline =
        TimeSpan.FromSeconds(30);
    public const int HardMaxConcurrentTransforms = 1;

    public long MaxEncodedBytes { get; set; } = HardMaxEncodedBytes;
    public int MaxWidth { get; set; } = HardMaxDimension;
    public int MaxHeight { get; set; } = HardMaxDimension;
    public long MaxAggregatePixels { get; set; } = HardMaxAggregatePixels;
    public int MaxFrames { get; set; } = 1;
    public long MaxEstimatedDecodedBytes { get; set; } =
        HardMaxEstimatedDecodedBytes;
    public TimeSpan ProcessingDeadline { get; set; } =
        HardMaxProcessingDeadline;
    public int MaximumConcurrentTransforms { get; set; } = 1;
    public string ScratchDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vistara",
        "scratch",
        "derivatives");

    internal void Validate()
    {
        if (MaxEncodedBytes is < 1 or > HardMaxEncodedBytes ||
            MaxWidth is < 1 or > HardMaxDimension ||
            MaxHeight is < 1 or > HardMaxDimension ||
            MaxAggregatePixels is < 1 or > HardMaxAggregatePixels ||
            MaxFrames is < 1 or > HardMaxFrames ||
            MaxEstimatedDecodedBytes is < 1 or > HardMaxEstimatedDecodedBytes ||
            ProcessingDeadline <= TimeSpan.Zero ||
            ProcessingDeadline > HardMaxProcessingDeadline ||
            MaximumConcurrentTransforms is < 1 or > HardMaxConcurrentTransforms ||
            string.IsNullOrWhiteSpace(ScratchDirectory))
        {
            throw new InvalidOperationException(
                "Worker imaging limits exceed the supported safety ceiling.");
        }
    }
}

public static class WorkerPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraWorkerPlatform(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        (VistaraDatabaseProvider provider, string connectionString) =
            ReadPersistence(configuration);
        int configuredWorkerCount =
            configuration.GetValue<int?>("Worker:Jobs:MaximumConcurrency") ?? 1;
        if (provider == VistaraDatabaseProvider.Sqlite &&
            configuredWorkerCount != 1)
        {
            throw new InvalidOperationException(
                "SQLite job execution supports a single worker only.");
        }

        services.AddVistaraPersistence(options =>
        {
            options.Provider = provider;
            options.ConnectionString = connectionString;
        });
        services.AddVistaraJobQueue(options =>
        {
            options.Provider = provider;
            options.ConnectionString = connectionString;
            options.ConfiguredWorkerCount = configuredWorkerCount;
        });
        services.AddDbContext<OutboxDbContext>(options =>
        {
            if (provider == VistaraDatabaseProvider.Sqlite)
            {
                options.UseSqlite(connectionString);
            }
            else
            {
                options.UseNpgsql(connectionString);
            }
        });

        services.AddOptions<WorkerPlatformOptions>()
            .Bind(configuration.GetSection(WorkerPlatformOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<WorkerPlatformOptions>,
                WorkerPlatformOptionsValidator>());
        services.TryAddScoped<WorkerTenantContext>();
        services.TryAddScoped<ITenantScope>(
            static provider => provider.GetRequiredService<WorkerTenantContext>());
        services.TryAddScoped<IMutableTenantScope>(
            static provider => provider.GetRequiredService<WorkerTenantContext>());
        services.TryAddScoped<IOutboxTenantContext>(
            static provider => provider.GetRequiredService<WorkerTenantContext>());
        services.TryAddScoped<OutboxRepository>(static provider =>
        {
            OutboxDbContext context =
                provider.GetRequiredService<OutboxDbContext>();
            return new OutboxRepository(context, context);
        });

        services.TryAddSingleton<IClock>(
            Vistara.Application.Common.SystemClock.Instance);
        services.TryAddSingleton<IUuid7Generator, Uuid7Generator>();
        services.TryAddSingleton<IJobRandomSource, SystemJobRandomSource>();
        services.TryAddSingleton<IJobRuntimeObserver>(
            NullJobRuntimeObserver.Instance);
        services.TryAddSingleton<IJobFailureClassifier, SafeJobFailureClassifier>();
        services.TryAddSingleton(static provider =>
        {
            WorkerPlatformOptions options =
                provider.GetRequiredService<IOptions<WorkerPlatformOptions>>().Value;
            return options.Jobs;
        });
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(JobLeaseOwner),
            static provider =>
            {
                WorkerPlatformOptions options =
                    provider.GetRequiredService<IOptions<WorkerPlatformOptions>>().Value;
                return new JobLeaseOwner(options.InstanceId!);
            }));
        services.TryAddSingleton(static provider =>
        {
            WorkerImagingLimitsOptions limits = provider
                .GetRequiredService<IOptions<WorkerPlatformOptions>>()
                .Value
                .ImagingLimits;
            return new ImageDecodeLimits(
                limits.MaxEncodedBytes,
                limits.MaxWidth,
                limits.MaxHeight,
                limits.MaxAggregatePixels,
                limits.MaxFrames,
                limits.MaxEstimatedDecodedBytes,
                limits.ProcessingDeadline);
        });
        services.TryAddSingleton<IDerivativeOutputScratchFactory>(static provider =>
        {
            WorkerImagingLimitsOptions limits = provider
                .GetRequiredService<IOptions<WorkerPlatformOptions>>()
                .Value
                .ImagingLimits;
            return new FileDerivativeOutputScratchFactory(limits.ScratchDirectory);
        });
        services.TryAddSingleton(static provider =>
        {
            WorkerImagingLimitsOptions limits = provider
                .GetRequiredService<IOptions<WorkerPlatformOptions>>()
                .Value
                .ImagingLimits;
            return new DerivativeTransformGate(limits.MaximumConcurrentTransforms);
        });
        services.AddVistaraWorkerIngest();
        services.AddVistaraUploadReconciliation();
        services.TryAddSingleton<UploadReconciliationScheduler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                UploadReconciliationSchedulerHostedService>());
        services.AddVistaraBlobIntegrityReconciliation();
        services.AddVistaraDerivativeRecoveryReconciliation();
        services.AddVistaraPurgeRecoveryReconciliation();
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.BlobIntegrity);
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.DerivativeRecovery);
        services.AddVistaraReconciliationSchedule(
            ReconciliationSchedules.PurgeRecovery);
        services.AddVistaraReconciliationScheduler();
        services.TryAddSingleton(DerivativePresetRegistry.Standard);
        services.TryAddScoped<
            IDerivativeStatePort,
            RelationalDerivativeStatePort>();
        services.TryAddScoped<DerivativeService>(static provider =>
        {
            WorkerPlatformOptions options =
                provider.GetRequiredService<IOptions<WorkerPlatformOptions>>().Value;
            return new DerivativeService(
                provider.GetRequiredService<IDerivativeStatePort>(),
                provider.GetRequiredService<Vistara.Application.Common.Storage.IBlobStore>(),
                provider.GetRequiredService<IImageProcessor>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<ImageDecodeLimits>(),
                provider.GetRequiredService<IDerivativeOutputScratchFactory>(),
                provider.GetRequiredService<DerivativeTransformGate>(),
                options.DerivativeOwnershipDuration);
        });
        services.TryAddScoped<DerivativeJobHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, DerivativeJobHandler>());
        services.TryAddScoped<RelationalLifecycleWorkerStore>();
        services.TryAddScoped<ILifecycleWorkerStore>(static provider =>
            provider.GetRequiredService<RelationalLifecycleWorkerStore>());
        services.TryAddScoped<LifecyclePurgeService>();
        services.TryAddScoped<LifecycleRestoreService>();
        services.TryAddScoped<LifecyclePurgeJobHandler>();
        services.TryAddScoped<LifecycleRestoreJobHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IJobHandler,
                LifecyclePurgeJobHandler>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IJobHandler,
                LifecycleRestoreJobHandler>());
        services.TryAddSingleton<JobWorkerRuntime>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, JobWorkerHostedService>());
        services.TryAddSingleton<OutboxPublisher>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, OutboxPublisherHostedService>());
        return services;
    }

    private static (VistaraDatabaseProvider Provider, string ConnectionString)
        ReadPersistence(IConfiguration configuration)
    {
        string? providerName = configuration["Persistence:Provider"];
        string? connectionString =
            configuration.GetConnectionString("Vistara") ??
            configuration["Persistence:ConnectionString"];
        if (!Enum.TryParse(
                providerName,
                ignoreCase: true,
                out VistaraDatabaseProvider provider) ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "An explicit supported persistence provider and connection string are required.");
        }

        return (provider, connectionString);
    }
}

internal sealed class WorkerPlatformOptionsValidator :
    IValidateOptions<WorkerPlatformOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        WorkerPlatformOptions options)
    {
        try
        {
            _ = new JobLeaseOwner(options.InstanceId!);
            options.Jobs.Validate();
            options.ImagingLimits.Validate();
            _ = new ImageDecodeLimits(
                options.ImagingLimits.MaxEncodedBytes,
                options.ImagingLimits.MaxWidth,
                options.ImagingLimits.MaxHeight,
                options.ImagingLimits.MaxAggregatePixels,
                options.ImagingLimits.MaxFrames,
                options.ImagingLimits.MaxEstimatedDecodedBytes,
                options.ImagingLimits.ProcessingDeadline);
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException)
        {
            return ValidateOptionsResult.Fail(
                "Worker identity, job, and imaging limits must be configured explicitly and valid.");
        }

        if (options.Outbox.BatchSize is < 1 or > 1_000 ||
            options.Outbox.LeaseDuration <= TimeSpan.Zero ||
            options.Outbox.LeaseDuration > TimeSpan.FromHours(1) ||
            options.Outbox.PollInterval <= TimeSpan.Zero ||
            options.DerivativeOwnershipDuration <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                "Worker outbox and derivative runtime settings are invalid.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class WorkerTenantContext :
    IMutableTenantScope,
    IOutboxTenantContext
{
    private Guid? _tenantId;

    public Guid TenantId =>
        _tenantId ??
        throw new InvalidOperationException(
            "A tenant scope must be established before database access.");

    internal void Establish(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new InvalidOperationException(
                "A UUIDv7 tenant scope is required.");
        }

        if (_tenantId.HasValue && _tenantId.Value != tenantId)
        {
            throw new InvalidOperationException(
                "A worker scope cannot switch tenants.");
        }

        _tenantId = tenantId;
    }

    void IMutableTenantScope.Establish(Guid tenantId) => Establish(tenantId);
}

internal sealed class JobWorkerHostedService(JobWorkerRuntime runtime) :
    BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        runtime.RunAsync(stoppingToken);
}

public sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<WorkerPlatformOptions> options)
{
    private readonly Guid _owner = Guid.CreateVersion7();

    public async Task<bool> PublishAvailableAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (AsyncServiceScope catalogScope = scopeFactory.CreateAsyncScope())
        {
            IWorkerTenantCatalog catalog = catalogScope.ServiceProvider
                .GetRequiredService<IWorkerTenantCatalog>();
            tenants = await catalog.ListTenantIdsAsync(cancellationToken);
        }

        bool published = false;
        foreach (Guid tenantId in tenants)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider
                .GetRequiredService<IMutableTenantScope>()
                .Establish(tenantId);
            OutboxRepository repository =
                scope.ServiceProvider.GetRequiredService<OutboxRepository>();
            IReadOnlyList<OutboxClaim> claims = await repository.ClaimPendingAsync(
                _owner,
                clock.UtcNow,
                options.Value.Outbox.LeaseDuration,
                options.Value.Outbox.BatchSize,
                cancellationToken);
            foreach (OutboxClaim claim in claims)
            {
                OutboxPublishResult result = await repository.PublishClaimAsync(
                    claim.Message.Id,
                    claim.ClaimId,
                    claim.Version,
                    clock.UtcNow,
                    cancellationToken);
                published |= result.Outcome is
                    OutboxPublishOutcome.Published or
                    OutboxPublishOutcome.AlreadyPublished;
            }
        }

        return published;
    }
}

internal sealed class OutboxPublisherHostedService(
    OutboxPublisher publisher,
    IOptions<WorkerPlatformOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool published =
                await publisher.PublishAvailableAsync(stoppingToken);
            if (!published)
            {
                await Task.Delay(options.Value.Outbox.PollInterval, stoppingToken);
            }
        }
    }
}
