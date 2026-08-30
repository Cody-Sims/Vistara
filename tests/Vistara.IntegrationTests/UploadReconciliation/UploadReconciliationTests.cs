using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Idempotency;
using Vistara.Domain.Assets;
using Vistara.Domain.Jobs;
using Vistara.IntegrationTests.UploadPersistence;
using Vistara.Worker.Features.Reconciliation.Uploads;
using Vistara.Worker.Runtime.Jobs;
using Xunit;

namespace Vistara.IntegrationTests.UploadReconciliation;

public sealed class UploadReconciliationTests
{
    [Fact]
    public async Task Expired_session_releases_quota_once_and_deletes_only_aged_owned_staging()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Expired(aged: true);

        UploadReconciliationReport first = await scenario.RunAsync();
        UploadReconciliationReport second = await scenario.RunAsync();

        Assert.Equal(1, first.Counts.ReservationsReleased);
        Assert.Equal(1, first.Counts.StagingDeleted);
        Assert.Equal(0, second.Counts.ReservationsReleased);
        Assert.Equal(1, scenario.State.ReservationReleaseCount);
        Assert.False(scenario.Storage.Contains(scenario.Candidate.StagingKey));
    }

    public sealed class UploadReconciliationJobHandlerTests
    {
        [Fact]
        public async Task Scheduled_job_runs_one_bounded_reconciliation_page()
        {
            var state = new TestReconciliationState([]);
            var storage = new TestReconciliationStorage();
            var service = new UploadReconciliationService(
                state,
                storage,
                new TestClock(UploadReconciliationTestsTime.UtcNow),
                new UploadReconciliationOptions());
            var handler = new UploadReconciliationJobHandler(service);
            DurableJob job = CreateJob("""{"cursor":null,"dryRun":false}""");

            JobHandlerResult result = await handler.HandleAsync(job, CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task Scheduled_job_rejects_malformed_payload_without_running()
        {
            var state = new TestReconciliationState([]);
            var service = new UploadReconciliationService(
                state,
                new TestReconciliationStorage(),
                new TestClock(UploadReconciliationTestsTime.UtcNow),
                new UploadReconciliationOptions());
            var handler = new UploadReconciliationJobHandler(service);
            DurableJob job = CreateJob("{not-json");

            JobHandlerResult result = await handler.HandleAsync(job, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(JobFailureReason.ProcessingFailed, result.Failure?.Reason);
            Assert.Equal(0, state.MutationCount);
        }

        private static DurableJob CreateJob(string payload) =>
            DurableJob.Create(
                new JobId(Guid.CreateVersion7()),
                new JobTenantId(Guid.CreateVersion7()),
                UploadReconciliationJobHandler.SupportedJobType,
                payload,
                payloadVersion: 1,
                new JobDedupeKey($"upload-reconciliation-{Guid.CreateVersion7():N}"),
                priority: 0,
                maxAttempts: 3,
                UploadReconciliationTestsTime.UtcNow,
                UploadReconciliationTestsTime.UtcNow);
    }

    [Fact]
    public async Task Fresh_staging_is_never_deleted_even_when_session_is_expired()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Expired(aged: false);

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(0, report.Counts.ReservationsReleased);
        Assert.Equal(0, report.Counts.StagingDeleted);
        Assert.True(scenario.Storage.Contains(scenario.Candidate.StagingKey));
    }

    [Fact]
    public async Task Expired_multipart_keeps_abort_required_state_and_quota_until_abort_is_confirmed()
    {
        ReconciliationScenario scenario = ReconciliationScenario.ExpiredMultipart();
        scenario.Storage.AbortOutcome = ReconciliationProviderMutationOutcome.Retry;

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(0, report.Counts.ReservationsReleased);
        Assert.Equal(0, scenario.State.ReservationReleaseCount);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
        Assert.Equal(
            UploadReconciliationSessionState.Aborting,
            scenario.State.Current(scenario.Candidate.Fence.UploadSessionId).State);
    }

    [Fact]
    public async Task Abandoned_multipart_is_aborted_before_owned_staging_is_deleted()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Aborting();

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.MultipartAborted);
        Assert.Equal(1, report.Counts.ReservationsReleased);
        Assert.Equal(1, report.Counts.StagingDeleted);
        Assert.Equal(
            [ReconciliationCheckpoint.MultipartInspected,
             ReconciliationCheckpoint.MultipartAborted,
             ReconciliationCheckpoint.SessionTransitioned,
             ReconciliationCheckpoint.ObjectInspected,
             ReconciliationCheckpoint.StagingDeleted],
            scenario.Checkpoints.Reached.Where(IsCoreStep));
    }

    [Fact]
    public async Task Ambiguous_complete_preserves_verified_canonical_object_and_session()
    {
        ReconciliationScenario scenario = ReconciliationScenario.UnknownCommit();
        scenario.Storage.MultipartState = ReconciliationMultipartState.Completed;
        scenario.Storage.AddCanonical(scenario.Candidate, matching: true);

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.CanonicalPreserved);
        Assert.Equal(0, report.Counts.Quarantined);
        Assert.True(scenario.State.CanonicalPreserved);
        Assert.True(scenario.Storage.Contains(scenario.Candidate.CanonicalKey!));
        Assert.Equal(0, scenario.Storage.DeleteCanonicalAttempts);
    }

    [Fact]
    public async Task Ambiguous_complete_quarantines_canonical_mismatch_without_deleting_it()
    {
        ReconciliationScenario scenario = ReconciliationScenario.UnknownCommit();
        scenario.Storage.MultipartState = ReconciliationMultipartState.Completed;
        scenario.Storage.AddCanonical(scenario.Candidate, matching: false);

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.Quarantined);
        Assert.True(scenario.State.Quarantined);
        Assert.True(scenario.Storage.Contains(scenario.Candidate.CanonicalKey!));
        Assert.Equal(0, scenario.Storage.DeleteCanonicalAttempts);
    }

    [Theory]
    [InlineData(ReconciliationMultipartState.Aborted)]
    [InlineData(ReconciliationMultipartState.Missing)]
    public async Task Ambiguous_abort_rechecks_provider_then_completes_idempotently(
        ReconciliationMultipartState providerState)
    {
        ReconciliationScenario scenario = ReconciliationScenario.UnknownAbort();
        scenario.Storage.MultipartState = providerState;

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.ReservationsReleased);
        Assert.Equal(0, scenario.Storage.AbortCalls);
        Assert.Equal(1, scenario.State.ReservationReleaseCount);
    }

    [Fact]
    public async Task Outcome_unknown_abort_is_recorded_and_rechecked_on_restart()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Aborting();
        scenario.Storage.AbortOutcome =
            ReconciliationProviderMutationOutcome.OutcomeUnknown;

        UploadReconciliationReport first = await scenario.RunAsync();
        scenario.Storage.MultipartState = ReconciliationMultipartState.Aborted;
        UploadReconciliationReport second = await scenario.RunAsync();

        Assert.Equal(1, first.Counts.Deferred);
        Assert.Equal(1, scenario.Storage.AbortCalls);
        Assert.Equal(1, second.Counts.ReservationsReleased);
        Assert.Equal(1, scenario.State.ReservationReleaseCount);
    }

    [Fact]
    public async Task Provider_timeout_defers_without_state_or_storage_mutation()
    {
        ReconciliationScenario scenario = ReconciliationScenario.UnknownAbort();
        scenario.Storage.MultipartState = ReconciliationMultipartState.Retry;

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.Deferred);
        Assert.Equal(0, scenario.Storage.AbortCalls);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
        Assert.Equal(0, scenario.State.MutationCount);
    }

    [Fact]
    public async Task Canonical_head_timeout_defers_unknown_commit_without_transition()
    {
        ReconciliationScenario scenario = ReconciliationScenario.UnknownCommit();
        scenario.Storage.HeadReturnsRetry = true;

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.Deferred);
        Assert.Equal(0, scenario.State.MutationCount);
    }

    [Fact]
    public async Task Session_version_theft_immediately_before_abort_prevents_provider_mutation()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Aborting();
        scenario.State.StealOnRevalidation = 2;

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.Stale);
        Assert.Equal(0, scenario.Storage.AbortCalls);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
    }

    [Fact]
    public async Task Session_version_theft_immediately_before_delete_prevents_deletion()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Expired(aged: true);
        scenario.State.StealOnRevalidation = 2;

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(0, report.Counts.ReservationsReleased);
        Assert.Equal(1, report.Counts.Stale);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
        Assert.True(scenario.Storage.Contains(scenario.Candidate.StagingKey));
    }

    [Fact]
    public async Task Dry_run_has_no_mutating_side_effects_and_reports_redacted_bounded_actions()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Expired(aged: true);
        scenario.Options = scenario.Options with { MaximumReportedActions = 1 };

        UploadReconciliationReport report = await scenario.RunAsync(dryRun: true);

        Assert.True(report.DryRun);
        Assert.Single(report.Actions);
        Assert.DoesNotContain(
            scenario.Candidate.Fence.UploadSessionId.ToString("D"),
            report.Actions[0].ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            scenario.Candidate.StagingKey.Value,
            report.Actions[0].ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, scenario.State.MutationCount);
        Assert.Equal(0, scenario.Storage.AbortCalls);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
    }

    [Fact]
    public async Task Multipart_dry_run_reports_the_full_safe_action_chain_without_mutation()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Aborting();

        UploadReconciliationReport report = await scenario.RunAsync(dryRun: true);

        Assert.Equal(1, report.Counts.MultipartAborted);
        Assert.Equal(1, report.Counts.ReservationsReleased);
        Assert.Equal(1, report.Counts.StagingDeleted);
        Assert.Contains(
            report.Actions,
            item => item.Action == ReconciliationActionKind.AbortMultipart &&
                item.Outcome == ReconciliationActionOutcome.Planned);
        Assert.Contains(
            report.Actions,
            item => item.Action == ReconciliationActionKind.DeleteStaging &&
                item.Outcome == ReconciliationActionOutcome.Planned);
        Assert.Equal(0, scenario.State.MutationCount);
        Assert.Equal(0, scenario.Storage.AbortCalls);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
    }

    [Theory]
    [InlineData("originals/aa/01991f9e-522b-7c80-a109-7f764ae57985/01991f9e-522b-7c80-a109-7f764ae57986/1/file.png")]
    [InlineData("derivatives/v1/source/recipe.webp")]
    [InlineData("staging/aa/01991f9e-522b-7c80-a109-7f764ae57985/01991f9e-522b-7c80-a109-7f764ae57987")]
    public async Task Cleanup_refuses_keys_not_provably_owned_by_the_session(string unsafeKey)
    {
        ReconciliationScenario scenario = ReconciliationScenario.Expired(aged: true);
        scenario.ReplaceStagingKey(new BlobKey(unsafeKey));

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.Quarantined);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
    }

    [Fact]
    public async Task Cleanup_refuses_object_without_matching_ownership_metadata()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Expired(aged: true);
        scenario.Storage.RemoveOwnershipMetadata(scenario.Candidate.StagingKey);

        UploadReconciliationReport report = await scenario.RunAsync();

        Assert.Equal(1, report.Counts.Quarantined);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
    }

    [Fact]
    public async Task Restart_after_each_checkpoint_converges_without_double_release()
    {
        ReconciliationCheckpoint[] checkpoints =
        [
            ReconciliationCheckpoint.CandidateRevalidated,
            ReconciliationCheckpoint.MultipartInspected,
            ReconciliationCheckpoint.MultipartAborted,
            ReconciliationCheckpoint.SessionTransitioned,
            ReconciliationCheckpoint.ObjectInspected,
            ReconciliationCheckpoint.StagingDeleted,
            ReconciliationCheckpoint.CursorSaved,
        ];
        foreach (ReconciliationCheckpoint checkpoint in checkpoints)
        {
            ReconciliationScenario scenario = ReconciliationScenario.Aborting();
            scenario.Checkpoints.CrashOnceAt = checkpoint;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await scenario.RunAsync());
            scenario.Checkpoints.CrashOnceAt = null;

            _ = await scenario.RunAsync();

            Assert.Equal(1, scenario.State.ReservationReleaseCount);
            Assert.True(scenario.State.IsTerminal);
            Assert.False(scenario.Storage.Contains(scenario.Candidate.StagingKey));
        }
    }

    [Fact]
    public async Task Stable_pagination_respects_session_and_storage_operation_limits()
    {
        ReconciliationScenario scenario = ReconciliationScenario.ManyExpired(5);
        scenario.Options = scenario.Options with
        {
            MaximumSessionsPerRun = 2,
            MaximumStorageOperationsPerRun = 4,
        };

        UploadReconciliationReport first = await scenario.RunAsync();
        scenario.Cursor = first.ContinuationCursor;
        UploadReconciliationReport second = await scenario.RunAsync();

        Assert.Equal(2, first.Counts.Scanned);
        Assert.Equal("cursor-2", first.ContinuationCursor);
        Assert.Equal(2, second.Counts.Scanned);
        Assert.Equal("cursor-4", second.ContinuationCursor);
        Assert.True(first.Counts.StorageOperations <= 4);
        Assert.True(second.Counts.StorageOperations <= 4);
    }

    [Fact]
    public async Task Cancellation_saves_last_stable_cursor_and_restart_continues()
    {
        ReconciliationScenario scenario = ReconciliationScenario.ManyExpired(3);
        scenario.RunId = Guid.CreateVersion7();
        scenario.Checkpoints.CancelAtItem = 2;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await scenario.RunAsync());

        Assert.Equal("cursor-1", scenario.State.SavedCursor);
        scenario.Checkpoints.CancelAtItem = null;
        UploadReconciliationReport restarted = await scenario.RunAsync();

        Assert.Equal(2, restarted.Counts.Scanned);
        Assert.Equal("cursor-3", restarted.ContinuationCursor);
    }

    [Fact]
    public async Task Metrics_use_only_safe_action_and_outcome_dimensions()
    {
        ReconciliationScenario scenario = ReconciliationScenario.Expired(aged: true);

        _ = await scenario.RunAsync();

        Assert.NotEmpty(scenario.Observer.Records);
        Assert.All(scenario.Observer.Records, record =>
        {
            Assert.True(Enum.IsDefined(record.Action));
            Assert.True(Enum.IsDefined(record.Outcome));
        });
    }

    [Fact]
    public async Task Production_reconciliation_completes_an_ambiguous_multipart_commit_after_restart()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        storage.BeforeCompleteMultipartAsync = _ =>
            throw new BlobStoreException(
                BlobStoreErrorCode.OutcomeUnknown,
                "The provider response was lost before completion was observed.");
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                MultipartRequest(tenantId, actorId, uploadId, "reconcile-complete"),
                CancellationToken.None);
            UploadSessionSnapshot issued = (await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None)).Session;
            storage.ObservedMultipartParts =
            [
                new UploadedPart(
                    1,
                    new BlobEntityTag("etag-1"),
                    new BlobChecksum(
                        BlobChecksumAlgorithm.Sha256,
                        new string('a', 64)),
                    20_000_000),
            ];
            UploadCommitResult ambiguous = await application.CommitAsync(
                issued,
                [new CommittedUploadPart(
                    1,
                    "etag-1",
                    new string('a', 64),
                    20_000_000)],
                new IdempotencyKey("reconcile-complete-commit"),
                issued.Version,
                CancellationToken.None);
            Assert.Equal(UploadCommitStatus.OutcomeUnknown, ambiguous.Status);
        }

        using ServiceProvider worker = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(2)),
            UploadPersistenceDatabase.Now.AddHours(2),
            addUploadReconciliation: true);
        await using AsyncServiceScope workerScope = worker.CreateAsyncScope();
        Assert.NotNull(
            workerScope.ServiceProvider
                .GetRequiredService<IUploadReconciliationStatePort>());
        Assert.NotNull(
            workerScope.ServiceProvider
                .GetRequiredService<IUploadReconciliationStoragePort>());
        Assert.NotNull(
            worker.GetRequiredService<UploadReconciliationScheduleMetadata>());
        UploadReconciliationReport report = await workerScope.ServiceProvider
            .GetRequiredService<UploadReconciliationService>()
            .RunAsync(
                new UploadReconciliationRunRequest(
                    tenantId,
                    Guid.CreateVersion7(),
                    cursor: null,
                    dryRun: false),
                CancellationToken.None);

        Assert.Equal(1, report.Counts.MultipartCompleted);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Equal(
            "CommitRequested",
            await context.UploadSessions.Select(row => row.State).SingleAsync());
        Assert.Equal(1, await database.CountAsync("jobs"));
    }

    [Fact]
    public async Task Production_reconciliation_finishes_an_ambiguous_abort_after_restart()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new()
        {
            AbortOutcomeUnknown = true,
        };
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                MultipartRequest(tenantId, actorId, uploadId, "reconcile-abort"),
                CancellationToken.None);
            UploadSessionSnapshot issued = (await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None)).Session;
            UploadAbortResult ambiguous = await application.AbortAsync(
                issued,
                issued.Version,
                CancellationToken.None);
            Assert.Equal(UploadAbortStatus.Unavailable, ambiguous.Status);
        }

        using (ServiceProvider worker = database.CreateWorkerProvider(
                   storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(2)),
                   UploadPersistenceDatabase.Now.AddHours(2),
                   addUploadReconciliation: true))
        {
            await using AsyncServiceScope workerScope = worker.CreateAsyncScope();
            UploadReconciliationReport report = await workerScope.ServiceProvider
                .GetRequiredService<UploadReconciliationService>()
                .RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        Guid.CreateVersion7(),
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None);
            Assert.Equal(1, report.Counts.MultipartAborted);
            Assert.Equal(0, report.Counts.ReservationsReleased);
        }

        using (ServiceProvider cleanup = database.CreateWorkerProvider(
                   storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(4)),
                   UploadPersistenceDatabase.Now.AddHours(4),
                   addUploadReconciliation: true))
        {
            await using AsyncServiceScope cleanupScope = cleanup.CreateAsyncScope();
            UploadReconciliationReport report = await cleanupScope.ServiceProvider
                .GetRequiredService<UploadReconciliationService>()
                .RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        Guid.CreateVersion7(),
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None);
            Assert.Equal(1, report.Counts.ReservationsReleased);
        }

        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Equal(
            "Aborted",
            await context.UploadSessions.Select(row => row.State).SingleAsync());
        Assert.Equal(
            "Released",
            await context.QuotaReservations.Select(row => row.State).SingleAsync());
    }

    [Fact]
    public async Task Production_reconciliation_recovers_and_aborts_crashed_multipart_issuance()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                MultipartRequest(
                    tenantId,
                    actorId,
                    uploadId,
                    "issuance-crash"),
                CancellationToken.None);
            storage.AfterBeginMultipartAsync = _ =>
                ValueTask.FromException(
                    new OperationCanceledException(
                        "Injected crash after multipart creation."));
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await application.IssueAsync(
                    reserved.Session!,
                    CancellationToken.None));
        }

        Assert.Equal(1, storage.ActiveMultipartSessions);
        using (ServiceProvider abortWorker = database.CreateWorkerProvider(
                   storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(2)),
                   UploadPersistenceDatabase.Now.AddHours(2),
                   addUploadReconciliation: true))
        {
            await using AsyncServiceScope scope = abortWorker.CreateAsyncScope();
            UploadReconciliationReport report = await scope.ServiceProvider
                .GetRequiredService<UploadReconciliationService>()
                .RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        Guid.CreateVersion7(),
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None);
            Assert.Equal(1, report.Counts.MultipartAborted);
            Assert.Equal(0, report.Counts.ReservationsReleased);
        }

        Assert.Equal(0, storage.ActiveMultipartSessions);
        using (ServiceProvider cleanupWorker = database.CreateWorkerProvider(
                   storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(4)),
                   UploadPersistenceDatabase.Now.AddHours(4),
                   addUploadReconciliation: true))
        {
            await using AsyncServiceScope scope = cleanupWorker.CreateAsyncScope();
            UploadReconciliationReport report = await scope.ServiceProvider
                .GetRequiredService<UploadReconciliationService>()
                .RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        Guid.CreateVersion7(),
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None);
            Assert.Equal(1, report.Counts.ReservationsReleased);
        }

        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Equal(
            "Aborted",
            await context.UploadSessions.Select(row => row.State).SingleAsync());
        Assert.Equal(
            "Released",
            await context.QuotaReservations.Select(row => row.State).SingleAsync());
    }

    [Fact]
    public async Task Production_reconciliation_cursor_does_not_skip_rows_that_leave_the_scan()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        storage.BeforeCompleteMultipartAsync = _ =>
            throw new BlobStoreException(
                BlobStoreErrorCode.OutcomeUnknown,
                "The provider completion outcome is unknown.");
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            for (int index = 0; index < 2; index++)
            {
                Guid uploadId = Guid.CreateVersion7(
                    UploadPersistenceDatabase.Now.AddMilliseconds(index + 2));
                UploadReserveResult reserved = await application.ReserveAsync(
                    MultipartRequest(
                        tenantId,
                        actorId,
                        uploadId,
                        $"cursor-{index}"),
                    CancellationToken.None);
                UploadSessionSnapshot issued = (await application.IssueAsync(
                    reserved.Session!,
                    CancellationToken.None)).Session;
                storage.ObservedMultipartParts =
                [
                    new UploadedPart(
                        1,
                        new BlobEntityTag($"etag-{index}"),
                        new BlobChecksum(
                            BlobChecksumAlgorithm.Sha256,
                            new string('a', 64)),
                        20_000_000),
                ];
                UploadCommitResult ambiguous = await application.CommitAsync(
                    issued,
                    [new CommittedUploadPart(
                        1,
                        $"etag-{index}",
                        new string('a', 64),
                        20_000_000)],
                    new IdempotencyKey($"cursor-commit-{index}"),
                    issued.Version,
                    CancellationToken.None);
                Assert.Equal(
                    UploadCommitStatus.OutcomeUnknown,
                    ambiguous.Status);
            }
        }

        using ServiceProvider worker = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(2)),
            UploadPersistenceDatabase.Now.AddHours(2),
            addUploadReconciliation: true);
        await using AsyncServiceScope workerScope = worker.CreateAsyncScope();
        var service = new UploadReconciliationService(
            workerScope.ServiceProvider
                .GetRequiredService<IUploadReconciliationStatePort>(),
            workerScope.ServiceProvider
                .GetRequiredService<IUploadReconciliationStoragePort>(),
            workerScope.ServiceProvider.GetRequiredService<IClock>(),
            new UploadReconciliationOptions
            {
                MaximumSessionsPerRun = 1,
            });

        Guid runId = Guid.CreateVersion7();
        UploadReconciliationReport first = await service.RunAsync(
            new UploadReconciliationRunRequest(
                tenantId,
                runId,
                cursor: null,
                dryRun: false),
            CancellationToken.None);
        UploadReconciliationReport second = await service.RunAsync(
            new UploadReconciliationRunRequest(
                tenantId,
                runId,
                cursor: null,
                dryRun: false),
            CancellationToken.None);

        Assert.Equal(1, first.Counts.Scanned);
        Assert.Equal(1, second.Counts.Scanned);
        Assert.Equal(2, await database.CountAsync("jobs"));
    }

    [Fact]
    public async Task Production_reconciliation_does_not_lease_or_version_active_uploads()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                new ReserveUploadRequest(
                    tenantId,
                    actorId,
                    uploadId,
                    "direct",
                    "active.jpg",
                    1_000,
                    "image/jpeg",
                    new string('a', 64),
                    $"staging/{tenantId.ToString("N")[..2]}/{tenantId:D}/{uploadId:D}",
                    new string('b', 64),
                    new IdempotencyKey("active-upload"),
                    UploadPersistenceDatabase.Now.AddHours(1)),
                CancellationToken.None);
            Assert.Equal(1, reserved.Session?.Version);
        }

        using ServiceProvider worker = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now),
            UploadPersistenceDatabase.Now,
            addUploadReconciliation: true);
        await using AsyncServiceScope workerScope = worker.CreateAsyncScope();
        UploadReconciliationReport report = await workerScope.ServiceProvider
            .GetRequiredService<UploadReconciliationService>()
            .RunAsync(
                new UploadReconciliationRunRequest(
                    tenantId,
                    Guid.CreateVersion7(),
                    cursor: null,
                    dryRun: false),
                CancellationToken.None);

        Assert.Equal(0, report.Counts.Scanned);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Vistara.Persistence.Model.UploadSessionRow row =
            await context.UploadSessions.SingleAsync();
        Assert.Equal(1, row.Version);
        Assert.Null(row.ReconciliationLeaseToken);
    }

    [Fact]
    public async Task Production_reconciliation_two_replicas_cleanup_once()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                new ReserveUploadRequest(
                    tenantId,
                    actorId,
                    uploadId,
                    "direct",
                    "expired.jpg",
                    1_000,
                    "image/jpeg",
                    new string('a', 64),
                    $"staging/{tenantId.ToString("N")[..2]}/{tenantId:D}/{uploadId:D}",
                    new string('b', 64),
                    new IdempotencyKey("replica-cleanup"),
                    UploadPersistenceDatabase.Now.AddHours(1)),
                CancellationToken.None);
            UploadIssuance issued = await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None);
            storage.StoreUploaded(storage.LastDirectRequest!);
            Assert.Equal("uploadIssued", issued.Session.State);
        }

        using (ServiceProvider expiration = database.CreateWorkerProvider(
                   storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(2)),
                   UploadPersistenceDatabase.Now.AddHours(2),
                   addUploadReconciliation: true))
        {
            await using AsyncServiceScope scope = expiration.CreateAsyncScope();
            _ = await scope.ServiceProvider
                .GetRequiredService<UploadReconciliationService>()
                .RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        Guid.CreateVersion7(),
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None);
        }

        using ServiceProvider first = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(4)),
            UploadPersistenceDatabase.Now.AddHours(4),
            addUploadReconciliation: true);
        using ServiceProvider second = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(4)),
            UploadPersistenceDatabase.Now.AddHours(4),
            addUploadReconciliation: true);
        await using AsyncServiceScope firstScope = first.CreateAsyncScope();
        await using AsyncServiceScope secondScope = second.CreateAsyncScope();

        UploadReconciliationReport[] reports = await Task.WhenAll(
            firstScope.ServiceProvider
                .GetRequiredService<UploadReconciliationService>()
                .RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        Guid.CreateVersion7(),
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None)
                .AsTask(),
            secondScope.ServiceProvider
                .GetRequiredService<UploadReconciliationService>()
                .RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        Guid.CreateVersion7(),
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(1, reports.Sum(report => report.Counts.ReservationsReleased));
        Assert.Equal(1, reports.Sum(report => report.Counts.StagingDeleted));
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Equal(
            "Released",
            await context.QuotaReservations.Select(row => row.State).SingleAsync());
        Assert.NotNull(
            await context.UploadSessions
                .Select(row => row.CleanupCompletedAtUtc)
                .SingleAsync());
        Assert.False(storage.Contains(new BlobKey(
            $"staging/{tenantId.ToString("N")[..2]}/{tenantId:D}/{uploadId:D}")));
    }

    [Fact]
    public async Task Production_restart_reuses_its_unexpired_leases_before_advancing_cursor()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(2));
        Guid runId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(3));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            _ = await scope.ServiceProvider
                .GetRequiredService<IUploadApplicationPort>()
                .ReserveAsync(
                    new ReserveUploadRequest(
                        tenantId,
                        actorId,
                        uploadId,
                        "direct",
                        "expired.jpg",
                        1_000,
                        "image/jpeg",
                        new string('a', 64),
                        $"staging/{tenantId.ToString("N")[..2]}/{tenantId:D}/{uploadId:D}",
                        new string('b', 64),
                        new IdempotencyKey("lease-restart"),
                        UploadPersistenceDatabase.Now.AddHours(1)),
                    CancellationToken.None);
        }

        using ServiceProvider worker = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(2)),
            UploadPersistenceDatabase.Now.AddHours(2),
            addUploadReconciliation: true);
        var crash = new TestReconciliationCheckpoints
        {
            CrashOnceAt = ReconciliationCheckpoint.CandidateRevalidated,
        };
        await using (AsyncServiceScope scope = worker.CreateAsyncScope())
        {
            var service = new UploadReconciliationService(
                scope.ServiceProvider
                    .GetRequiredService<IUploadReconciliationStatePort>(),
                scope.ServiceProvider
                    .GetRequiredService<IUploadReconciliationStoragePort>(),
                scope.ServiceProvider.GetRequiredService<IClock>(),
                new UploadReconciliationOptions(),
                checkpoints: crash);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await service.RunAsync(
                    new UploadReconciliationRunRequest(
                        tenantId,
                        runId,
                        cursor: null,
                        dryRun: false),
                    CancellationToken.None));
        }

        await using AsyncServiceScope restarted = worker.CreateAsyncScope();
        UploadReconciliationReport report = await restarted.ServiceProvider
            .GetRequiredService<UploadReconciliationService>()
            .RunAsync(
                new UploadReconciliationRunRequest(
                    tenantId,
                    runId,
                    cursor: null,
                    dryRun: false),
                CancellationToken.None);

        Assert.Equal(1, report.Counts.Scanned);
        Assert.Equal(1, report.Counts.SessionsExpired);
    }

    [Fact]
    public async Task Production_reconciliation_marks_missing_expired_staging_as_cleaned()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(
            UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using (ServiceProvider api = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = api.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            _ = await application.ReserveAsync(
                new ReserveUploadRequest(
                    tenantId,
                    actorId,
                    uploadId,
                    "direct",
                    "expired.jpg",
                    1_000,
                    "image/jpeg",
                    new string('a', 64),
                    $"staging/{tenantId.ToString("N")[..2]}/{tenantId:D}/{uploadId:D}",
                    new string('b', 64),
                    new IdempotencyKey("expired-upload"),
                    UploadPersistenceDatabase.Now.AddHours(1)),
                CancellationToken.None);
        }

        using ServiceProvider expirationWorker = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(2)),
            UploadPersistenceDatabase.Now.AddHours(2),
            addUploadReconciliation: true);
        UploadReconciliationReport first;
        UploadReconciliationReport second;
        UploadReconciliationReport third;
        await using (AsyncServiceScope scope = expirationWorker.CreateAsyncScope())
        {
            UploadReconciliationService service =
                scope.ServiceProvider.GetRequiredService<
                    UploadReconciliationService>();
            first = await service.RunAsync(
                new UploadReconciliationRunRequest(
                    tenantId,
                    Guid.CreateVersion7(),
                    cursor: null,
                    dryRun: false),
                CancellationToken.None);
        }

        using ServiceProvider cleanupWorker = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(4)),
            UploadPersistenceDatabase.Now.AddHours(4),
            addUploadReconciliation: true);
        await using (AsyncServiceScope scope = cleanupWorker.CreateAsyncScope())
        {
            UploadReconciliationService service =
                scope.ServiceProvider.GetRequiredService<
                    UploadReconciliationService>();
            second = await service.RunAsync(
                new UploadReconciliationRunRequest(
                    tenantId,
                    Guid.CreateVersion7(),
                    cursor: null,
                    dryRun: false),
                CancellationToken.None);
        }

        using ServiceProvider completedWorker = database.CreateWorkerProvider(
            storage.CreateReplica(UploadPersistenceDatabase.Now.AddHours(6)),
            UploadPersistenceDatabase.Now.AddHours(6),
            addUploadReconciliation: true);
        await using (AsyncServiceScope scope = completedWorker.CreateAsyncScope())
        {
            UploadReconciliationService service =
                scope.ServiceProvider.GetRequiredService<
                    UploadReconciliationService>();
            third = await service.RunAsync(
                new UploadReconciliationRunRequest(
                    tenantId,
                    Guid.CreateVersion7(),
                    cursor: null,
                    dryRun: false),
                CancellationToken.None);
        }

        Assert.Equal(1, first.Counts.Scanned);
        Assert.Equal(1, second.Counts.Scanned);
        Assert.Equal(0, third.Counts.Scanned);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.NotNull(
            await context.UploadSessions
                .Select(row => row.CleanupCompletedAtUtc)
                .SingleAsync());
    }

    private static ReserveUploadRequest MultipartRequest(
        Guid tenantId,
        Guid actorId,
        Guid uploadId,
        string idempotencyKey) =>
        new(
            tenantId,
            actorId,
            uploadId,
            "multipart",
            "reconciliation.jpg",
            20_000_000,
            "image/jpeg",
            new string('a', 64),
            $"staging/{tenantId.ToString("N")[..2]}/{tenantId:D}/{uploadId:D}",
            new string('b', 64),
            new IdempotencyKey(idempotencyKey),
            UploadPersistenceDatabase.Now.AddHours(1));

    private static bool IsCoreStep(ReconciliationCheckpoint checkpoint) =>
        checkpoint is not ReconciliationCheckpoint.CandidateRevalidated
            and not ReconciliationCheckpoint.CursorSaved;
}

