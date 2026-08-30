using Vistara.Application.Common;
using Vistara.Application.Common.Storage;

namespace Vistara.Worker.Features.Reconciliation.Uploads;

public sealed class UploadReconciliationService
{
    private readonly IUploadReconciliationStatePort _state;
    private readonly IUploadReconciliationStoragePort _storage;
    private readonly IClock _clock;
    private readonly UploadReconciliationOptions _options;
    private readonly IUploadReconciliationObserver _observer;
    private readonly IUploadReconciliationCheckpointObserver _checkpoints;

    public UploadReconciliationService(
        IUploadReconciliationStatePort state,
        IUploadReconciliationStoragePort storage,
        IClock clock,
        UploadReconciliationOptions options,
        IUploadReconciliationObserver? observer = null,
        IUploadReconciliationCheckpointObserver? checkpoints = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _observer = observer ?? NullUploadReconciliationObserver.Instance;
        _checkpoints = checkpoints ?? NullUploadReconciliationCheckpointObserver.Instance;
    }

    public async ValueTask<UploadReconciliationReport> RunAsync(
        UploadReconciliationRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset utcNow = _clock.UtcNow;
        var run = new RunState(request.DryRun, _options, _observer);
        string? startingCursor = request.DryRun
            ? request.Cursor
            : await _state.LoadCheckpointAsync(
                request.TenantId,
                request.RunId,
                cancellationToken) ?? request.Cursor;
        UploadReconciliationPage page = await _state.ScanAsync(
            new UploadReconciliationScanRequest(
                startingCursor,
                request.RunId,
                _options.MaximumSessionsPerRun,
                utcNow,
                utcNow - _options.MinimumObjectAge,
                _options.LeaseDuration,
                request.DryRun,
                request.TenantId),
            cancellationToken);
        run.Scanned = page.Candidates.Count;

        string? stableCursor = startingCursor;
        try
        {
            foreach (UploadReconciliationCandidate candidate in page.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessResult processed = await ProcessAsync(
                    candidate,
                    utcNow,
                    run,
                    cancellationToken);
                if (processed == ProcessResult.StorageBudgetExhausted)
                {
                    break;
                }

                stableCursor = candidate.ContinuationCursor;
                if (!request.DryRun)
                {
                    await _state.SaveCheckpointAsync(
                        request.RunId,
                        stableCursor,
                        cancellationToken);
                    await CheckpointAsync(
                        ReconciliationCheckpoint.CursorSaved,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!request.DryRun)
            {
                await _state.SaveCheckpointAsync(
                    request.RunId,
                    stableCursor,
                    CancellationToken.None);
            }

            throw;
        }

        return run.CreateReport(stableCursor);
    }

    private async ValueTask<ProcessResult> ProcessAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        UploadReconciliationCandidate? current = await _state.RevalidateAsync(
            candidate.Fence,
            utcNow,
            cancellationToken);
        if (current is null)
        {
            run.Stale(ReconciliationActionKind.ExpireSession);
            return ProcessResult.Completed;
        }

        run.Revalidated++;
        await CheckpointAsync(
            ReconciliationCheckpoint.CandidateRevalidated,
            cancellationToken);

        bool abandoned = IsAged(current.UpdatedAtUtc, utcNow);
        switch (current.State)
        {
            case UploadReconciliationSessionState.Pending
                when current.ExpiresAtUtc <= utcNow:
                return await ExpireAsync(current, utcNow, run, cancellationToken);
            case UploadReconciliationSessionState.Expired:
            case UploadReconciliationSessionState.Aborted:
            case UploadReconciliationSessionState.Accepted:
            case UploadReconciliationSessionState.Quarantined:
                return await CleanupStagingAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
            case UploadReconciliationSessionState.Committing
                when abandoned:
            case UploadReconciliationSessionState.OutcomeUnknownCommit
                when abandoned:
                return await ReconcileCommitAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
            case UploadReconciliationSessionState.Aborting
                when abandoned:
            case UploadReconciliationSessionState.OutcomeUnknownAbort
                when abandoned:
                return await ReconcileAbortAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
            case UploadReconciliationSessionState.CommitRequested
                when abandoned:
            case UploadReconciliationSessionState.Verifying
                when abandoned:
            case UploadReconciliationSessionState.Promoting
                when abandoned:
            case UploadReconciliationSessionState.Reconciling
                when abandoned:
                return await RecoverIngestAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
            default:
                return ProcessResult.Completed;
        }
    }

    private async ValueTask<ProcessResult> RecoverIngestAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (candidate.CanonicalKey is not null)
        {
            if (!run.TryUseStorageOperation())
            {
                return ProcessResult.StorageBudgetExhausted;
            }

            UploadReconciliationHeadResult canonical;
            try
            {
                canonical = await _storage.VerifyAsync(
                    candidate.CanonicalKey,
                    candidate.ExpectedSizeBytes,
                    cancellationToken);
            }
            catch (Exception exception) when (IsTransientProviderFailure(exception))
            {
                run.Deferred(ReconciliationActionKind.InspectCanonical);
                return ProcessResult.Completed;
            }

            run.Applied(ReconciliationActionKind.InspectCanonical);
            await CheckpointAsync(
                ReconciliationCheckpoint.ObjectInspected,
                cancellationToken);
            if (canonical.Status == UploadReconciliationHeadStatus.Retry)
            {
                run.Deferred(ReconciliationActionKind.InspectCanonical);
                return ProcessResult.Completed;
            }

            if (canonical.Status == UploadReconciliationHeadStatus.Found)
            {
                UploadReconciliationObjectHead head = canonical.Head
                    ?? throw new InvalidOperationException(
                        "Found canonical verification lacks data.");
                if (!CanonicalMatches(candidate, head))
                {
                    return await QuarantineAsync(
                        candidate,
                        ReconciliationQuarantineReason.CanonicalMismatch,
                        utcNow,
                        run,
                        cancellationToken);
                }

                return await PreserveCanonicalAsync(
                    candidate,
                    head.Identity,
                    utcNow,
                    run,
                    cancellationToken);
            }
        }

        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        UploadReconciliationHeadResult staging;
        try
        {
            staging = await _storage.HeadAsync(
                candidate.StagingKey,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.InspectStaging);
            return ProcessResult.Completed;
        }

        run.Applied(ReconciliationActionKind.InspectStaging);
        await CheckpointAsync(
            ReconciliationCheckpoint.ObjectInspected,
            cancellationToken);
        if (staging.Status == UploadReconciliationHeadStatus.Retry)
        {
            run.Deferred(ReconciliationActionKind.InspectStaging);
            return ProcessResult.Completed;
        }

        if (staging.Status == UploadReconciliationHeadStatus.Missing)
        {
            return await QuarantineAsync(
                candidate,
                ReconciliationQuarantineReason.OwnershipMismatch,
                utcNow,
                run,
                cancellationToken);
        }

        UploadReconciliationObjectHead stagingHead = staging.Head
            ?? throw new InvalidOperationException("Found staging HEAD lacks data.");
        if (!CompletedStagingMatches(candidate, stagingHead))
        {
            return await QuarantineAsync(
                candidate,
                ReconciliationQuarantineReason.OwnershipMismatch,
                utcNow,
                run,
                cancellationToken);
        }

        if (run.DryRun)
        {
            run.Planned(ReconciliationActionKind.ResumeIngest);
            return ProcessResult.Completed;
        }

        UploadReconciliationMutationResult resumed =
            await _state.ResumeIngestAsync(
                candidate.Fence,
                utcNow,
                cancellationToken);
        if (resumed.Status == UploadReconciliationMutationStatus.Stale)
        {
            run.Stale(ReconciliationActionKind.ResumeIngest);
            return ProcessResult.Completed;
        }

        run.Applied(ReconciliationActionKind.ResumeIngest);
        await CheckpointAsync(
            ReconciliationCheckpoint.SessionTransitioned,
            cancellationToken);
        return ProcessResult.Completed;
    }

