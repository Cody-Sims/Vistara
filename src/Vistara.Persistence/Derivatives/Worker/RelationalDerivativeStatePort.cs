using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common;
using Vistara.Application.Common.Events;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Persistence.Outbox;

namespace Vistara.Persistence.Derivatives.Worker;

/// <summary>
/// Keeps fenced worker state on the derivative row so the immutable job payload
/// cannot race the job heartbeat. Processing rows temporarily use the
/// representation fields for the staged candidate and CacheKey for its version.
/// </summary>
public sealed class RelationalDerivativeStatePort : IDerivativeStatePort
{
    private const string PublicationIntent = "derivative.publication.intent";
    private const string PublicationPublished = "derivative.publication.published";
    private const string PublicationOutcomeUnknown =
        "derivative.publication.outcome_unknown";
    private const string Ownership = "derivative.ownership";
    private const string AssetReadyEventType = "asset.ready";
    private const string AssetReadyActor = "worker.derivatives";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly DbContextOptions<VistaraDbContext> _databaseOptions;
    private readonly IMutableTenantScope _tenantScope;
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly bool _isSqlite;

    public RelationalDerivativeStatePort(
        DbContextOptions<VistaraDbContext> databaseOptions,
        IMutableTenantScope tenantScope,
        IClock clock,
        IUuid7Generator? idGenerator = null)
    {
        _databaseOptions = databaseOptions ??
            throw new ArgumentNullException(nameof(databaseOptions));
        _tenantScope = tenantScope ??
            throw new ArgumentNullException(nameof(tenantScope));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? new Uuid7Generator(_clock);
        using var database =
            new VistaraDbContext(_databaseOptions, _tenantScope);
        string? provider = database.Database.ProviderName;
        _isSqlite = provider == "Microsoft.EntityFrameworkCore.Sqlite";
        if (!_isSqlite && provider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            throw new InvalidOperationException(
                "Derivative worker state requires SQLite or PostgreSQL.");
        }
    }

