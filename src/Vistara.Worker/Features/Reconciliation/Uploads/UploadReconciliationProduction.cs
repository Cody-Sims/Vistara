using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Domain.Assets;
using Vistara.Persistence.Uploads;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Reconciliation.Uploads;

public sealed record UploadReconciliationScheduleMetadata
{
    public static UploadReconciliationScheduleMetadata Default { get; } = new();

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    public bool DryRun { get; init; }

    public int PayloadVersion { get; init; } = 1;

    public string JobType { get; init; } =
        UploadReconciliationJobHandler.SupportedJobType.Value;
}

public static class UploadReconciliationServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraUploadReconciliation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<RelationalUploadReconciliationStore>();
        services.TryAddScoped<
            IUploadReconciliationStatePort,
            RelationalUploadReconciliationStateAdapter>();
        services.TryAddScoped<
            IUploadReconciliationStoragePort,
            BlobStoreUploadReconciliationStorageAdapter>();
        services.TryAddSingleton(new UploadReconciliationOptions());
        services.TryAddSingleton(UploadReconciliationScheduleMetadata.Default);
        services.TryAddScoped<UploadReconciliationService>();
        services.TryAddScoped<UploadReconciliationJobHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IJobHandler,
                UploadReconciliationJobHandler>());
        return services;
    }
}

