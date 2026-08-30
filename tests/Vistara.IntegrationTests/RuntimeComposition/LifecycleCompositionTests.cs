using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Jobs;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Lifecycle;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Lifecycle;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.RuntimeComposition;

public sealed class LifecycleCompositionTests
{
    [Fact]
    public void Worker_composition_registers_one_scoped_lifecycle_handler_graph()
    {
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(Configuration());

        AssertSingleScoped<RelationalLifecycleWorkerStore>(services);
        AssertSingleScoped<ILifecycleWorkerStore>(services);
        AssertSingleScoped<LifecyclePurgeService>(services);
        AssertSingleScoped<LifecycleRestoreService>(services);
        AssertSingleScoped<LifecyclePurgeJobHandler>(services);
        AssertSingleScoped<LifecycleRestoreJobHandler>(services);
        AssertSingleJobHandler<LifecyclePurgeJobHandler>(services);
        AssertSingleJobHandler<LifecycleRestoreJobHandler>(services);
    }

    [Fact]
    public void Worker_startup_validation_resolves_lifecycle_after_tenant_establishment()
    {
        var resolvedTenants = new List<Guid>();
        ServiceCollection services = [];
        services.AddSingleton<IBlobStore>(
            NoInvocationProxy.Create<IBlobStore>());
        services.AddSingleton<IImageProcessor>(
            NoInvocationProxy.Create<IImageProcessor>());
        services.AddScoped<ILifecycleWorkerStore>(provider =>
        {
            resolvedTenants.Add(
                provider.GetRequiredService<ITenantScope>().TenantId);
            return NoInvocationProxy.Create<ILifecycleWorkerStore>();
        });
        services.AddVistaraWorkerPlatform(Configuration());
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });

        provider.ValidateVistaraWorkerPlatformComposition();

        Guid tenantId = Assert.Single(resolvedTenants);
        Assert.NotEqual(Guid.Empty, tenantId);
        Assert.Equal(7, tenantId.Version);
    }

    [Fact]
    public async Task Worker_runtime_dispatches_lifecycle_jobs_from_producer_contracts()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid batchId = Guid.CreateVersion7();
        var store = new RecordingLifecycleWorkerStore();
        var restorePayload = new LifecycleRestoreJobPayload(
            tenantId,
            actorId,
            [new LifecycleAssetTarget(assetId, 1)]);
        var purgePayload = new LifecyclePurgeJobPayload(tenantId, batchId);
        var queue = new RecordingJobQueue(
        [
            CreateAssignment(
                tenantId,
                LifecycleJobContracts.RestoreType,
                LifecycleJobContracts.SerializeRestore(restorePayload),
                "lifecycle-restore"),
            CreateAssignment(
                tenantId,
                LifecycleJobContracts.PurgeType,
                LifecycleJobContracts.SerializePurge(purgePayload),
                "lifecycle-purge"),
        ]);
        ServiceCollection services = [];
        services.AddSingleton<IBlobStore>(
            NoInvocationProxy.Create<IBlobStore>());
        services.AddSingleton<IImageProcessor>(
            NoInvocationProxy.Create<IImageProcessor>());
        services.AddScoped<ILifecycleWorkerStore>(_ => store);
        services.AddVistaraWorkerPlatform(Configuration());
        services.RemoveAll<IWorkerTenantCatalog>();
        services.RemoveAll<IJobQueue>();
        services.AddSingleton<IWorkerTenantCatalog>(
            new FixedWorkerTenantCatalog(tenantId));
        services.AddSingleton<IJobQueue>(queue);
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });

        await provider
            .GetRequiredService<JobWorkerRuntime>()
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, queue.Completed.Count);
        LifecycleRestoreJobPayload restored =
            Assert.Single(store.RestorePayloads);
        Assert.Equal(restorePayload.TenantId, restored.TenantId);
        Assert.Equal(restorePayload.ActorId, restored.ActorId);
        Assert.Equal(restorePayload.Targets, restored.Targets);
        Assert.Equal((tenantId, batchId), Assert.Single(store.PurgeBatches));
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = "Data Source=:memory:",
                ["Worker:InstanceId"] = "lifecycle-composition-test",
            })
            .Build();

    private static void AssertSingleScoped<TService>(IServiceCollection services)
    {
        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    private static void AssertSingleJobHandler<THandler>(
        IServiceCollection services)
    {
        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate =>
                candidate.ServiceType == typeof(IJobHandler) &&
                candidate.ImplementationType == typeof(THandler));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    private static JobLeaseAssignment CreateAssignment(
        Guid tenantId,
        JobType jobType,
        string payload,
        string dedupeKey)
    {
        DateTimeOffset now =
            new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        DurableJob job = DurableJob.Create(
            new JobId(Guid.CreateVersion7()),
            new JobTenantId(tenantId),
            jobType,
            payload,
            LifecycleJobContracts.PayloadVersion,
            new JobDedupeKey(dedupeKey),
            priority: 0,
            maxAttempts: 3,
            availableAtUtc: now,
            createdAtUtc: now);
        Result<JobLease> leased = job.TryLease(
            new JobLeaseOwner("lifecycle-composition-test"),
            now,
            TimeSpan.FromMinutes(1));
        Assert.True(
            leased.TryGetValue(out JobLease? lease),
            leased.Error?.Code);
        return new JobLeaseAssignment(job, lease!);
    }

    private sealed class FixedWorkerTenantCatalog(Guid tenantId) :
        IWorkerTenantCatalog
    {
        public ValueTask<IReadOnlyList<Guid>> ListTenantIdsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<Guid>>([tenantId]);
        }
    }

    private sealed class RecordingJobQueue(
        IReadOnlyList<JobLeaseAssignment> assignments) : IJobQueue
    {
        private readonly Queue<JobLeaseAssignment> _assignments =
            new(assignments);

        internal List<JobCompletionRequest> Completed { get; } = [];

        public ValueTask<Result<JobEnqueueResult>> EnqueueAsync(
            DurableJob job,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<JobLeaseAssignment>>> LeaseAsync(
            JobLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leased = new List<JobLeaseAssignment>();
            while (leased.Count < request.MaximumCount &&
                   _assignments.TryDequeue(out JobLeaseAssignment? assignment))
            {
                leased.Add(assignment);
            }

            return ValueTask.FromResult(
                Result.Success<IReadOnlyList<JobLeaseAssignment>>(leased));
        }

        public ValueTask<Result<JobLease>> HeartbeatAsync(
            JobHeartbeatRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> CompleteAsync(
            JobCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completed.Add(request);
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> FailAsync(
            JobFailureRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Lifecycle job failed with {request.Failure.Code}.");

        public ValueTask<Result> RecoverExpiredAsync(
            JobExpiredLeaseRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLifecycleWorkerStore : ILifecycleWorkerStore
    {
        internal List<LifecycleRestoreJobPayload> RestorePayloads { get; } = [];
        internal List<(Guid TenantId, Guid BatchId)> PurgeBatches { get; } = [];

        public ValueTask<Result> RestoreAsync(
            LifecycleRestoreJobPayload payload,
            DateTimeOffset restoredAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestorePayloads.Add(payload);
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<LifecyclePurgeBatchWork> StartPurgeBatchAsync(
            Guid tenantId,
            Guid batchId,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PurgeBatches.Add((tenantId, batchId));
            return ValueTask.FromResult(
                new LifecyclePurgeBatchWork(
                    LifecyclePurgeBatchWorkStatus.Completed,
                    []));
        }

        public ValueTask<LifecyclePurgeAssetPreparation> PreparePurgeAssetAsync(
            Guid tenantId,
            Guid batchId,
            Guid assetId,
            string storageProvider,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<LifecyclePurgeActionCheck> RecheckPurgeActionAsync(
            LifecyclePurgeAssetFence fence,
            LifecyclePurgeProviderAction action,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> RecordPurgeActionDeletedAsync(
            LifecyclePurgeAssetFence fence,
            LifecyclePurgeProviderAction action,
            DateTimeOffset deletedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<long>> CompletePurgeAssetAsync(
            LifecyclePurgeAssetFence fence,
            DateTimeOffset purgedAtUtc,
            DateTimeOffset backupExpiresAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> RecordPurgeItemResultAsync(
            Guid tenantId,
            Guid batchId,
            Guid assetId,
            LifecyclePurgeItemOutcome outcome,
            string errorCode,
            DateTimeOffset recordedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> CompletePurgeBatchAsync(
            Guid tenantId,
            Guid batchId,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy generates a runtime subclass.")]
    private class NoInvocationProxy : DispatchProxy
    {
        internal static T Create<T>()
            where T : class =>
            DispatchProxy.Create<T, NoInvocationProxy>();

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            throw new InvalidOperationException(
                $"{targetMethod?.Name ?? "Unknown"} should not be invoked.");
    }
}