internal sealed class ReconciliationScenario
{
    private ReconciliationScenario(
        IReadOnlyList<UploadReconciliationCandidate> candidates,
        TestReconciliationStorage storage)
    {
        State = new TestReconciliationState(candidates);
        Storage = storage;
        Candidate = candidates[0];
    }

    internal UploadReconciliationCandidate Candidate { get; private set; }

    internal TestReconciliationState State { get; }

    internal TestReconciliationStorage Storage { get; }

    internal TestReconciliationCheckpoints Checkpoints { get; } = new();

    internal TestReconciliationObserver Observer { get; } = new();

    internal UploadReconciliationOptions Options { get; set; } = new();

    internal string? Cursor { get; set; }

    internal Guid? RunId { get; set; }

    internal static ReconciliationScenario Expired(bool aged)
    {
        UploadReconciliationCandidate candidate = CreateCandidate(
            UploadReconciliationSessionState.Pending,
            aged,
            providerUploadId: null);
        var storage = new TestReconciliationStorage();
        storage.AddStaging(candidate, aged);
        return new ReconciliationScenario([candidate], storage);
    }

    internal static ReconciliationScenario Aborting()
    {
        UploadReconciliationCandidate candidate = CreateCandidate(
            UploadReconciliationSessionState.Aborting,
            aged: true,
            providerUploadId: "provider-upload");
        var storage = new TestReconciliationStorage
        {
            MultipartState = ReconciliationMultipartState.Active,
        };
        storage.AddStaging(candidate, aged: true);
        return new ReconciliationScenario([candidate], storage);
    }