internal sealed class RelationalUploadReconciliationStateAdapter(
    RelationalUploadReconciliationStore store,
    IClock clock) : IUploadReconciliationStatePort
{
    private readonly RelationalUploadReconciliationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private Guid? _activeTenantId;

    public async ValueTask<UploadReconciliationPage> ScanAsync(
        UploadReconciliationScanRequest request,
        CancellationToken cancellationToken)
    {
        EstablishTenant(request.TenantId);
        PersistedUploadReconciliationPage page = await _store.ScanAsync(
            request.TenantId,
            request.Cursor,
            request.MaximumSessions,
            request.UtcNow,
            request.LeaseDuration,
            request.DryRun,
            cancellationToken);
        return new UploadReconciliationPage(
            page.Candidates.Select(ToWorker).ToArray(),
            page.ContinuationCursor);
    }

    public async ValueTask<UploadReconciliationCandidate?> RevalidateAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        EstablishTenant(fence.TenantId);
        PersistedUploadReconciliationCandidate? candidate =
            await _store.RevalidateAsync(
                fence.TenantId,
                fence.UploadSessionId,
                fence.Version,
                fence.LeaseToken,
                fence.LeaseExpiresAtUtc,
                utcNow,
                cancellationToken);
        return candidate is null ? null : ToWorker(candidate);
    }

    public ValueTask<UploadReconciliationMutationResult> ExpireAndReleaseAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.ExpireAndReleaseAsync(
                candidate,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult>
        CompleteAbortAndReleaseAsync(
            UploadReconciliationFence fence,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.CompleteAbortAndReleaseAsync(
                candidate,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult>
        RecordAbortOutcomeUnknownAsync(
            UploadReconciliationFence fence,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.RecordAbortOutcomeUnknownAsync(
                candidate,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> CompleteCommitAsync(
        UploadReconciliationFence fence,
        BlobIdentity stagingIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.CompleteCommitAsync(
                candidate,
                stagingIdentity,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> CompleteCleanupAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.CompleteCleanupAsync(
                candidate,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> PreserveCanonicalAsync(
        UploadReconciliationFence fence,
        BlobIdentity canonicalIdentity,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) =>
                RelationalUploadReconciliationStore.PreserveCanonicalAsync(
                    candidate,
                    canonicalIdentity,
                    utcNow,
                    token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> QuarantineAsync(
        UploadReconciliationFence fence,
        ReconciliationQuarantineReason reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.QuarantineAsync(
                candidate,
                reason.ToString(),
                utcNow,
                token),
            cancellationToken);

    public ValueTask SaveCheckpointAsync(
        Guid runId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (_activeTenantId is not { } tenantId)
        {
            throw new InvalidOperationException(
                "A reconciliation tenant must be established before saving a cursor.");
        }

        return _store.SaveCheckpointAsync(
            tenantId,
            runId,
            cursor,
            _clock.UtcNow,
            cancellationToken);
    }

    private async ValueTask<UploadReconciliationMutationResult> MutateAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        Func<
            PersistedUploadReconciliationCandidate,
            CancellationToken,
            ValueTask<PersistedUploadReconciliationMutation>> mutation,
        CancellationToken cancellationToken)
    {
        EstablishTenant(fence.TenantId);
        PersistedUploadReconciliationCandidate? current =
            await _store.RevalidateAsync(
                fence.TenantId,
                fence.UploadSessionId,
                fence.Version,
                fence.LeaseToken,
                fence.LeaseExpiresAtUtc,
                utcNow,
                cancellationToken);
        if (current is null)
        {
            return UploadReconciliationMutationResult.Stale();
        }

        return ToWorker(await mutation(current, cancellationToken));
    }

    private void EstablishTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new InvalidOperationException(
                "Upload reconciliation requires a UUIDv7 tenant.");
        }

        if (_activeTenantId.HasValue && _activeTenantId.Value != tenantId)
        {
            throw new InvalidOperationException(
                "A reconciliation scope cannot switch tenants.");
        }

        _activeTenantId = tenantId;
    }

    private static UploadReconciliationCandidate ToWorker(
        PersistedUploadReconciliationCandidate candidate) =>
        new(
            new UploadReconciliationFence(
                candidate.TenantId,
                candidate.UploadSessionId,
                candidate.Version,
                candidate.LeaseToken,
                candidate.LeaseExpiresAtUtc),
            State(candidate),
            candidate.CreatedAtUtc,
            candidate.UpdatedAtUtc,
            candidate.ExpiresAtUtc,
            new BlobKey(candidate.StagingKey),
            candidate.StagingProviderVersion is null
                ? null
                : new BlobVersion(candidate.StagingProviderVersion),
            candidate.CanonicalKey is null
                ? null
                : new BlobKey(candidate.CanonicalKey),
            candidate.ExpectedSizeBytes,
            new Sha256Checksum(candidate.ExpectedSha256),
            candidate.MultipartSession?.UploadId,
            candidate.ReservationReleased,
            candidate.ContinuationCursor,
            new BlobMediaType(candidate.DeclaredContentType),
            candidate.MultipartSession,
            candidate.CompletionParts);

    private static UploadReconciliationSessionState State(
        PersistedUploadReconciliationCandidate candidate) =>
        candidate.State switch
        {
            "Pending" or "UploadIssued" =>
                UploadReconciliationSessionState.Pending,
            "Committing" => UploadReconciliationSessionState.Committing,
            "Aborting" => UploadReconciliationSessionState.Aborting,
            "Expired" => UploadReconciliationSessionState.Expired,
            "Aborted" => UploadReconciliationSessionState.Aborted,
            "Accepted" => UploadReconciliationSessionState.Accepted,
            "Rejected" => UploadReconciliationSessionState.Quarantined,
            "OutcomeUnknown" when candidate.LastKnownState == "Aborting" =>
                UploadReconciliationSessionState.OutcomeUnknownAbort,
            "OutcomeUnknown" =>
                UploadReconciliationSessionState.OutcomeUnknownCommit,
            _ => UploadReconciliationSessionState.Quarantined,
        };

    private static UploadReconciliationMutationResult ToWorker(
        PersistedUploadReconciliationMutation mutation) =>
        mutation.Status switch
        {
            PersistedUploadReconciliationMutationStatus.Applied
                when mutation.Current is not null =>
                UploadReconciliationMutationResult.Applied(
                    ToWorker(mutation.Current),
                    mutation.ReservationReleased),
            PersistedUploadReconciliationMutationStatus.AlreadyApplied
                when mutation.Current is not null =>
                UploadReconciliationMutationResult.AlreadyApplied(
                    ToWorker(mutation.Current)),
            _ => UploadReconciliationMutationResult.Stale(),
        };
}

internal sealed class BlobStoreUploadReconciliationStorageAdapter(
    IBlobStore blobStore) : IUploadReconciliationStoragePort
{
    private readonly IBlobStore _blobStore =
        blobStore ?? throw new ArgumentNullException(nameof(blobStore));

    public async ValueTask<UploadReconciliationHeadResult> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            BlobHead? head = await _blobStore.HeadAsync(key, cancellationToken);
            return head is null
                ? UploadReconciliationHeadResult.Missing()
                : UploadReconciliationHeadResult.Found(ToWorker(head));
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.NotFound)
        {
            return UploadReconciliationHeadResult.Missing();
        }
        catch (BlobStoreException)
        {
            return UploadReconciliationHeadResult.Retry();
        }
    }

    public async ValueTask<ReconciliationMultipartState> InspectMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken)
    {
        UploadReconciliationHeadResult head = await HeadAsync(
            multipart.StagingKey,
            cancellationToken);
        return head.Status switch
        {
            UploadReconciliationHeadStatus.Found =>
                ReconciliationMultipartState.Completed,
            UploadReconciliationHeadStatus.Missing
                when multipart.Session is not null =>
                ReconciliationMultipartState.Active,
            UploadReconciliationHeadStatus.Missing =>
                ReconciliationMultipartState.Unknown,
            _ => ReconciliationMultipartState.Retry,
        };
    }

    public async ValueTask<ReconciliationProviderMutationOutcome>
        AbortMultipartAsync(
            UploadReconciliationMultipart multipart,
            CancellationToken cancellationToken)
    {
        if (multipart.Session is null)
        {
            return ReconciliationProviderMutationOutcome.Stale;
        }

        try
        {
            await _blobStore.AbortMultipartAsync(
                multipart.Session,
                cancellationToken);
            return ReconciliationProviderMutationOutcome.Succeeded;
        }
        catch (BlobStoreException exception)
        {
            return exception.Code switch
            {
                BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.InvalidRequest =>
                    ReconciliationProviderMutationOutcome.Missing,
                BlobStoreErrorCode.PreconditionFailed =>
                    ReconciliationProviderMutationOutcome.Stale,
                BlobStoreErrorCode.OutcomeUnknown =>
                    ReconciliationProviderMutationOutcome.OutcomeUnknown,
                _ => ReconciliationProviderMutationOutcome.Retry,
            };
        }
    }

    public async ValueTask<ReconciliationProviderMutationOutcome>
        CompleteMultipartAsync(
            UploadReconciliationMultipart multipart,
            CancellationToken cancellationToken)
    {
        if (multipart.Session is null || multipart.CompletionParts.Count == 0)
        {
            return ReconciliationProviderMutationOutcome.Stale;
        }

        try
        {
            _ = await _blobStore.CompleteMultipartAsync(
                multipart.Session,
                multipart.CompletionParts,
                cancellationToken);
            return ReconciliationProviderMutationOutcome.Succeeded;
        }
        catch (BlobStoreException exception)
        {
            return exception.Code switch
            {
                BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.InvalidRequest =>
                    ReconciliationProviderMutationOutcome.Missing,
                BlobStoreErrorCode.PreconditionFailed =>
                    ReconciliationProviderMutationOutcome.Stale,
                BlobStoreErrorCode.OutcomeUnknown =>
                    ReconciliationProviderMutationOutcome.OutcomeUnknown,
                _ => ReconciliationProviderMutationOutcome.Retry,
            };
        }
    }

    public async ValueTask<ReconciliationProviderMutationOutcome>
        DeleteStagingAsync(
            BlobIdentity identity,
            CancellationToken cancellationToken)
    {
        try
        {
            BlobDeleteResult result = await _blobStore.DeleteAsync(
                identity.Key,
                new BlobDeleteOptions(
                    new BlobRequestConditions(ifMatch: identity.Version)),
                cancellationToken);
            return result.Deleted
                ? ReconciliationProviderMutationOutcome.Succeeded
                : ReconciliationProviderMutationOutcome.Missing;
        }
        catch (BlobStoreException exception)
        {
            return exception.Code switch
            {
                BlobStoreErrorCode.NotFound =>
                    ReconciliationProviderMutationOutcome.Missing,
                BlobStoreErrorCode.PreconditionFailed =>
                    ReconciliationProviderMutationOutcome.Stale,
                BlobStoreErrorCode.OutcomeUnknown =>
                    ReconciliationProviderMutationOutcome.OutcomeUnknown,
                _ => ReconciliationProviderMutationOutcome.Retry,
            };
        }
    }

    private static UploadReconciliationObjectHead ToWorker(BlobHead head)
    {
        Sha256Checksum? sha256 = null;
        BlobChecksum? checksum = head.Properties.Checksums.SingleOrDefault(
            item => item.Algorithm == BlobChecksumAlgorithm.Sha256);
        string? value = checksum?.Value;
        if (value is null &&
            head.Properties.Metadata.TryGetValue(
                "vistara-sha256",
                out string? metadataValue))
        {
            value = metadataValue;
        }

        if (value is not null)
        {
            try
            {
                sha256 = new Sha256Checksum(value);
            }
            catch (ArgumentException)
            {
            }
        }

        return new UploadReconciliationObjectHead(
            head.Identity,
            head.Properties.LastModifiedUtc,
            head.Properties.ContentLength,
            sha256,
            MetadataGuid(head, "vistara-tenant-id"),
            MetadataGuid(head, "vistara-upload-id"),
            head.Properties.ContentType,
            head.Properties.EntityTag);
    }

    private static Guid? MetadataGuid(BlobHead head, string key) =>
        head.Properties.Metadata.TryGetValue(key, out string? value) &&
        Guid.TryParse(value, out Guid parsed) &&
        parsed != Guid.Empty &&
        parsed.Version == 7
            ? parsed
            : null;
}
