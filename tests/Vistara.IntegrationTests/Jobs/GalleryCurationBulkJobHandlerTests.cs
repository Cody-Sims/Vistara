using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Curation;
using Vistara.Domain.Jobs;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Gallery;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.Jobs;

/// <summary>
/// Covers the job-facing contract of the bulk curation handler: what it
/// refuses, what it reports, and what it records.
/// </summary>
public sealed class GalleryCurationBulkJobHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ActorId = Guid.CreateVersion7();
    private static readonly Guid FirstAssetId = Guid.CreateVersion7();
    private static readonly Guid SecondAssetId = Guid.CreateVersion7();

    [Fact]
    public void Handler_declares_the_queued_bulk_curation_job_type()
    {
        GalleryCurationBulkJobHandler handler =
            Handler(new RecordingExecutor(), new RecordingAudit());

        Assert.Equal("GalleryCurationBulk", handler.JobType.Value);
        Assert.Equal(
            GalleryCurationJobContracts.BulkType,
            GalleryCurationBulkJobHandler.SupportedJobType);
    }

    [Fact]
    public async Task Handler_applies_the_batch_and_records_every_item_outcome()
    {
        var executor = new RecordingExecutor
        {
            Results =
            [
                new BulkCurationItemResult(FirstAssetId, "succeeded", 2, null),
                new BulkCurationItemResult(
                    SecondAssetId,
                    "conflict",
                    null,
                    "asset_version_conflict"),
            ],
        };
        var audit = new RecordingAudit();

        JobHandlerResult result = await Handler(executor, audit).HandleAsync(
            Job(Payload()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, Assert.Single(executor.Invocations).Now);
        AuditRecord record = Assert.Single(audit.Records);
        Assert.Equal("gallery.curation.bulk", record.Action);
        Assert.Equal(TenantId, record.TenantId.Value);
        Assert.Equal(ActorId.ToString("D"), record.Actor.Identifier);
        Assert.Equal(AuditOutcome.Failed, record.Outcome);
        Assert.Equal("setFavorite", record.Before.Fields["action"]);
        Assert.Equal("2", record.Before.Fields["requested"]);
        Assert.Equal("succeeded:v2", record.After.Fields[$"asset:{FirstAssetId:D}"]);
        Assert.Equal(
            "conflict:asset_version_conflict",
            record.After.Fields[$"asset:{SecondAssetId:D}"]);
    }

    [Fact]
    public async Task Handler_reports_success_when_every_item_succeeds()
    {
        var executor = new RecordingExecutor
        {
            Results =
            [
                new BulkCurationItemResult(FirstAssetId, "succeeded", 2, null),
                new BulkCurationItemResult(SecondAssetId, "succeeded", 3, null),
            ],
        };
        var audit = new RecordingAudit();

        JobHandlerResult result = await Handler(executor, audit).HandleAsync(
            Job(Payload()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AuditOutcome.Succeeded, Assert.Single(audit.Records).Outcome);
    }

    [Fact]
    public async Task Handler_fails_for_retry_when_an_item_is_unavailable()
    {
        var executor = new RecordingExecutor
        {
            Results =
            [
                new BulkCurationItemResult(FirstAssetId, "succeeded", 2, null),
                new BulkCurationItemResult(
                    SecondAssetId,
                    "failed",
                    null,
                    "curation_unavailable"),
            ],
        };
        var audit = new RecordingAudit();

        JobHandlerResult result = await Handler(executor, audit).HandleAsync(
            Job(Payload()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            JobFailureReason.ProviderUnavailable,
            result.Failure!.Reason);
        Assert.Single(audit.Records);
    }

    [Fact]
    public async Task Handler_refuses_malformed_foreign_and_cross_tenant_job_rows()
    {
        string trusted = GalleryCurationJobContracts.SerializeBulk(Payload());
        DurableJob[] rejected =
        [
            Job(Payload(), payload: "{\"items\":[]}"),
            Job(Payload(), payload: "not-json"),
            Job(Payload(), payloadVersion: 1),
            Job(Payload(), type: new JobType("GalleryCurationBulkV2")),
            Job(Payload(tenantId: Guid.CreateVersion7()), tenantId: TenantId),
            Job(Payload(), payload: trusted, tenantId: Guid.CreateVersion7()),
        ];
        var executor = new RecordingExecutor();
        var audit = new RecordingAudit();

        foreach (DurableJob job in rejected)
        {
            JobHandlerResult result = await Handler(executor, audit)
                .HandleAsync(job, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(JobFailureReason.ProcessingFailed, result.Failure!.Reason);
        }

        Assert.Empty(executor.Invocations);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task Handler_propagates_cancellation_without_recording_an_outcome()
    {
        var executor = new RecordingExecutor { ThrowOnCancellation = true };
        var audit = new RecordingAudit();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Handler(executor, audit).HandleAsync(
                Job(Payload()),
                cancellation.Token));

        Assert.Empty(audit.Records);
    }

    [Fact]
    public void Worker_composition_registers_one_scoped_bulk_curation_handler()
    {
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "Sqlite",
                    ["Persistence:ConnectionString"] = "Data Source=:memory:",
                    ["Worker:InstanceId"] = "curation-registration-test",
                })
                .Build());

        ServiceDescriptor executor = Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IGalleryCurationBulkExecutor));
        ServiceDescriptor service = Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(GalleryCurationBulkService));
        ServiceDescriptor handler = Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IJobHandler) &&
                descriptor.ImplementationType ==
                    typeof(GalleryCurationBulkJobHandler));
        Assert.Equal(ServiceLifetime.Scoped, executor.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, service.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);
    }

    private static GalleryCurationBulkJobHandler Handler(
        IGalleryCurationBulkExecutor executor,
        IAuditWriter audit) =>
        new(new GalleryCurationBulkService(
            executor,
            audit,
            new FixedUuid7Generator(),
            new FixedClock(Now)));

    private static GalleryCurationBulkJobPayload Payload(Guid? tenantId = null) =>
        new(
            tenantId ?? TenantId,
            ActorId,
            actorCanManageAll: false,
            new BulkCurationAction("setFavorite", null, null, true),
            [
                new BulkCurationTarget(FirstAssetId, 1),
                new BulkCurationTarget(SecondAssetId, 1),
            ]);

    private static DurableJob Job(
        GalleryCurationBulkJobPayload envelope,
        string? payload = null,
        int? payloadVersion = null,
        JobType? type = null,
        Guid? tenantId = null) =>
        DurableJob.Create(
            new JobId(Guid.CreateVersion7()),
            new JobTenantId(tenantId ?? envelope.TenantId),
            type ?? GalleryCurationJobContracts.BulkType,
            payload ?? GalleryCurationJobContracts.SerializeBulk(envelope),
            payloadVersion ?? GalleryCurationJobContracts.PayloadVersion,
            new JobDedupeKey($"gallery-curation:{Guid.NewGuid():N}"),
            priority: 0,
            maxAttempts: 5,
            availableAtUtc: Now,
            createdAtUtc: Now);

    private sealed class RecordingExecutor : IGalleryCurationBulkExecutor
    {
        internal List<(CurationActor Actor, BulkCurationRequest Request, DateTimeOffset Now)>
            Invocations
        { get; } = [];

        internal IReadOnlyList<BulkCurationItemResult> Results { get; init; } =
            [new BulkCurationItemResult(FirstAssetId, "succeeded", 2, null)];

        internal bool ThrowOnCancellation { get; init; }

        public ValueTask<IReadOnlyList<BulkCurationItemResult>> ExecuteBulkAsync(
            CurationActor actor,
            BulkCurationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (ThrowOnCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Invocations.Add((actor, request, now));
            return ValueTask.FromResult(Results);
        }
    }

    private sealed class RecordingAudit : IAuditWriter
    {
        internal List<AuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedUuid7Generator : IUuid7Generator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }
}