    internal static ReconciliationScenario ExpiredMultipart()
    {
        UploadReconciliationCandidate candidate = CreateCandidate(
            UploadReconciliationSessionState.Pending,
            aged: true,
            providerUploadId: "provider-upload");
        var storage = new TestReconciliationStorage
        {
            MultipartState = ReconciliationMultipartState.Active,
        };
        storage.AddStaging(candidate, aged: true);
        return new ReconciliationScenario([candidate], storage);
    }

    internal static ReconciliationScenario UnknownCommit()
    {
        UploadReconciliationCandidate candidate = CreateCandidate(
            UploadReconciliationSessionState.OutcomeUnknownCommit,
            aged: true,
            providerUploadId: "provider-upload");
        var storage = new TestReconciliationStorage();
        storage.AddStaging(candidate, aged: true);
        return new ReconciliationScenario([candidate], storage);
    }

    internal static ReconciliationScenario UnknownAbort()
    {
        UploadReconciliationCandidate candidate = CreateCandidate(
            UploadReconciliationSessionState.OutcomeUnknownAbort,
            aged: true,
            providerUploadId: "provider-upload");
        var storage = new TestReconciliationStorage();
        storage.AddStaging(candidate, aged: true);
        return new ReconciliationScenario([candidate], storage);
    }