    private async ValueTask<ProcessResult> ExpireAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        UploadReconciliationCandidate current = candidate;
        if (candidate.ProviderUploadId is null &&
            candidate.MultipartIssuanceId is not null &&
            candidate.ExpectedContentType is not null &&
            candidate.MultipartPartPlanLifetime is not null)
        {
            if (run.DryRun)
            {
                run.Planned(ReconciliationActionKind.InspectMultipart);
                run.Planned(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Completed;
            }

            if (!run.TryUseStorageOperation())
            {
                return ProcessResult.StorageBudgetExhausted;
            }

            UploadReconciliationMultipartRecovery recovered =
                await _storage.RecoverMultipartAsync(
                    new UploadReconciliationMultipartIssuance(
                        candidate.Fence.TenantId,
                        candidate.Fence.UploadSessionId,
                        candidate.MultipartIssuanceId,
                        candidate.StagingKey,
                        candidate.ExpectedSizeBytes,
                        candidate.ExpectedContentType,
                        candidate.ExpiresAtUtc,
                        candidate.MultipartPartPlanLifetime.Value),
                    cancellationToken);
            if (recovered.Retry || recovered.Session is null)
            {
                run.Deferred(ReconciliationActionKind.InspectMultipart);
                return ProcessResult.Completed;
            }

            UploadReconciliationMutationResult recorded =
                await _state.RecordMultipartIssuedForAbortAsync(
                    candidate.Fence,
                    recovered.Session,
                    utcNow,
                    cancellationToken);
            if (recorded.Status == UploadReconciliationMutationStatus.Stale ||
                recorded.Current is null)
            {
                run.Stale(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Completed;
            }

            current = recorded.Current;
            await CheckpointAsync(
                ReconciliationCheckpoint.SessionTransitioned,
                cancellationToken);
        }

        if (TryCreateMultipart(current, out UploadReconciliationMultipart multipart))
        {
            if (run.DryRun)
            {
                run.Planned(ReconciliationActionKind.ExpireSession);
                run.SessionsExpired++;
            }
            else
            {
                UploadReconciliationMutationResult mutation =
                    await _state.PrepareAbortAsync(
                        current.Fence,
                        utcNow,
                        cancellationToken);
                if (mutation.Status == UploadReconciliationMutationStatus.Stale ||
                    mutation.Current is null)
                {
                    run.Stale(ReconciliationActionKind.ExpireSession);
                    return ProcessResult.Completed;
                }

                current = mutation.Current;
                run.Applied(ReconciliationActionKind.ExpireSession);
                run.SessionsExpired++;
                await CheckpointAsync(
                    ReconciliationCheckpoint.SessionTransitioned,
                    cancellationToken);
            }

            return await AbortExpiredMultipartAsync(
                current,
                multipart,
                utcNow,
                run,
                cancellationToken);
        }

        if (candidate.State != UploadReconciliationSessionState.Expired)
        {
            if (run.DryRun)
            {
                run.Planned(ReconciliationActionKind.ExpireSession);
                run.SessionsExpired++;
            }
            else
            {
                UploadReconciliationMutationResult mutation =
                    await _state.ExpireAsync(
                        candidate.Fence,
                        utcNow,
                        cancellationToken);
                if (mutation.Status == UploadReconciliationMutationStatus.Stale ||
                    mutation.Current is null)
                {
                    run.Stale(ReconciliationActionKind.ExpireSession);
                    return ProcessResult.Completed;
                }

                current = mutation.Current;
                run.Applied(ReconciliationActionKind.ExpireSession);
                run.SessionsExpired++;
                await CheckpointAsync(
                    ReconciliationCheckpoint.SessionTransitioned,
                    cancellationToken);
            }
        }

        return await CleanupStagingAsync(
            current,
            utcNow,
            run,
            cancellationToken);
    }

    private async ValueTask<ProcessResult> AbortExpiredMultipartAsync(
        UploadReconciliationCandidate candidate,
        UploadReconciliationMultipart multipart,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (run.DryRun)
        {
            run.MultipartAborted++;
            run.Planned(ReconciliationActionKind.AbortMultipart);
            return await CompleteAbortAsync(
                candidate,
                utcNow,
                run,
                cancellationToken);
        }

        UploadReconciliationCandidate? current = await _state.RevalidateAsync(
            candidate.Fence,
            utcNow,
            cancellationToken);
        if (current is null ||
            !TryCreateMultipart(current, out multipart))
        {
            run.Stale(ReconciliationActionKind.AbortMultipart);
            return ProcessResult.Completed;
        }

        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        ReconciliationProviderMutationOutcome outcome;
        try
        {
            outcome = await _storage.AbortMultipartAsync(
                multipart,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.AbortMultipart);
            return ProcessResult.Deferred;
        }

        switch (outcome)
        {
            case ReconciliationProviderMutationOutcome.Succeeded:
            case ReconciliationProviderMutationOutcome.Missing:
                run.MultipartAborted++;
                run.Applied(ReconciliationActionKind.AbortMultipart);
                await CheckpointAsync(
                    ReconciliationCheckpoint.MultipartAborted,
                    cancellationToken);
                return await CompleteAbortAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
            case ReconciliationProviderMutationOutcome.OutcomeUnknown:
                _ = await _state.RecordAbortOutcomeUnknownAsync(
                    current.Fence,
                    utcNow,
                    cancellationToken);
                run.Deferred(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Deferred;
            case ReconciliationProviderMutationOutcome.Stale:
                run.Stale(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Deferred;
            case ReconciliationProviderMutationOutcome.Retry:
                run.Deferred(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Deferred;
            default:
                throw new InvalidOperationException(
                    "The provider mutation outcome is invalid.");
        }
    }

    private async ValueTask<ProcessResult> ReconcileAbortAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        UploadReconciliationCandidate abortCandidate = candidate;
        bool recoveredIssuance = false;
        if (!TryCreateMultipart(
                abortCandidate,
                out UploadReconciliationMultipart multipart) &&
            abortCandidate.MultipartIssuanceId is not null &&
            abortCandidate.ExpectedContentType is not null &&
            abortCandidate.MultipartPartPlanLifetime is not null)
        {
            if (!run.TryUseStorageOperation())
            {
                return ProcessResult.StorageBudgetExhausted;
            }

            UploadReconciliationMultipartRecovery recovered =
                await _storage.RecoverMultipartAsync(
                    new UploadReconciliationMultipartIssuance(
                        abortCandidate.Fence.TenantId,
                        abortCandidate.Fence.UploadSessionId,
                        abortCandidate.MultipartIssuanceId,
                        abortCandidate.StagingKey,
                        abortCandidate.ExpectedSizeBytes,
                        abortCandidate.ExpectedContentType,
                        abortCandidate.ExpiresAtUtc,
                        abortCandidate.MultipartPartPlanLifetime.Value),
                    cancellationToken);
            if (recovered.Retry || recovered.Session is null)
            {
                run.Deferred(ReconciliationActionKind.InspectMultipart);
                return ProcessResult.Completed;
            }

            UploadReconciliationMutationResult recorded =
                await _state.RecordMultipartIssuedForAbortAsync(
                    abortCandidate.Fence,
                    recovered.Session,
                    utcNow,
                    cancellationToken);
            if (recorded.Status == UploadReconciliationMutationStatus.Stale ||
                recorded.Current is null)
            {
                run.Stale(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Completed;
            }

            abortCandidate = recorded.Current;
            recoveredIssuance = true;
            if (!TryCreateMultipart(abortCandidate, out multipart))
            {
                run.Stale(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Completed;
            }
        }

        if (multipart is null)
        {
            return await QuarantineAsync(
                abortCandidate,
                ReconciliationQuarantineReason.OwnershipMismatch,
                utcNow,
                run,
                cancellationToken);
        }

        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        ReconciliationMultipartState providerState;
        try
        {
            providerState = await _storage.InspectMultipartAsync(
                multipart,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.InspectMultipart);
            return ProcessResult.Completed;
        }

        run.Applied(ReconciliationActionKind.InspectMultipart);
        await CheckpointAsync(
            ReconciliationCheckpoint.MultipartInspected,
            cancellationToken);

        switch (providerState)
        {
            case ReconciliationMultipartState.Completed:
                return await ReconcileCommitAsync(
                    abortCandidate,
                    utcNow,
                    run,
                    cancellationToken,
                    multipartAlreadyInspected: true);
            case ReconciliationMultipartState.Aborted:
            case ReconciliationMultipartState.Missing:
                return await CompleteAbortAsync(
                    abortCandidate,
                    utcNow,
                    run,
                    cancellationToken);
            case ReconciliationMultipartState.Retry:
                run.Deferred(ReconciliationActionKind.InspectMultipart);
                return ProcessResult.Completed;
            case ReconciliationMultipartState.Unknown:
            case ReconciliationMultipartState.Active:
                break;
            default:
                throw new InvalidOperationException("The multipart state is invalid.");
        }

        if (run.DryRun)
        {
            run.MultipartAborted++;
            run.Planned(ReconciliationActionKind.AbortMultipart);
            return await CompleteAbortAsync(
                abortCandidate,
                utcNow,
                run,
                cancellationToken);
        }

        UploadReconciliationCandidate? current = await _state.RevalidateAsync(
            abortCandidate.Fence,
            utcNow,
            cancellationToken);
        if (current is null ||
            (!recoveredIssuance && !IsAged(current.UpdatedAtUtc, utcNow)) ||
            !TryCreateMultipart(current, out multipart))
        {
            run.Stale(ReconciliationActionKind.AbortMultipart);
            return ProcessResult.Completed;
        }

        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        ReconciliationProviderMutationOutcome outcome;
        try
        {
            outcome = await _storage.AbortMultipartAsync(
                multipart,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.AbortMultipart);
            return ProcessResult.Completed;
        }

        switch (outcome)
        {
            case ReconciliationProviderMutationOutcome.Succeeded:
            case ReconciliationProviderMutationOutcome.Missing:
                run.MultipartAborted++;
                run.Applied(ReconciliationActionKind.AbortMultipart);
                await CheckpointAsync(
                    ReconciliationCheckpoint.MultipartAborted,
                    cancellationToken);
                return await CompleteAbortAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
            case ReconciliationProviderMutationOutcome.OutcomeUnknown:
                UploadReconciliationMutationResult recorded =
                    await _state.RecordAbortOutcomeUnknownAsync(
                        current.Fence,
                        utcNow,
                        cancellationToken);
                if (recorded.Status == UploadReconciliationMutationStatus.Stale)
                {
                    run.Stale(ReconciliationActionKind.AbortMultipart);
                }
                else
                {
                    run.Deferred(ReconciliationActionKind.AbortMultipart);
                }

                return ProcessResult.Completed;
            case ReconciliationProviderMutationOutcome.Stale:
                run.Stale(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Completed;
            case ReconciliationProviderMutationOutcome.Retry:
                run.Deferred(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Completed;
            default:
                throw new InvalidOperationException(
                    "The provider mutation outcome is invalid.");
        }
    }

    private async ValueTask<ProcessResult> CompleteAbortAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        UploadReconciliationCandidate current = candidate;
        if (!run.DryRun)
        {
            UploadReconciliationMutationResult mutation =
                await _state.CompleteAbortAsync(
                    candidate.Fence,
                    utcNow,
                    cancellationToken);
            if (mutation.Status == UploadReconciliationMutationStatus.Stale ||
                mutation.Current is null)
            {
                run.Stale(ReconciliationActionKind.AbortMultipart);
                return ProcessResult.Completed;
            }

            current = mutation.Current;
            await CheckpointAsync(
                ReconciliationCheckpoint.SessionTransitioned,
                cancellationToken);
        }

        return await CleanupStagingAsync(
            current,
            utcNow,
            run,
            cancellationToken);
    }

    private async ValueTask<ProcessResult> ReconcileCommitAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken,
        bool multipartAlreadyInspected = false)
    {
        if (candidate.MultipartSession is not null &&
            candidate.CanonicalKey is null)
        {
            ProcessResult? completed =
                await ReconcileCompletedMultipartAsync(
                    candidate,
                    utcNow,
                    run,
                    cancellationToken);
            if (completed.HasValue)
            {
                return completed.Value;
            }
        }

        if (candidate.CanonicalKey is not null)
        {
            if (!run.TryUseStorageOperation())
            {
                return ProcessResult.StorageBudgetExhausted;
            }

            UploadReconciliationHeadResult canonical;
            try
            {
                canonical = await _storage.VerifyAsync(
                    candidate.CanonicalKey,
                    candidate.ExpectedSizeBytes,
                    cancellationToken);
            }
            catch (Exception exception) when (IsTransientProviderFailure(exception))
            {
                run.Deferred(ReconciliationActionKind.InspectCanonical);
                return ProcessResult.Completed;
            }

            run.Applied(ReconciliationActionKind.InspectCanonical);
            await CheckpointAsync(
                ReconciliationCheckpoint.ObjectInspected,
                cancellationToken);
            if (canonical.Status == UploadReconciliationHeadStatus.Retry)
            {
                run.Deferred(ReconciliationActionKind.InspectCanonical);
                return ProcessResult.Completed;
            }

            if (canonical.Status == UploadReconciliationHeadStatus.Found)
            {
                UploadReconciliationObjectHead head = canonical.Head
                    ?? throw new InvalidOperationException("Found HEAD result lacks data.");
                if (!CanonicalMatches(candidate, head))
                {
                    return await QuarantineAsync(
                        candidate,
                        ReconciliationQuarantineReason.CanonicalMismatch,
                        utcNow,
                        run,
                        cancellationToken);
                }

                return await PreserveCanonicalAsync(
                    candidate,
                    head.Identity,
                    utcNow,
                    run,
                    cancellationToken);
            }
        }

        if (!TryCreateMultipart(candidate, out UploadReconciliationMultipart multipart))
        {
            run.Deferred(ReconciliationActionKind.InspectCanonical);
            return ProcessResult.Completed;
        }

        ReconciliationMultipartState providerState;
        if (multipartAlreadyInspected)
        {
            providerState = ReconciliationMultipartState.Completed;
        }
        else
        {
            if (!run.TryUseStorageOperation())
            {
                return ProcessResult.StorageBudgetExhausted;
            }

            try
            {
                providerState = await _storage.InspectMultipartAsync(
                    multipart,
                    cancellationToken);
            }
            catch (Exception exception) when (IsTransientProviderFailure(exception))
            {
                run.Deferred(ReconciliationActionKind.InspectMultipart);
                return ProcessResult.Completed;
            }

            run.Applied(ReconciliationActionKind.InspectMultipart);
            await CheckpointAsync(
                ReconciliationCheckpoint.MultipartInspected,
                cancellationToken);
        }

        return providerState switch
        {
            ReconciliationMultipartState.Aborted or
            ReconciliationMultipartState.Missing =>
                await CompleteAbortAsync(
                    candidate,
                    utcNow,
                    run,
                    cancellationToken),
            ReconciliationMultipartState.Completed =>
                await QuarantineAsync(
                    candidate,
                    ReconciliationQuarantineReason.CompletedMultipartMissingCanonical,
                    utcNow,
                    run,
                    cancellationToken),
            ReconciliationMultipartState.Active
                when candidate.MultipartSession is not null &&
                     candidate.CompletionParts.Count > 0 =>
                await RetryMultipartCompletionAsync(
                    candidate,
                    utcNow,
                    run,
                    cancellationToken),
            ReconciliationMultipartState.Active or
            ReconciliationMultipartState.Unknown or
            ReconciliationMultipartState.Retry =>
                Deferred(run, ReconciliationActionKind.InspectMultipart),
            _ => throw new InvalidOperationException("The multipart state is invalid."),
        };
    }

    private async ValueTask<ProcessResult> RetryMultipartCompletionAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (run.DryRun)
        {
            run.Planned(ReconciliationActionKind.CompleteMultipart);
            return ProcessResult.Completed;
        }

        UploadReconciliationCandidate? current = await _state.RevalidateAsync(
            candidate.Fence,
            utcNow,
            cancellationToken);
        if (current is null ||
            !TryCreateMultipart(current, out UploadReconciliationMultipart multipart) ||
            multipart.Session is null ||
            multipart.CompletionParts.Count == 0)
        {
            run.Stale(ReconciliationActionKind.CompleteMultipart);
            return ProcessResult.Completed;
        }

        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        ReconciliationProviderMutationOutcome outcome;
        try
        {
            outcome = await _storage.CompleteMultipartAsync(
                multipart,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.CompleteMultipart);
            return ProcessResult.Completed;
        }

        switch (outcome)
        {
            case ReconciliationProviderMutationOutcome.Succeeded:
                ProcessResult? completed = await ReconcileCompletedMultipartAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
                if (completed.HasValue)
                {
                    return completed.Value;
                }

                run.Deferred(ReconciliationActionKind.CompleteMultipart);
                return ProcessResult.Completed;
            case ReconciliationProviderMutationOutcome.OutcomeUnknown:
            case ReconciliationProviderMutationOutcome.Retry:
            case ReconciliationProviderMutationOutcome.Missing:
                run.Deferred(ReconciliationActionKind.CompleteMultipart);
                return ProcessResult.Completed;
            case ReconciliationProviderMutationOutcome.Stale:
                run.Stale(ReconciliationActionKind.CompleteMultipart);
                return ProcessResult.Completed;
            default:
                throw new InvalidOperationException(
                    "The provider mutation outcome is invalid.");
        }
    }

    private async ValueTask<ProcessResult?> ReconcileCompletedMultipartAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        UploadReconciliationHeadResult result;
        try
        {
            result = await _storage.HeadAsync(
                candidate.StagingKey,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.InspectStaging);
            return ProcessResult.Completed;
        }

        run.Applied(ReconciliationActionKind.InspectStaging);
        await CheckpointAsync(
            ReconciliationCheckpoint.ObjectInspected,
            cancellationToken);
        if (result.Status == UploadReconciliationHeadStatus.Retry)
        {
            run.Deferred(ReconciliationActionKind.InspectStaging);
            return ProcessResult.Completed;
        }

        if (result.Status == UploadReconciliationHeadStatus.Missing)
        {
            return null;
        }

        UploadReconciliationObjectHead head = result.Head
            ?? throw new InvalidOperationException("Found HEAD result lacks data.");
        if (!CompletedStagingMatches(candidate, head))
        {
            return await QuarantineAsync(
                candidate,
                ReconciliationQuarantineReason.OwnershipMismatch,
                utcNow,
                run,
                cancellationToken);
        }

        if (run.DryRun)
        {
            run.MultipartCompleted++;
            run.Planned(ReconciliationActionKind.CompleteMultipart);
            return ProcessResult.Completed;
        }

        UploadReconciliationMutationResult mutation =
            await _state.CompleteCommitAsync(
                candidate.Fence,
                head.Identity,
                utcNow,
                cancellationToken);
        if (mutation.Status == UploadReconciliationMutationStatus.Stale)
        {
            run.Stale(ReconciliationActionKind.CompleteMultipart);
            return ProcessResult.Completed;
        }

        run.MultipartCompleted++;
        run.Applied(ReconciliationActionKind.CompleteMultipart);
        await CheckpointAsync(
            ReconciliationCheckpoint.SessionTransitioned,
            cancellationToken);
        return ProcessResult.Completed;
    }

    private async ValueTask<ProcessResult> PreserveCanonicalAsync(
        UploadReconciliationCandidate candidate,
        BlobIdentity canonicalIdentity,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        UploadReconciliationCandidate current = candidate;
        if (run.DryRun)
        {
            run.CanonicalPreserved++;
            run.Planned(ReconciliationActionKind.PreserveCanonical);
        }
        else
        {
            UploadReconciliationMutationResult mutation =
                await _state.PreserveCanonicalAsync(
                    candidate.Fence,
                    canonicalIdentity,
                    utcNow,
                    cancellationToken);
            if (mutation.Status == UploadReconciliationMutationStatus.Stale ||
                mutation.Current is null)
            {
                run.Stale(ReconciliationActionKind.PreserveCanonical);
                return ProcessResult.Completed;
            }

            current = mutation.Current;
            run.CanonicalPreserved++;
            run.Applied(ReconciliationActionKind.PreserveCanonical);
            await CheckpointAsync(
                ReconciliationCheckpoint.SessionTransitioned,
                cancellationToken);
        }

        return ProcessResult.Completed;
    }

    private async ValueTask<ProcessResult> CleanupStagingAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (!IsAged(candidate.CreatedAtUtc, utcNow) ||
            !IsAged(candidate.UpdatedAtUtc, utcNow))
        {
            return ProcessResult.Completed;
        }

        if (!IsOwnedStagingKey(candidate))
        {
            return await QuarantineAsync(
                candidate,
                ReconciliationQuarantineReason.UnsafeStagingKey,
                utcNow,
                run,
                cancellationToken);
        }

        UploadReconciliationCandidate current = candidate;
        if (!run.DryRun)
        {
            UploadReconciliationCandidate? revalidated = await _state.RevalidateAsync(
                candidate.Fence,
                utcNow,
                cancellationToken);
            if (revalidated is null ||
                revalidated.StagingKey != candidate.StagingKey ||
                revalidated.ExpectedStagingVersion != candidate.ExpectedStagingVersion ||
                !IsAged(revalidated.CreatedAtUtc, utcNow) ||
                !IsAged(revalidated.UpdatedAtUtc, utcNow) ||
                !IsOwnedStagingKey(revalidated))
            {
                run.Stale(ReconciliationActionKind.DeleteStaging);
                return ProcessResult.Completed;
            }

            current = revalidated;
        }

        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        UploadReconciliationHeadResult result;
        try
        {
            result = await _storage.HeadAsync(
                current.StagingKey,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.InspectStaging);
            return ProcessResult.Completed;
        }

        run.Applied(ReconciliationActionKind.InspectStaging);
        await CheckpointAsync(
            ReconciliationCheckpoint.ObjectInspected,
            cancellationToken);
        if (result.Status == UploadReconciliationHeadStatus.Retry)
        {
            run.Deferred(ReconciliationActionKind.InspectStaging);
            return ProcessResult.Completed;
        }

        if (result.Status == UploadReconciliationHeadStatus.Missing)
        {
            if (run.DryRun)
            {
                PlanReservationRelease(current, run);
            }
            else
            {
                await CompleteCleanupStateAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
            }

            return ProcessResult.Completed;
        }

        UploadReconciliationObjectHead head = result.Head
            ?? throw new InvalidOperationException("Found HEAD result lacks data.");
        if (!StagingHeadIsOwned(current, head))
        {
            return await QuarantineAsync(
                current,
                ReconciliationQuarantineReason.OwnershipMismatch,
                utcNow,
                run,
                cancellationToken);
        }

        if (!IsAged(head.LastModifiedUtc, utcNow))
        {
            return ProcessResult.Completed;
        }

        if (run.DryRun)
        {
            run.Planned(ReconciliationActionKind.DeleteStaging);
            run.StagingDeleted++;
            PlanReservationRelease(current, run);
            return ProcessResult.Completed;
        }

        if (!run.TryUseStorageOperation())
        {
            return ProcessResult.StorageBudgetExhausted;
        }

        ReconciliationProviderMutationOutcome deletion;
        try
        {
            deletion = await _storage.DeleteStagingAsync(
                head.Identity,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            run.Deferred(ReconciliationActionKind.DeleteStaging);
            return ProcessResult.Completed;
        }

        switch (deletion)
        {
            case ReconciliationProviderMutationOutcome.Succeeded:
                run.StagingDeleted++;
                run.Applied(ReconciliationActionKind.DeleteStaging);
                await CheckpointAsync(
                    ReconciliationCheckpoint.StagingDeleted,
                    cancellationToken);
                await CompleteCleanupStateAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
                break;
            case ReconciliationProviderMutationOutcome.Missing:
                run.AlreadyApplied(ReconciliationActionKind.DeleteStaging);
                await CompleteCleanupStateAsync(
                    current,
                    utcNow,
                    run,
                    cancellationToken);
                break;
            case ReconciliationProviderMutationOutcome.Stale:
                run.Stale(ReconciliationActionKind.DeleteStaging);
                break;
            case ReconciliationProviderMutationOutcome.OutcomeUnknown:
            case ReconciliationProviderMutationOutcome.Retry:
                run.Deferred(ReconciliationActionKind.DeleteStaging);
                break;
            default:
                throw new InvalidOperationException(
                    "The provider mutation outcome is invalid.");
        }

        return ProcessResult.Completed;
    }

    private async ValueTask CompleteCleanupStateAsync(
        UploadReconciliationCandidate candidate,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        UploadReconciliationMutationResult mutation =
            await _state.CompleteCleanupAsync(
                candidate.Fence,
                utcNow,
                cancellationToken);
        if (mutation.Status == UploadReconciliationMutationStatus.Stale)
        {
            run.Stale(ReconciliationActionKind.DeleteStaging);
        }
        else if (mutation.ReservationReleased)
        {
            run.ReservationsReleased++;
            run.Applied(ReconciliationActionKind.ReleaseReservation);
        }
    }

    private static void PlanReservationRelease(
        UploadReconciliationCandidate candidate,
        RunState run)
    {
        if (!candidate.ReservationReleased &&
            candidate.State != UploadReconciliationSessionState.Accepted)
        {
            run.ReservationsReleased++;
            run.Planned(ReconciliationActionKind.ReleaseReservation);
        }
    }

    private async ValueTask<ProcessResult> QuarantineAsync(
        UploadReconciliationCandidate candidate,
        ReconciliationQuarantineReason reason,
        DateTimeOffset utcNow,
        RunState run,
        CancellationToken cancellationToken)
    {
        if (run.DryRun)
        {
            run.Quarantined++;
            run.Refused(ReconciliationActionKind.Quarantine);
            return ProcessResult.Completed;
        }

        UploadReconciliationMutationResult mutation = await _state.QuarantineAsync(
            candidate.Fence,
            reason,
            utcNow,
            cancellationToken);
        if (mutation.Status == UploadReconciliationMutationStatus.Stale)
        {
            run.Stale(ReconciliationActionKind.Quarantine);
            return ProcessResult.Completed;
        }

        run.Quarantined++;
        run.Applied(ReconciliationActionKind.Quarantine);
        await CheckpointAsync(
            ReconciliationCheckpoint.Quarantined,
            cancellationToken);
        return ProcessResult.Completed;
    }

    private bool IsAged(DateTimeOffset timestamp, DateTimeOffset utcNow) =>
        timestamp <= utcNow - _options.MinimumObjectAge;

    private static bool TryCreateMultipart(
        UploadReconciliationCandidate candidate,
        out UploadReconciliationMultipart multipart)
    {
        if (candidate.ProviderUploadId is null ||
            !IsOwnedStagingKey(candidate))
        {
            multipart = null!;
            return false;
        }

        multipart = new UploadReconciliationMultipart(
            candidate.Fence.TenantId,
            candidate.Fence.UploadSessionId,
            candidate.ProviderUploadId,
            candidate.StagingKey,
            candidate.MultipartSession,
            candidate.CompletionParts);
        return true;
    }

    private static bool IsOwnedStagingKey(UploadReconciliationCandidate candidate)
    {
        string[] segments = candidate.StagingKey.Value.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4 &&
            segments[0] == "staging" &&
            segments[1].Length is >= 1 and <= 8 &&
            segments[1].All(IsLowerAsciiOrDigit) &&
            Guid.TryParseExact(segments[2], "D", out Guid tenantId) &&
            tenantId == candidate.Fence.TenantId &&
            Guid.TryParseExact(segments[3], "D", out Guid uploadSessionId) &&
            uploadSessionId == candidate.Fence.UploadSessionId;
    }

    private static bool IsLowerAsciiOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool StagingHeadIsOwned(
        UploadReconciliationCandidate candidate,
        UploadReconciliationObjectHead head) =>
        head.Identity.Key == candidate.StagingKey &&
        head.OwnerTenantId == candidate.Fence.TenantId &&
        head.OwnerUploadSessionId == candidate.Fence.UploadSessionId &&
        (candidate.ExpectedStagingVersion is null ||
            head.Identity.Version == candidate.ExpectedStagingVersion);

    private static bool CompletedStagingMatches(
        UploadReconciliationCandidate candidate,
        UploadReconciliationObjectHead head) =>
        head.Identity.Key == candidate.StagingKey &&
        head.OwnerTenantId == candidate.Fence.TenantId &&
        head.OwnerUploadSessionId == candidate.Fence.UploadSessionId &&
        head.ContentLength == candidate.ExpectedSizeBytes &&
        (head.Sha256 is null ||
         head.Sha256 == candidate.ExpectedSha256) &&
        (candidate.ExpectedContentType is null ||
         head.ContentType == candidate.ExpectedContentType);

    private static bool CanonicalMatches(
        UploadReconciliationCandidate candidate,
        UploadReconciliationObjectHead head) =>
        candidate.CanonicalKey is not null &&
        head.Identity.Key == candidate.CanonicalKey &&
        head.OwnerTenantId == candidate.Fence.TenantId &&
        (!candidate.CanonicalRequiresUploadOwnership ||
         head.OwnerUploadSessionId == candidate.Fence.UploadSessionId) &&
        head.ContentLength == candidate.ExpectedSizeBytes &&
        head.Sha256 == candidate.ExpectedSha256 &&
        (candidate.ExpectedContentType is null ||
         head.ContentType == candidate.ExpectedContentType);

    private static bool IsTransientProviderFailure(Exception exception) =>
        exception is TimeoutException or BlobStoreException;

    private ValueTask CheckpointAsync(
        ReconciliationCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        _checkpoints.ReachedAsync(checkpoint, cancellationToken);

    private static ProcessResult Deferred(
        RunState run,
        ReconciliationActionKind action)
    {
        run.Deferred(action);
        return ProcessResult.Completed;
    }

    private enum ProcessResult
    {
        Completed,
        Deferred,
        StorageBudgetExhausted,
    }

    private sealed class RunState
    {
        private readonly UploadReconciliationOptions _options;
        private readonly IUploadReconciliationObserver _observer;
        private readonly List<UploadReconciliationAction> _actions = [];

        internal RunState(
            bool dryRun,
            UploadReconciliationOptions options,
            IUploadReconciliationObserver observer)
        {
            DryRun = dryRun;
            _options = options;
            _observer = observer;
        }

        internal bool DryRun { get; }

        internal int Scanned { get; set; }

        internal int Revalidated { get; set; }

        internal int ReservationsReleased { get; set; }

        internal int SessionsExpired { get; set; }

        internal int MultipartAborted { get; set; }

        internal int MultipartCompleted { get; set; }

        internal int StagingDeleted { get; set; }

        internal int CanonicalPreserved { get; set; }

        internal int Quarantined { get; set; }

        internal int DeferredCount { get; private set; }

        internal int StaleCount { get; private set; }

        internal int StorageOperations { get; private set; }

        internal bool TryUseStorageOperation()
        {
            if (StorageOperations >= _options.MaximumStorageOperationsPerRun)
            {
                return false;
            }

            StorageOperations++;
            return true;
        }

        internal void Planned(ReconciliationActionKind action) =>
            Record(action, ReconciliationActionOutcome.Planned);

        internal void Applied(ReconciliationActionKind action) =>
            Record(action, ReconciliationActionOutcome.Applied);

        internal void AlreadyApplied(ReconciliationActionKind action) =>
            Record(action, ReconciliationActionOutcome.AlreadyApplied);

        internal void Deferred(ReconciliationActionKind action)
        {
            DeferredCount++;
            Record(action, ReconciliationActionOutcome.Deferred);
        }

        internal void Stale(ReconciliationActionKind action)
        {
            StaleCount++;
            Record(action, ReconciliationActionOutcome.Stale);
        }

        internal void Refused(ReconciliationActionKind action) =>
            Record(action, ReconciliationActionOutcome.Refused);

        internal UploadReconciliationReport CreateReport(string? continuationCursor) =>
            new(
                DryRun,
                continuationCursor,
                new UploadReconciliationCounts(
                    Scanned,
                    Revalidated,
                    ReservationsReleased,
                    SessionsExpired,
                    MultipartAborted,
                    MultipartCompleted,
                    StagingDeleted,
                    CanonicalPreserved,
                    Quarantined,
                    DeferredCount,
                    StaleCount,
                    StorageOperations),
                _actions.AsReadOnly());

        private void Record(
            ReconciliationActionKind action,
            ReconciliationActionOutcome outcome)
        {
            _observer.Record(action, outcome);
            if (_actions.Count < _options.MaximumReportedActions)
            {
                _actions.Add(UploadReconciliationAction.Redacted(action, outcome));
            }
        }
    }
}
