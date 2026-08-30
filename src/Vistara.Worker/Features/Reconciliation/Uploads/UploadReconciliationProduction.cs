using System.Buffers;
using System.Security.Cryptography;
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

    public ValueTask<string?> LoadCheckpointAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        EstablishTenant(tenantId);
        return _store.LoadCheckpointAsync(
            tenantId,
            runId,
            cancellationToken);
    }

    public async ValueTask<UploadReconciliationPage> ScanAsync(
        UploadReconciliationScanRequest request,
        CancellationToken cancellationToken)
    {
        EstablishTenant(request.TenantId);
        PersistedUploadReconciliationPage page = await _store.ScanAsync(
            request.TenantId,
            request.Cursor,
            request.RunId,
            request.MaximumSessions,
            request.UtcNow,
            request.RecoveryCutoffUtc,
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

    public ValueTask<UploadReconciliationMutationResult> ExpireAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.ExpireAsync(
                candidate,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> PrepareAbortAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.PrepareAbortAsync(
                candidate,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult>
        RecordMultipartIssuedForAbortAsync(
            UploadReconciliationFence fence,
            MultipartSession session,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.RecordMultipartIssuedForAbortAsync(
                candidate,
                session,
                utcNow,
                token),
            cancellationToken);

    public ValueTask<UploadReconciliationMutationResult> CompleteAbortAsync(
            UploadReconciliationFence fence,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.CompleteAbortAsync(
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

    public ValueTask<UploadReconciliationMutationResult> ResumeIngestAsync(
        UploadReconciliationFence fence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        MutateAsync(
            fence,
            utcNow,
            (candidate, token) => _store.ResumeIngestAsync(
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
            (candidate, token) => _store.PreserveCanonicalAsync(
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
            candidate.CompletionParts,
            candidate.MultipartIssuanceId,
            candidate.MultipartPartPlanLifetime,
            candidate.CanonicalRequiresUploadOwnership);

    private static UploadReconciliationSessionState State(
        PersistedUploadReconciliationCandidate candidate) =>
        candidate.State switch
        {
            "Pending" or "UploadIssued" =>
                UploadReconciliationSessionState.Pending,
            "CommitRequested" => UploadReconciliationSessionState.CommitRequested,
            "Verifying" => UploadReconciliationSessionState.Verifying,
            "Promoting" => UploadReconciliationSessionState.Promoting,
            "Reconciling" => UploadReconciliationSessionState.Reconciling,
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
    IBlobStore blobStore,
    IClock clock) : IUploadReconciliationStoragePort
{
    private readonly IBlobStore _blobStore =
        blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

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

    public async ValueTask<UploadReconciliationHeadResult> VerifyAsync(
        BlobKey key,
        long expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSizeBytes);
        UploadReconciliationHeadResult headed = await HeadAsync(
            key,
            cancellationToken);
        if (headed.Status != UploadReconciliationHeadStatus.Found)
        {
            return headed;
        }

        UploadReconciliationObjectHead observed = headed.Head
            ?? throw new InvalidOperationException("Found HEAD result lacks data.");
        if (observed.ContentLength != expectedSizeBytes)
        {
            return headed;
        }

        try
        {
            await using BlobReadHandle handle = await _blobStore.OpenReadAsync(
                key,
                new BlobReadOptions(
                    Conditions: new BlobRequestConditions(
                        ifMatch: observed.Identity.Version)),
                cancellationToken);
            if (handle.Head.Identity != observed.Identity)
            {
                return UploadReconciliationHeadResult.Retry();
            }

            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long length = 0;
            try
            {
                int read;
                while ((read = await handle.Content.ReadAsync(
                           buffer.AsMemory(0, buffer.Length),
                           cancellationToken)) > 0)
                {
                    length = checked(length + read);
                    if (length > expectedSizeBytes)
                    {
                        return UploadReconciliationHeadResult.Found(
                            observed with
                            {
                                ContentLength = length,
                                Sha256 = null,
                            });
                    }

                    hash.AppendData(buffer, 0, read);
                }

                return UploadReconciliationHeadResult.Found(
                    observed with
                    {
                        ContentLength = length,
                        Sha256 = new Sha256Checksum(
                            Convert.ToHexStringLower(hash.GetHashAndReset())),
                    });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        catch (BlobStoreException exception)
            when (exception.Code is BlobStoreErrorCode.NotFound or
                BlobStoreErrorCode.PreconditionFailed)
        {
            return UploadReconciliationHeadResult.Retry();
        }
        catch (BlobStoreException)
        {
            return UploadReconciliationHeadResult.Retry();
        }
    }

    public async ValueTask<UploadReconciliationMultipartRecovery>
        RecoverMultipartAsync(
            UploadReconciliationMultipartIssuance issuance,
            CancellationToken cancellationToken)
    {
        if (_blobStore is not IDurableMultipartBlobStore durable)
        {
            return new(null, Retry: true);
        }

        TimeSpan remaining = issuance.ExpiresAtUtc - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            remaining = issuance.PartPlanLifetime;
        }

        try
        {
            MultipartSession session = await durable.GetOrCreateMultipartAsync(
                issuance.IssuanceId,
                new MultipartRequest(
                    issuance.StagingKey,
                    issuance.ExpectedSizeBytes,
                    issuance.ContentType,
                    checksum: null,
                    BlobRequestConditions.CreateOnly,
                    remaining,
                    issuance.PartPlanLifetime,
                    new BlobMetadata(
                    [
                        KeyValuePair.Create(
                            "vistara-tenant-id",
                            issuance.TenantId.ToString("D")),
                        KeyValuePair.Create(
                            "vistara-upload-id",
                            issuance.UploadSessionId.ToString("D")),
                        KeyValuePair.Create(
                            "vistara-multipart-issuance-id",
                            issuance.IssuanceId),
                    ])),
                cancellationToken);
            return new(session, Retry: false);
        }
        catch (BlobStoreException)
        {
            return new(null, Retry: true);
        }
        catch (TimeoutException)
        {
            return new(null, Retry: true);
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
            UploadReconciliationHeadStatus.Missing =>
                await InspectMissingMultipartAsync(
                    multipart,
                    cancellationToken),
            _ => ReconciliationMultipartState.Retry,
        };
    }

    private async ValueTask<ReconciliationMultipartState> InspectMissingMultipartAsync(
        UploadReconciliationMultipart multipart,
        CancellationToken cancellationToken)
    {
        if (multipart.Session is null ||
            _blobStore is not IDurableMultipartBlobStore durable)
        {
            return ReconciliationMultipartState.Unknown;
        }

        try
        {
            MultipartInventory inventory = await durable.InspectMultipartAsync(
                multipart.Session,
                multipart.CompletionParts,
                cancellationToken);
            return inventory.State switch
            {
                MultipartInventoryState.Active =>
                    ReconciliationMultipartState.Active,
                MultipartInventoryState.Completed =>
                    ReconciliationMultipartState.Completed,
                MultipartInventoryState.Aborted =>
                    ReconciliationMultipartState.Aborted,
                MultipartInventoryState.Missing =>
                    ReconciliationMultipartState.Missing,
                _ => ReconciliationMultipartState.Unknown,
            };
        }
        catch (BlobStoreException)
        {
            return ReconciliationMultipartState.Retry;
        }
        catch (TimeoutException)
        {
            return ReconciliationMultipartState.Retry;
        }
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
                BlobStoreErrorCode.NotFound =>
                    ReconciliationProviderMutationOutcome.Missing,
                BlobStoreErrorCode.InvalidRequest =>
                    ReconciliationProviderMutationOutcome.Stale,
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
                BlobStoreErrorCode.NotFound =>
                    ReconciliationProviderMutationOutcome.Missing,
                BlobStoreErrorCode.InvalidRequest =>
                    ReconciliationProviderMutationOutcome.Stale,
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