    internal static ReconciliationScenario ManyExpired(int count)
    {
        UploadReconciliationCandidate[] candidates = Enumerable.Range(0, count)
            .Select(index => CreateCandidate(
                UploadReconciliationSessionState.Expired,
                aged: true,
                providerUploadId: null,
                suffix: index))
            .ToArray();
        var storage = new TestReconciliationStorage();
        foreach (UploadReconciliationCandidate candidate in candidates)
        {
            storage.AddStaging(candidate, aged: true);
        }

        return new ReconciliationScenario(candidates, storage);
    }

    internal async Task<UploadReconciliationReport> RunAsync(bool dryRun = false)
    {
        var service = new UploadReconciliationService(
            State,
            Storage,
            new FixedClock(UploadReconciliationTestsTime.UtcNow),
            Options,
            Observer,
            Checkpoints);
        return await service.RunAsync(
            new UploadReconciliationRunRequest(
                RunId ?? Guid.CreateVersion7(),
                Cursor,
                dryRun),
            CancellationToken.None);
    }

    internal void ReplaceStagingKey(BlobKey key)
    {
        UploadReconciliationCandidate replacement = Candidate with { StagingKey = key };
        State.Replace(Candidate.Fence.UploadSessionId, replacement);
        Storage.Move(Candidate.StagingKey, key);
        Candidate = replacement;
    }

