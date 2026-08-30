using Vistara.Api.Features.Uploads;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Idempotency;
using Vistara.Persistence.Uploads;

namespace Vistara.Api.Composition.Platform;

internal sealed class PlatformUploadPersistenceAdapter(
    RelationalUploadApplicationStore store,
    IBlobStore blobStore,
    UploadPersistenceOptions options) : IUploadApplicationPort
{
    private readonly RelationalUploadApplicationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IBlobStore _blobStore =
        blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly UploadPersistenceOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<UploadProviderPolicy> GetProviderPolicyAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        new(
            _blobStore.Capabilities,
            await _store.GetMaximumUploadBytesAsync(tenantId, cancellationToken),
            _options.MultipartThresholdBytes,
            _options.PlanLifetime);

    public async ValueTask<UploadReserveResult> ReserveAsync(
        ReserveUploadRequest request,
        CancellationToken cancellationToken)
    {
        PersistedUploadReserveResult result = await _store.ReserveAsync(
            new PersistedUploadReserveCommand(
                request.TenantId,
                request.ActorId,
                request.UploadId,
                request.Strategy,
                request.DisplayFileName,
                request.ExpectedSizeBytes,
                request.DeclaredContentType,
                request.Sha256,
                request.StagingKey,
                request.RequestHash,
                request.IdempotencyKey.Value,
                request.ExpiresAtUtc),
            cancellationToken);
        return result.Status switch
        {
            PersistedUploadReserveStatus.Created when result.Session is not null =>
                UploadReserveResult.Created(ToApi(result.Session)),
            PersistedUploadReserveStatus.Replayed when result.Session is not null =>
                UploadReserveResult.Replayed(ToApi(result.Session)),
            PersistedUploadReserveStatus.IdempotencyConflict =>
                UploadReserveResult.Conflict(),
            PersistedUploadReserveStatus.QuotaExceeded =>
                UploadReserveResult.QuotaExceeded(),
            _ => UploadReserveResult.Unavailable(),
        };
    }

    public async ValueTask<UploadIssuance> IssueAsync(
        UploadSessionSnapshot session,
        CancellationToken cancellationToken)
    {
        PersistedUploadIssuance issuance = await _store.IssueAsync(
            ToPersistence(session),
            cancellationToken);
        UploadSessionSnapshot updated = ToApi(issuance.Session);
        return issuance.Session.Strategy switch
        {
            "proxy" => UploadIssuance.Proxy(updated),
            "direct" when issuance.DirectPlan is not null =>
                UploadIssuance.Direct(
                    updated,
                    ToApi(issuance.DirectPlan)),
            "multipart" when issuance.MultipartSession is not null =>
                UploadIssuance.Multipart(
                    updated,
                    issuance.Parts.Select(ToApi).ToArray(),
                    issuance.MultipartSession.MaxParts,
                    issuance.MultipartSession.MinPartBytes,
                    issuance.MultipartSession.MaxPartBytes),
            _ => throw new InvalidOperationException(
                "The persisted upload issuance is invalid."),
        };
    }

    public async ValueTask<UploadSessionSnapshot?> GetAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        PersistedUploadSession? session = await _store.GetAsync(
            tenantId,
            uploadId,
            cancellationToken);
        return session is null ? null : ToApi(session);
    }

    public async ValueTask<UploadWriteResult> WriteProxyAsync(
        UploadSessionSnapshot session,
        Stream content,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        PersistedUploadWriteResult result = await _store.WriteProxyAsync(
            ToPersistence(session),
            content,
            expectedVersion,
            cancellationToken);
        return result.Status switch
        {
            PersistedUploadWriteStatus.Written when result.Session is not null =>
                UploadWriteResult.Written(ToApi(result.Session)),
            _ => UploadWriteResult.Failure(ToApi(result.Status)),
        };
    }

    public async ValueTask<UploadPartPlanResult> RefreshPartPlansAsync(
        UploadSessionSnapshot session,
        IReadOnlyList<int> partNumbers,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        PersistedUploadPartPlanResult result =
            await _store.RefreshPartPlansAsync(
                ToPersistence(session),
                partNumbers,
                expectedVersion,
                cancellationToken);
        return result.Status == PersistedUploadPartPlanStatus.Created
            ? UploadPartPlanResult.Created(result.Parts.Select(ToApi).ToArray())
            : UploadPartPlanResult.Failure(ToApi(result.Status));
    }

    public async ValueTask<UploadCommitResult> CommitAsync(
        UploadSessionSnapshot session,
        IReadOnlyList<CommittedUploadPart> parts,
        IdempotencyKey idempotencyKey,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        PersistedUploadCommitResult result = await _store.CommitAsync(
            ToPersistence(session),
            parts.Select(part => new PersistedCommittedUploadPart(
                part.PartNumber,
                part.EntityTag,
                part.Checksum,
                part.SizeBytes)).ToArray(),
            idempotencyKey.Value,
            expectedVersion,
            cancellationToken);
        UploadSessionSnapshot? updated =
            result.Session is null ? null : ToApi(result.Session);
        return result.Status switch
        {
            PersistedUploadCommitStatus.Queued when updated is not null =>
                UploadCommitResult.Queued(updated),
            PersistedUploadCommitStatus.Replayed when updated is not null =>
                UploadCommitResult.Replayed(updated),
            PersistedUploadCommitStatus.AlreadyAccepted when updated is not null =>
                UploadCommitResult.Accepted(updated),
            _ => UploadCommitResult.Failure(ToApi(result.Status)),
        };
    }

    public async ValueTask<UploadAbortResult> AbortAsync(
        UploadSessionSnapshot session,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        PersistedUploadAbortResult result = await _store.AbortAsync(
            ToPersistence(session),
            expectedVersion,
            cancellationToken);
        UploadSessionSnapshot? updated =
            result.Session is null ? null : ToApi(result.Session);
        return result.Status switch
        {
            PersistedUploadAbortStatus.Aborted when updated is not null =>
                UploadAbortResult.Aborted(updated),
            PersistedUploadAbortStatus.AlreadyAborted when updated is not null =>
                UploadAbortResult.AlreadyAborted(updated),
            _ => UploadAbortResult.Failure(ToApi(result.Status)),
        };
    }

    private static UploadWriteStatus ToApi(PersistedUploadWriteStatus status) =>
        status switch
        {
            PersistedUploadWriteStatus.VersionConflict =>
                UploadWriteStatus.VersionConflict,
            PersistedUploadWriteStatus.InvalidState => UploadWriteStatus.InvalidState,
            PersistedUploadWriteStatus.Expired => UploadWriteStatus.Expired,
            PersistedUploadWriteStatus.TooLarge => UploadWriteStatus.TooLarge,
            PersistedUploadWriteStatus.IntegrityMismatch =>
                UploadWriteStatus.IntegrityMismatch,
            _ => UploadWriteStatus.Unavailable,
        };

    private static UploadPartPlanStatus ToApi(
        PersistedUploadPartPlanStatus status) =>
        status switch
        {
            PersistedUploadPartPlanStatus.VersionConflict =>
                UploadPartPlanStatus.VersionConflict,
            PersistedUploadPartPlanStatus.InvalidState =>
                UploadPartPlanStatus.InvalidState,
            PersistedUploadPartPlanStatus.Expired => UploadPartPlanStatus.Expired,
            _ => UploadPartPlanStatus.Unavailable,
        };

    private static UploadCommitStatus ToApi(
        PersistedUploadCommitStatus status) =>
        status switch
        {
            PersistedUploadCommitStatus.IdempotencyConflict =>
                UploadCommitStatus.IdempotencyConflict,
            PersistedUploadCommitStatus.VersionConflict =>
                UploadCommitStatus.VersionConflict,
            PersistedUploadCommitStatus.InvalidState =>
                UploadCommitStatus.InvalidState,
            PersistedUploadCommitStatus.Expired => UploadCommitStatus.Expired,
            PersistedUploadCommitStatus.OutcomeUnknown =>
                UploadCommitStatus.OutcomeUnknown,
            _ => UploadCommitStatus.Unavailable,
        };

    private static UploadAbortStatus ToApi(PersistedUploadAbortStatus status) =>
        status switch
        {
            PersistedUploadAbortStatus.VersionConflict =>
                UploadAbortStatus.VersionConflict,
            PersistedUploadAbortStatus.InvalidState =>
                UploadAbortStatus.InvalidState,
            PersistedUploadAbortStatus.Expired => UploadAbortStatus.Expired,
            _ => UploadAbortStatus.Unavailable,
        };

    private static UploadSessionSnapshot ToApi(PersistedUploadSession session) =>
        new(
            session.TenantId,
            session.ActorId,
            session.UploadId,
            session.Strategy,
            session.State,
            session.ExpectedSizeBytes,
            session.DeclaredContentType,
            session.Sha256,
            session.DisplayFileName,
            session.StagingKey,
            session.ExpiresAtUtc,
            session.Version,
            session.Parts.Select(part => new UploadPartSnapshot(
                part.PartNumber,
                part.SizeBytes,
                part.Checksum)).ToArray());

    private static PersistedUploadSession ToPersistence(
        UploadSessionSnapshot session) =>
        new(
            session.TenantId,
            session.ActorId,
            session.UploadId,
            session.Strategy,
            session.State,
            session.ExpectedSizeBytes,
            session.DeclaredContentType,
            session.Sha256,
            session.DisplayFileName,
            session.StagingKey,
            session.ExpiresAtUtc,
            session.Version,
            session.Parts.Select(part => new PersistedUploadPart(
                part.PartNumber,
                part.SizeBytes,
                part.Checksum)).ToArray());

    private static UploadSignedRequest ToApi(DirectUploadPlan plan) =>
        new(
            plan.Request.Method.ToString().ToUpperInvariant(),
            plan.Request.Url,
            plan.Request.Headers,
            plan.ExpiresAtUtc);

    private static UploadSignedPartRequest ToApi(MultipartPartPlan plan) =>
        new(
            plan.PartNumber,
            new UploadSignedRequest(
                plan.Request.Method.ToString().ToUpperInvariant(),
                plan.Request.Url,
                plan.Request.Headers,
                plan.ExpiresAtUtc),
            plan.MinBytes,
            plan.MaxBytes);
}