    public async ValueTask<DerivativeAcquireResult> AcquireAsync(
        DerivativeAcquireRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await WithLockedJobAsync(
                    request.TenantId,
                    request.JobLease.JobId,
                    async (database, job, token) =>
                    {
                        if (!MatchesJob(job, request))
                        {
                            return DerivativeAcquireResult.NotFound();
                        }

                        LoadedWork? loaded = await LoadWorkAsync(
                            database,
                            request,
                            token);
                        if (loaded is null)
                        {
                            return DerivativeAcquireResult.NotFound();
                        }

                        DerivativeGenerationDescriptorV1 descriptor =
                            request.Payload.Generation;
                        DerivativeRequestRow? state = await database
                            .Set<DerivativeRequestRow>()
                            .SingleOrDefaultAsync(
                                row =>
                                    row.JobId == request.RequestId ||
                                    row.GenerationIdentity ==
                                        descriptor.GenerationIdentity,
                                token);
                        if (state is null)
                        {
                            state = CreateState(
                                job!,
                                descriptor,
                                loaded.IsPublic);
                            database.Add(state);
                        }
                        else if (!MatchesState(state, job!, descriptor))
                        {
                            return DerivativeAcquireResult.NotFound();
                        }

                        if (state.State == "Failed" &&
                            IsPermanentFailure(state.FailureCode))
                        {
                            return DerivativeAcquireResult.Completed();
                        }

                        if (state.State == "Processing" &&
                            HasLiveOwnership(
                                state.FailureCode,
                                job!,
                                request.NowUtc))
                        {
                            return DerivativeAcquireResult.Busy();
                        }

                        long fenceVersion = checked(state.Version + 1);
                        state.Version = fenceVersion;
                        state.UpdatedAtUtc = request.NowUtc;
                        DerivativeStagedOutput? staged = ReadStaged(state);
                        bool ready = state.State == "Ready";
                        if (!ready)
                        {
                            state.State = "Processing";
                        }

                        DerivativeFence fence = new(
                            request.TenantId,
                            request.RequestId,
                            fenceVersion,
                            request.NowUtc.Add(request.OwnershipDuration),
                            CurrentLease(job!));
                        state.FailureCode = OwnershipMarker(
                            Ownership,
                            fence);
                        await database.SaveChangesAsync(token);
                        return ready
                            ? DerivativeAcquireResult.Ready(
                                fence,
                                loaded.Work,
                                staged)
                            : DerivativeAcquireResult.Acquired(
                                fence,
                                loaded.Work,
                                staged);
                    },
                    cancellationToken);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
            }
        }

        return DerivativeAcquireResult.NotFound();
    }

    public ValueTask<DerivativeStateWriteResult> RecordStagedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        return MutateOwnedAsync(
            fence,
            (_, state, _) =>
            {
                DerivativeStagedOutput? existing = ReadStaged(state);
                if (existing is not null && existing != staged)
                {
                    return ValueTask.FromResult(false);
                }

                WriteStaged(state, staged);
                state.UpdatedAtUtc = _clock.UtcNow;
                return ValueTask.FromResult(true);
            },
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> RecordPublishOutcomeUnknownAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken) =>
        MutateOwnedAsync(
            fence,
            (_, state, _) =>
            {
                state.FailureCode = OwnershipMarker(
                    PublicationOutcomeUnknown,
                    fence);
                state.UpdatedAtUtc = _clock.UtcNow;
                return ValueTask.FromResult(true);
            },
            cancellationToken);

    public async ValueTask<DerivativePublicationOutcome> PublishIfOwnedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        DerivativePublicationOperation publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(publish);
        DerivativeStateWriteResult authorized = await MutateOwnedAsync(
            fence,
            (_, state, _) =>
            {
                if (ReadStaged(state) != staged)
                {
                    return ValueTask.FromResult(false);
                }

                state.FailureCode = OwnershipMarker(
                    PublicationIntent,
                    fence);
                state.UpdatedAtUtc = _clock.UtcNow;
                return ValueTask.FromResult(true);
            },
            cancellationToken);
        if (authorized == DerivativeStateWriteResult.Stale)
        {
            return DerivativePublicationOutcome.Stale;
        }

        DerivativePublicationAttemptOutcome attempt =
            await publish(cancellationToken);
        DerivativeStateWriteResult recorded = await MutateOwnedAsync(
            fence,
            (_, state, _) =>
            {
                if (ReadStaged(state) != staged ||
                    state.FailureCode !=
                        OwnershipMarker(PublicationIntent, fence))
                {
                    return ValueTask.FromResult(false);
                }

                state.FailureCode = attempt switch
                {
                    DerivativePublicationAttemptOutcome.Published =>
                        OwnershipMarker(PublicationPublished, fence),
                    DerivativePublicationAttemptOutcome.OutcomeUnknown =>
                        OwnershipMarker(PublicationOutcomeUnknown, fence),
                    DerivativePublicationAttemptOutcome.Retry =>
                        OwnershipMarker(Ownership, fence),
                    _ => throw new InvalidOperationException(
                        "The derivative publication outcome is invalid."),
                };
                state.UpdatedAtUtc = _clock.UtcNow;
                return ValueTask.FromResult(true);
            },
            cancellationToken);
        if (recorded == DerivativeStateWriteResult.Stale)
        {
            return DerivativePublicationOutcome.Stale;
        }

        return attempt switch
        {
            DerivativePublicationAttemptOutcome.Published =>
                DerivativePublicationOutcome.Published,
            DerivativePublicationAttemptOutcome.OutcomeUnknown =>
                DerivativePublicationOutcome.OutcomeUnknown,
            DerivativePublicationAttemptOutcome.Retry =>
                DerivativePublicationOutcome.Retry,
            _ => throw new InvalidOperationException(
                "The derivative publication outcome is invalid."),
        };
    }

    /// <summary>
    /// Keeps the fenced ownership marker on the ready row so the owning worker
    /// can still complete cleanup; only cleanup clears it.
    /// </summary>
    public ValueTask<DerivativeStateWriteResult> MarkReadyAsync(
        DerivativeReadyOutput ready,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ready);
        return MutateOwnedAsync(
            ready.Fence,
            (job, state, _) =>
            {
                if (!TryReadPayload(job, out DerivativeJobPayloadV1? payload) ||
                    payload is null ||
                    !string.Equals(
                        payload.Generation.GenerationIdentity,
                        ready.Result.Identity.Value,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        payload.Generation.CacheKey,
                        ready.Result.CacheKey.Value,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The ready derivative does not match its generation descriptor.");
                }

                state.State = "Ready";
                state.FailureCode = OwnershipMarker(Ownership, ready.Fence);
                state.CacheKey = payload.Generation.CacheKey;
                state.RepresentationStorageKey =
                    ready.Head.Identity.Key.Value;
                state.RepresentationContentLength =
                    ready.Head.Properties.ContentLength;
                state.RepresentationContentType =
                    ready.Head.Properties.ContentType.Value;
                state.RepresentationSha256 =
                    ready.Result.RepresentationSha256.Value;
                state.UpdatedAtUtc = ready.ReadyAtUtc;
                return ValueTask.FromResult(true);
            },
            (database, state, token) => PromoteAssetIfReadyAsync(
                database,
                state,
                ready.ReadyAtUtc,
                token),
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> MarkFailedAsync(
        DerivativeFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return MutateOwnedAsync(
            failure.Fence,
            (job, state, _) =>
            {
                if (!TryReadPayload(job, out DerivativeJobPayloadV1? payload) ||
                    payload is null)
                {
                    return ValueTask.FromResult(false);
                }

                state.State = "Failed";
                state.FailureCode = failure.Code.ToString();
                state.CacheKey = payload.Generation.CacheKey;
                state.RepresentationStorageKey = null;
                state.RepresentationContentLength = null;
                state.RepresentationContentType = null;
                state.RepresentationSha256 = null;
                state.UpdatedAtUtc = failure.FailedAtUtc;
                return ValueTask.FromResult(true);
            },
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> CompleteCleanupAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken) =>
        MutateOwnedAsync(
            fence,
            (_, state, _) =>
            {
                if (state.State != "Ready")
                {
                    return ValueTask.FromResult(false);
                }

                state.FailureCode = null;
                state.UpdatedAtUtc = _clock.UtcNow;
                return ValueTask.FromResult(true);
            },
            cancellationToken);

    private ValueTask<DerivativeStateWriteResult> MutateOwnedAsync(
        DerivativeFence fence,
        Func<JobRow, DerivativeRequestRow, CancellationToken, ValueTask<bool>> mutate,
        CancellationToken cancellationToken) =>
        MutateOwnedAsync(fence, mutate, afterFlushStep: null, cancellationToken);

    /// <summary>
    /// Applies a fenced mutation to the derivative row. <paramref name="afterFlushStep"/>
    /// runs inside the same transaction once the derivative row is flushed, so
    /// readiness evaluation observes the derivative it just published and either
    /// both writes commit or neither does.
    /// </summary>
    private ValueTask<DerivativeStateWriteResult> MutateOwnedAsync(
        DerivativeFence fence,
        Func<JobRow, DerivativeRequestRow, CancellationToken, ValueTask<bool>> mutate,
        Func<VistaraDbContext, DerivativeRequestRow, CancellationToken, ValueTask>?
            afterFlushStep,
        CancellationToken cancellationToken) =>
        WithLockedJobAsync(
            fence.TenantId,
            fence.JobLease.JobId,
            async (database, job, token) =>
            {
                if (job is null || !Owns(job, fence))
                {
                    return DerivativeStateWriteResult.Stale;
                }

                TenantKey tenantId = fence.TenantId;
                DerivativeRequestRow? state = await database
                    .Set<DerivativeRequestRow>()
                    .SingleOrDefaultAsync(
                        row =>
                            row.TenantId == tenantId &&
                            row.JobId == fence.JobLease.JobId.Value,
                        token);
                if (state is null ||
                    !HasOwnership(state.FailureCode, fence) ||
                    !await mutate(job, state, token))
                {
                    return DerivativeStateWriteResult.Stale;
                }

                state.Version = checked(state.Version + 1);
                await database.SaveChangesAsync(token);
                if (afterFlushStep is not null)
                {
                    await afterFlushStep(database, state, token);
                    await database.SaveChangesAsync(token);
                }

                return DerivativeStateWriteResult.Applied;
            },
            cancellationToken);

    /// <summary>
    /// Promotes the owning asset to <c>Ready</c> once every required standard
    /// derivative for its current revision is visible. The asset row is locked
    /// first so parallel derivative completions cannot each observe an
    /// incomplete set and leave the asset stuck in <c>Processing</c>.
    /// </summary>
    private async ValueTask PromoteAssetIfReadyAsync(
        VistaraDbContext database,
        DerivativeRequestRow state,
        DateTimeOffset readyAtUtc,
        CancellationToken cancellationToken)
    {
        TenantKey tenantId = state.TenantId;
        AssetRow? asset = await LockAssetAsync(
            database,
            tenantId,
            state.AssetId,
            cancellationToken);
        if (asset is null ||
            asset.Status != "Processing" ||
            asset.CurrentRevisionId != state.RevisionId)
        {
            return;
        }

        Guid assetId = state.AssetId;
        Guid revisionId = state.RevisionId;
        List<string> readyPresets = await database
            .Set<DerivativeRequestRow>()
            .Where(row =>
                row.TenantId == tenantId &&
                row.AssetId == assetId &&
                row.RevisionId == revisionId &&
                row.State == "Ready")
            .Select(row => row.PresetName)
            .ToListAsync(cancellationToken);
        if (!AssetReadinessPolicy.IsSatisfiedBy(readyPresets))
        {
            return;
        }

        DateTimeOffset changedAtUtc =
            readyAtUtc < asset.UpdatedAtUtc ? asset.UpdatedAtUtc : readyAtUtc;
        asset.Status = "Ready";
        asset.UpdatedAtUtc = changedAtUtc;
        asset.Version = checked(asset.Version + 1);
        AppendReadyAudit(database, asset, changedAtUtc);
        await AppendReadyEventAsync(database, asset, changedAtUtc, cancellationToken);
    }

    private async ValueTask<AssetRow?> LockAssetAsync(
        VistaraDbContext database,
        TenantKey tenantId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (_isSqlite)
        {
            return await database.Assets.SingleOrDefaultAsync(
                row => row.TenantId == tenantId && row.Id == assetId,
                cancellationToken);
        }

        return await database.Assets
            .FromSqlRaw(
                """
                SELECT *
                FROM assets
                WHERE id = {0} AND tenant_id = {1}
                FOR UPDATE
                """,
                assetId,
                tenantId.Value)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private void AppendReadyAudit(
        VistaraDbContext database,
        AssetRow asset,
        DateTimeOffset occurredAtUtc) =>
        database.AuditEvents.Add(new AuditEventRow
        {
            Id = _idGenerator.NewId(),
            TenantId = asset.TenantId,
            ActorKind = "System",
            ActorIdentifier = AssetReadyActor,
            Action = AssetReadyEventType,
            ResourceType = "asset",
            ResourceIdentifier = asset.Id.ToString("D"),
            BeforeJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["state"] = "processing" },
                JsonOptions),
            AfterJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["state"] = "ready" },
                JsonOptions),
            Outcome = "Succeeded",
            OccurredAtUtc = occurredAtUtc,
        });

    private async ValueTask AppendReadyEventAsync(
        VistaraDbContext database,
        AssetRow asset,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var outbox = new OutboxRepository(database, database);
        EventSequence sequence =
            await outbox.ReserveSequenceAsync(cancellationToken);
        string payload = JsonSerializer.Serialize(
            new AssetReadyPayload(
                asset.Id,
                asset.CurrentRevisionId ?? Guid.Empty,
                "ready"),
            JsonOptions);
        var envelope = new EventEnvelope(
            new EventMetadata(
                new EventId(_idGenerator.NewId()),
                new EventTenantId(asset.TenantId.Value),
                sequence,
                AssetReadyEventType,
                eventVersion: 1,
                occurredAtUtc,
                correlationId: asset.Id),
            payload);
        await outbox.AppendAsync(
            OutboxMessage.Create(
                new OutboxMessageId(_idGenerator.NewId()),
                envelope,
                occurredAtUtc),
            cancellationToken);
    }

    private bool Owns(JobRow job, DerivativeFence fence)
    {
        DateTimeOffset now = _clock.UtcNow;
        return fence.TenantId == job.TenantId &&
            fence.RequestId == job.Id &&
            fence.JobLease.JobId.Value == job.Id &&
            job.State == nameof(JobState.Leased) &&
            string.Equals(
                job.LeaseOwner,
                fence.JobLease.Owner.Value,
                StringComparison.Ordinal) &&
            job.LeaseAcquiredAtUtc == fence.JobLease.AcquiredAtUtc &&
            job.LeaseExpiresAtUtc > now &&
            fence.ExpiresAtUtc > now;
    }

    private static bool MatchesJob(
        JobRow? job,
        DerivativeAcquireRequest request) =>
        job is not null &&
        job.Id == request.RequestId &&
        job.TenantId == request.TenantId &&
        job.State == nameof(JobState.Leased) &&
        string.Equals(
            job.LeaseOwner,
            request.JobLease.Owner.Value,
            StringComparison.Ordinal) &&
        job.LeaseAcquiredAtUtc == request.JobLease.AcquiredAtUtc &&
        job.LeaseExpiresAtUtc > request.NowUtc &&
        TryReadPayload(job, out DerivativeJobPayloadV1? payload) &&
        payload == request.Payload &&
        job.DedupeKey ==
            DerivativeJobContract.CreateDedupeKey(request.Payload).Value;

    private static bool TryReadPayload(
        JobRow job,
        out DerivativeJobPayloadV1? payload) =>
        DerivativeJobContract.TryParse(
            new JobType(job.Type),
            job.PayloadVersion,
            job.Payload,
            out payload);

    private static async ValueTask<LoadedWork?> LoadWorkAsync(
        VistaraDbContext database,
        DerivativeAcquireRequest request,
        CancellationToken cancellationToken)
    {
        DerivativeGenerationDescriptorV1 descriptor =
            request.Payload.Generation;
        if (descriptor.TenantId != request.TenantId ||
            !string.Equals(
                descriptor.PipelineFingerprint,
                request.PipelineFingerprint.Value,
                StringComparison.Ordinal))
        {
            return null;
        }

        AssetRow? asset = await database.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == descriptor.AssetId &&
                    row.CurrentRevisionId == descriptor.RevisionId,
                cancellationToken);
        AssetRevisionRow? revision = await database.AssetRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == descriptor.RevisionId &&
                    row.AssetId == descriptor.AssetId,
                cancellationToken);
        BlobRow? blob = revision is null
            ? null
            : await database.Blobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == revision.BlobId,
                    cancellationToken);
        if (asset is null ||
            revision is null ||
            blob is null ||
            blob.State != "Active" ||
            revision.RevisionNumber != descriptor.RevisionNumber ||
            !string.Equals(
                blob.Sha256,
                descriptor.SourceSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                blob.Provider,
                request.StorageProvider,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(blob.ProviderVersion) ||
            blob.SizeBytes <= 0)
        {
            return null;
        }

        DerivativeGenerationRequest generation;
        try
        {
            generation = descriptor.ToGenerationRequest();
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new LoadedWork(
            new DerivativeWorkItem(
                request.RequestId,
                generation,
                new BlobKey(blob.ObjectKey),
                new BlobVersion(blob.ProviderVersion),
                blob.SizeBytes),
            asset.Visibility == "Public");
    }

    private static DerivativeRequestRow CreateState(
        JobRow job,
        DerivativeGenerationDescriptorV1 descriptor,
        bool isPublic)
    {
        DerivativeGenerationRequest generation =
            descriptor.ToGenerationRequest();
        return new DerivativeRequestRow
        {
            Id = job.Id,
            TenantId = descriptor.TenantId,
            AssetId = descriptor.AssetId,
            RevisionId = descriptor.RevisionId,
            JobId = job.Id,
            IdempotencyKey =
                DerivativeRequestPersistenceIdentity
                    .PreGeneratedIdempotencyKey(job.Id),
            RequestHash = descriptor.GenerationIdentity,
            PresetName = descriptor.PresetName,
            PresetRevision = descriptor.PresetRevision,
            Width = descriptor.Width,
            Height = descriptor.Height,
            Fit = descriptor.Fit,
            Format = descriptor.Format,
            Quality = descriptor.Quality,
            PipelineId = descriptor.PipelineIdentity,
            PipelineFingerprint = descriptor.PipelineFingerprint,
            SourceSha256 = descriptor.SourceSha256,
            RecipeSha256 = descriptor.RecipeSha256,
            GenerationIdentity = descriptor.GenerationIdentity,
            CacheKey = descriptor.CacheKey,
            Extension = generation.Output.FileExtension,
            IsPublic = isPublic,
            State = "Queued",
            CreatedAtUtc = job.CreatedAtUtc,
            UpdatedAtUtc = job.CreatedAtUtc,
            Version = 1,
        };
    }

    private static bool MatchesState(
        DerivativeRequestRow state,
        JobRow job,
        DerivativeGenerationDescriptorV1 descriptor) =>
        state.TenantId.Value == descriptor.TenantId &&
        state.AssetId == descriptor.AssetId &&
        state.RevisionId == descriptor.RevisionId &&
        state.JobId == job.Id &&
        state.PresetName == descriptor.PresetName &&
        state.PresetRevision == descriptor.PresetRevision &&
        state.Width == descriptor.Width &&
        state.Height == descriptor.Height &&
        state.Fit == descriptor.Fit &&
        state.Format == descriptor.Format &&
        state.Quality == descriptor.Quality &&
        state.PipelineId == descriptor.PipelineIdentity &&
        state.PipelineFingerprint == descriptor.PipelineFingerprint &&
        state.SourceSha256 == descriptor.SourceSha256 &&
        state.RecipeSha256 == descriptor.RecipeSha256 &&
        state.GenerationIdentity == descriptor.GenerationIdentity &&
        state.Extension ==
            descriptor.ToGenerationRequest().Output.FileExtension;

    private static DerivativeStagedOutput? ReadStaged(
        DerivativeRequestRow state)
    {
        if (state.State == "Ready" ||
            state.RepresentationStorageKey is null ||
            state.RepresentationContentLength is null ||
            state.RepresentationContentType is null ||
            state.RepresentationSha256 is null)
        {
            return null;
        }

        try
        {
            return new DerivativeStagedOutput(
                new BlobIdentity(
                    new BlobKey(state.RepresentationStorageKey),
                    new BlobVersion(state.CacheKey)),
                state.RepresentationContentLength.Value,
                new ImageSha256(state.RepresentationSha256),
                new BlobMediaType(state.RepresentationContentType));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void WriteStaged(
        DerivativeRequestRow state,
        DerivativeStagedOutput staged)
    {
        state.CacheKey = staged.Identity.Version.Value;
        state.RepresentationStorageKey = staged.Identity.Key.Value;
        state.RepresentationContentLength = staged.Bytes;
        state.RepresentationContentType = staged.ContentType.Value;
        state.RepresentationSha256 = staged.Sha256.Value;
    }

    private static bool IsPermanentFailure(string? value) =>
        value == DerivativeFailureCode.SourceRevisionChanged.ToString() ||
        value == DerivativeFailureCode.DestinationIdentityConflict.ToString();

    private static string OwnershipMarker(
        string status,
        DerivativeFence fence) =>
        $"{status}:{fence.Version}:" +
        $"{fence.JobLease.AcquiredAtUtc.UtcTicks}:" +
        $"{fence.ExpiresAtUtc.UtcTicks}";

    private static bool HasLiveOwnership(
        string? marker,
        JobRow job,
        DateTimeOffset now)
    {
        if (marker is null)
        {
            return false;
        }

        if (!TryReadOwnership(
                marker,
                out _,
                out long acquiredAtTicks,
                out long expiresAtTicks))
        {
            return false;
        }

        return job.LeaseAcquiredAtUtc?.UtcTicks == acquiredAtTicks &&
            now.UtcTicks < expiresAtTicks;
    }

    private static bool HasOwnership(
        string? marker,
        DerivativeFence fence)
    {
        if (marker is null ||
            !TryReadOwnership(
                marker,
                out long version,
                out long acquiredAtTicks,
                out long expiresAtTicks))
        {
            return false;
        }

        return version == fence.Version &&
            acquiredAtTicks == fence.JobLease.AcquiredAtUtc.UtcTicks &&
            expiresAtTicks == fence.ExpiresAtUtc.UtcTicks;
    }

    private static bool TryReadOwnership(
        string marker,
        out long version,
        out long acquiredAtTicks,
        out long expiresAtTicks)
    {
        version = default;
        acquiredAtTicks = default;
        expiresAtTicks = default;
        string[] parts = marker.Split(':');
        return parts.Length == 4 &&
            long.TryParse(
                parts[1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out version) &&
            long.TryParse(
                parts[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out acquiredAtTicks) &&
            long.TryParse(
                parts[3],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out expiresAtTicks);
    }

    private static JobLease CurrentLease(JobRow job)
    {
        if (job.LeaseOwner is null ||
            !job.LeaseAcquiredAtUtc.HasValue ||
            !job.LeaseExpiresAtUtc.HasValue)
        {
            throw new InvalidOperationException(
                "The derivative job does not have a complete lease.");
        }

        return new JobLease(
            new JobId(job.Id),
            new JobLeaseOwner(job.LeaseOwner),
            job.LeaseAcquiredAtUtc.Value,
            job.LeaseExpiresAtUtc.Value,
            new JobVersion(job.Version));
    }

    private async ValueTask<T> WithLockedJobAsync<T>(
        Guid tenantId,
        JobId jobId,
        Func<VistaraDbContext, JobRow?, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tenantScope.Establish(tenantId);
        await using var database =
            new VistaraDbContext(_databaseOptions, _tenantScope);
        if (_isSqlite)
        {
            await database.Database.OpenConnectionAsync(cancellationToken);
            var connection =
                (SqliteConnection)database.Database.GetDbConnection();
            await using SqliteTransaction sqliteTransaction =
                connection.BeginTransaction(
                    IsolationLevel.Serializable,
                    deferred: false);
            await database.Database.UseTransactionAsync(
                sqliteTransaction,
                cancellationToken);
            try
            {
                JobRow? job = await database.Jobs.SingleOrDefaultAsync(
                    row =>
                        row.Id == jobId.Value &&
                        row.TenantId == tenantId,
                    cancellationToken);
                T result = await action(database, job, cancellationToken);
                await sqliteTransaction.CommitAsync(cancellationToken);
                return result;
            }
            finally
            {
                await database.Database.UseTransactionAsync(
                    null,
                    CancellationToken.None);
                await database.Database.CloseConnectionAsync();
            }
        }

        await using IDbContextTransaction postgresTransaction =
            await database.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        JobRow? locked = await database.Jobs
            .FromSqlRaw(
                """
                SELECT *
                FROM jobs
                WHERE id = {0} AND tenant_id = {1}
                FOR UPDATE
                """,
                jobId.Value,
                tenantId)
            .SingleOrDefaultAsync(cancellationToken);
        T value = await action(database, locked, cancellationToken);
        await postgresTransaction.CommitAsync(cancellationToken);
        return value;
    }

    private sealed record LoadedWork(
        DerivativeWorkItem Work,
        bool IsPublic);

    private sealed record AssetReadyPayload(
        Guid AssetId,
        Guid RevisionId,
        string State);
}