    private static UploadReconciliationCandidate CreateCandidate(
        UploadReconciliationSessionState state,
        bool aged,
        string? providerUploadId,
        int suffix = 0)
    {
        Guid tenantId = Guid.Parse("01991f9e-522b-7c80-a109-7f764ae57985");
        Guid uploadId = Guid.CreateVersion7();
        DateTimeOffset created = aged
            ? UploadReconciliationTestsTime.UtcNow.AddHours(-8)
            : UploadReconciliationTestsTime.UtcNow.AddMinutes(-5);
        return new UploadReconciliationCandidate(
            new UploadReconciliationFence(
                tenantId,
                uploadId,
                version: 3,
                $"lease-{suffix}",
                UploadReconciliationTestsTime.UtcNow.AddMinutes(5)),
            state,
            created,
            created,
            UploadReconciliationTestsTime.UtcNow.AddHours(-1),
            new BlobKey($"staging/aa/{tenantId:D}/{uploadId:D}"),
            new BlobVersion($"staging-v{suffix}"),
            new BlobKey($"originals/aa/{tenantId:D}/{uploadId:D}/1/{uploadId:D}.png"),
            expectedSizeBytes: 17,
            new Sha256Checksum(new string('a', 64)),
            providerUploadId,
            reservationReleased: false,
            $"cursor-{suffix + 1}");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}

internal sealed class TestClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}
