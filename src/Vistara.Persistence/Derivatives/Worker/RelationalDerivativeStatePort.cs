using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Derivatives.Worker;

/// <summary>
/// Persists derivative checkpoints inside the versioned job payload so the
/// worker state and its lease fence are locked atomically in one row.
/// </summary>
public sealed class RelationalDerivativeStatePort : IDerivativeStatePort
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly JobDbContext _jobs;
    private readonly VistaraDbContext _database;
    private readonly IMutableTenantScope _tenantScope;
    private readonly DerivativePresetRegistry _presets;
    private readonly IClock _clock;
    private readonly bool _isSqlite;

    public RelationalDerivativeStatePort(
        JobDbContext jobs,
        VistaraDbContext database,
        IMutableTenantScope tenantScope,
        DerivativePresetRegistry presets,
        IClock clock)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _tenantScope = tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        string? provider = jobs.Database.ProviderName;
        _isSqlite = provider == "Microsoft.EntityFrameworkCore.Sqlite";
        if (!_isSqlite && provider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            throw new InvalidOperationException(
                "Derivative worker state requires SQLite or PostgreSQL.");
        }
    }

    public ValueTask<DerivativeAcquireResult> AcquireAsync(
        DerivativeAcquireRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WithLockedJobAsync(
            request.TenantId,
            request.JobLease.JobId,
            async (row, token) =>
            {
                PayloadEnvelope? payload = Parse(row?.Payload);
                if (row is null ||
                    payload is null ||
                    !MatchesRequest(row, payload, request))
                {
                    return DerivativeAcquireResult.NotFound();
                }

                WorkerState? state = payload.WorkerState;
                if (state is null)
                {
                    WorkState? work = await LoadWorkAsync(request, token);
                    if (work is null)
                    {
                        return DerivativeAcquireResult.NotFound();
                    }

                    state = new WorkerState
                    {
                        Status = StateStatus.Processing,
                        Work = work,
                    };
                    payload.WorkerState = state;
                }
                else if (!MatchesWork(state.Work, request))
                {
                    return DerivativeAcquireResult.NotFound();
                }

                if (state.Status == StateStatus.Completed ||
                    (state.Status == StateStatus.Failed &&
                     state.Failure is { Retryable: false }))
                {
                    return DerivativeAcquireResult.Completed();
                }

                DateTimeOffset now = request.NowUtc;
                if (state.Fence is not null &&
                    OwnsCurrentJob(row, state.Fence, now) &&
                    now < state.Fence.ExpiresAtUtc)
                {
                    return DerivativeAcquireResult.Busy();
                }

                JobLease currentLease = CurrentLease(row);
                state.FenceVersion++;
                state.Fence = FenceState.From(
                    currentLease,
                    state.FenceVersion,
                    now.Add(request.OwnershipDuration));
                if (state.Status != StateStatus.Ready)
                {
                    state.Status = StateStatus.Processing;
                    state.Failure = null;
                }

                row.Payload = Serialize(payload);
                await _jobs.SaveChangesAsync(token);
                DerivativeFence fence = state.Fence.ToFence(
                    request.TenantId,
                    request.RequestId);
                DerivativeWorkItem workItem = state.Work.ToWork(
                    request.RequestId,
                    _presets);
                DerivativeStagedOutput? staged = state.Staged?.ToStaged();
                return state.Status == StateStatus.Ready
                    ? DerivativeAcquireResult.Ready(fence, workItem, staged)
                    : DerivativeAcquireResult.Acquired(fence, workItem, staged);
            },
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> RecordStagedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        return MutateOwnedAsync(
            fence,
            (state, _) =>
            {
                state.Staged ??= StagedState.From(staged);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> RecordPublishOutcomeUnknownAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken) =>
        MutateOwnedAsync(
            fence,
            (state, _) =>
            {
                state.PublishOutcomeUnknown = true;
                return ValueTask.CompletedTask;
            },
            cancellationToken);

    public ValueTask<DerivativePublicationOutcome> PublishIfOwnedAsync(
        DerivativeFence fence,
        DerivativeStagedOutput staged,
        DerivativePublicationOperation publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(publish);
        return WithLockedJobAsync(
            fence.TenantId,
            fence.JobLease.JobId,
            async (row, token) =>
            {
                PayloadEnvelope? payload = Parse(row?.Payload);
                WorkerState? state = payload?.WorkerState;
                if (row is null ||
                    payload is null ||
                    state is null ||
                    !Owns(row, state, fence))
                {
                    return DerivativePublicationOutcome.Stale;
                }

                StagedState candidate = StagedState.From(staged);
                if (state.Staged is not null && state.Staged != candidate)
                {
                    return DerivativePublicationOutcome.Stale;
                }

                state.Staged = candidate;
                row.Payload = Serialize(payload);
                await _jobs.SaveChangesAsync(token);
                DerivativePublicationAttemptOutcome attempt = await publish(token);
                if (attempt == DerivativePublicationAttemptOutcome.OutcomeUnknown)
                {
                    state.PublishOutcomeUnknown = true;
                    row.Payload = Serialize(payload);
                    await _jobs.SaveChangesAsync(token);
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
            },
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> MarkReadyAsync(
        DerivativeReadyOutput ready,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ready);
        return MutateOwnedAsync(
            ready.Fence,
            (state, _) =>
            {
                if (!string.Equals(
                        state.Work.GenerationIdentity,
                        ready.Result.Identity.Value,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The ready derivative identity does not match durable work.");
                }

                state.Status = StateStatus.Ready;
                state.ReadyAtUtc = ready.ReadyAtUtc;
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> MarkFailedAsync(
        DerivativeFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return MutateOwnedAsync(
            failure.Fence,
            (state, _) =>
            {
                state.Status = StateStatus.Failed;
                state.Failure = new FailureState
                {
                    Code = failure.Code,
                    Retryable = failure.Retryable,
                    FailedAtUtc = failure.FailedAtUtc,
                };
                state.Staged = null;
                state.Fence = null;
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    public ValueTask<DerivativeStateWriteResult> CompleteCleanupAsync(
        DerivativeFence fence,
        CancellationToken cancellationToken) =>
        MutateOwnedAsync(
            fence,
            (state, _) =>
            {
                state.Status = StateStatus.Completed;
                state.Staged = null;
                state.Fence = null;
                return ValueTask.CompletedTask;
            },
            cancellationToken);

    private ValueTask<DerivativeStateWriteResult> MutateOwnedAsync(
        DerivativeFence fence,
        Func<WorkerState, CancellationToken, ValueTask> mutate,
        CancellationToken cancellationToken) =>
        WithLockedJobAsync(
            fence.TenantId,
            fence.JobLease.JobId,
            async (row, token) =>
            {
                PayloadEnvelope? payload = Parse(row?.Payload);
                WorkerState? state = payload?.WorkerState;
                if (row is null ||
                    payload is null ||
                    state is null ||
                    !Owns(row, state, fence))
                {
                    return DerivativeStateWriteResult.Stale;
                }

                await mutate(state, token);
                row.Payload = Serialize(payload);
                await _jobs.SaveChangesAsync(token);
                return DerivativeStateWriteResult.Applied;
            },
            cancellationToken);

    private bool Owns(JobRow row, WorkerState state, DerivativeFence fence) =>
        state.Fence is not null &&
        state.Fence.Version == fence.Version &&
        state.Fence.JobId == fence.JobLease.JobId.Value &&
        string.Equals(
            state.Fence.JobOwner,
            fence.JobLease.Owner.Value,
            StringComparison.Ordinal) &&
        state.Fence.JobAcquiredAtUtc == fence.JobLease.AcquiredAtUtc &&
        OwnsCurrentJob(row, state.Fence, _clock.UtcNow) &&
        _clock.UtcNow < state.Fence.ExpiresAtUtc;

    private static bool OwnsCurrentJob(
        JobRow row,
        FenceState fence,
        DateTimeOffset now) =>
        row.State == nameof(JobState.Leased) &&
        row.Id == fence.JobId &&
        string.Equals(row.LeaseOwner, fence.JobOwner, StringComparison.Ordinal) &&
        row.LeaseAcquiredAtUtc == fence.JobAcquiredAtUtc &&
        row.LeaseExpiresAtUtc > now;

    private static bool MatchesRequest(
        JobRow row,
        PayloadEnvelope payload,
        DerivativeAcquireRequest request) =>
        row.Id == request.RequestId &&
        row.TenantId == request.TenantId &&
        row.Type == DerivativeJobContract.TypeName &&
        row.PayloadVersion == DerivativeJobContract.PayloadVersion &&
        row.State == nameof(JobState.Leased) &&
        row.LeaseOwner == request.JobLease.Owner.Value &&
        row.LeaseAcquiredAtUtc == request.JobLease.AcquiredAtUtc &&
        row.LeaseExpiresAtUtc > request.NowUtc &&
        payload.AssetId == request.Payload.AssetId &&
        payload.RevisionId == request.Payload.RevisionId &&
        string.Equals(payload.Preset, request.Payload.Preset, StringComparison.Ordinal) &&
        row.DedupeKey ==
            DerivativeJobContract.CreateDedupeKey(request.Payload).Value;

    private static bool MatchesWork(
        WorkState work,
        DerivativeAcquireRequest request) =>
        work.TenantId == request.TenantId &&
        work.AssetId == request.Payload.AssetId &&
        work.RevisionId == request.Payload.RevisionId &&
        string.Equals(
            work.PresetName,
            request.Payload.Preset,
            StringComparison.Ordinal) &&
        string.Equals(
            work.StorageProvider,
            request.StorageProvider,
            StringComparison.Ordinal) &&
        string.Equals(
            work.PipelineFingerprint,
            request.PipelineFingerprint.Value,
            StringComparison.Ordinal);

    private async ValueTask<WorkState?> LoadWorkAsync(
        DerivativeAcquireRequest request,
        CancellationToken cancellationToken)
    {
        _tenantScope.Establish(request.TenantId);
        _database.ChangeTracker.Clear();
        AssetRow? asset = await _database.Assets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == request.Payload.AssetId &&
                    row.CurrentRevisionId == request.Payload.RevisionId,
                cancellationToken);
        AssetRevisionRow? revision = await _database.AssetRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == request.Payload.RevisionId &&
                    row.AssetId == request.Payload.AssetId,
                cancellationToken);
        BlobRow? blob = revision is null
            ? null
            : await _database.Blobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == revision.BlobId,
                    cancellationToken);
        if (asset is null ||
            revision is null ||
            blob is null ||
            blob.State != "Active" ||
            !string.Equals(
                blob.Provider,
                request.StorageProvider,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(blob.ProviderVersion) ||
            blob.SizeBytes <= 0)
        {
            return null;
        }

        DerivativeSourceIdentity source;
        try
        {
            source = new DerivativeSourceIdentity(
                request.TenantId,
                asset.Id,
                revision.Id,
                revision.RevisionNumber,
                new ImageSha256(blob.Sha256));
        }
        catch (ArgumentException)
        {
            return null;
        }

        DerivativeResolutionResult resolved = _presets.ResolveDefault(
            source,
            new DerivativePresetId(
                request.Payload.Preset,
                DerivativeJobContract.PresetRevision),
            request.PipelineFingerprint);
        DerivativeGenerationRequest? generation = resolved.GenerationRequest;
        if (resolved.Status != DerivativeNegotiationStatus.Selected ||
            generation is null)
        {
            return null;
        }

        return WorkState.From(
            generation,
            request.StorageProvider,
            blob.ObjectKey,
            blob.ProviderVersion,
            blob.SizeBytes);
    }

    private static JobLease CurrentLease(JobRow row)
    {
        if (row.LeaseOwner is null ||
            !row.LeaseAcquiredAtUtc.HasValue ||
            !row.LeaseExpiresAtUtc.HasValue)
        {
            throw new InvalidOperationException(
                "The derivative job does not have a complete lease.");
        }

        return new JobLease(
            new JobId(row.Id),
            new JobLeaseOwner(row.LeaseOwner),
            row.LeaseAcquiredAtUtc.Value,
            row.LeaseExpiresAtUtc.Value,
            new JobVersion(row.Version));
    }

    private async ValueTask<T> WithLockedJobAsync<T>(
        Guid tenantId,
        JobId jobId,
        Func<JobRow?, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs.ChangeTracker.Clear();
        if (_isSqlite)
        {
            await _jobs.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)_jobs.Database.GetDbConnection();
            await using SqliteTransaction sqliteTransaction =
                connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            await _jobs.Database.UseTransactionAsync(sqliteTransaction, cancellationToken);
            try
            {
                JobRow? row = await _jobs.Jobs.SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == jobId.Value &&
                        candidate.TenantId == tenantId,
                    cancellationToken);
                T result = await action(row, cancellationToken);
                await sqliteTransaction.CommitAsync(cancellationToken);
                return result;
            }
            finally
            {
                await _jobs.Database.UseTransactionAsync(null, CancellationToken.None);
                await _jobs.Database.CloseConnectionAsync();
            }
        }

        await using IDbContextTransaction postgresTransaction =
            await _jobs.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        JobRow? locked = await _jobs.Jobs
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
        T value = await action(locked, cancellationToken);
        await postgresTransaction.CommitAsync(cancellationToken);
        return value;
    }

    private static PayloadEnvelope? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PayloadEnvelope>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Serialize(PayloadEnvelope payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    private sealed class PayloadEnvelope
    {
        public Guid AssetId { get; set; }

        public Guid RevisionId { get; set; }

        public string Preset { get; set; } = string.Empty;

        public WorkerState? WorkerState { get; set; }
    }

    private sealed class WorkerState
    {
        public StateStatus Status { get; set; }

        public WorkState Work { get; set; } = new();

        public long FenceVersion { get; set; }

        public FenceState? Fence { get; set; }

        public StagedState? Staged { get; set; }

        public bool PublishOutcomeUnknown { get; set; }

        public FailureState? Failure { get; set; }

        public DateTimeOffset? ReadyAtUtc { get; set; }
    }

    private enum StateStatus
    {
        Processing,
        Ready,
        Failed,
        Completed,
    }

    private sealed record FenceState
    {
        public Guid JobId { get; init; }

        public string JobOwner { get; init; } = string.Empty;

        public DateTimeOffset JobAcquiredAtUtc { get; init; }

        public DateTimeOffset JobExpiresAtUtc { get; init; }

        public long JobVersion { get; init; }

        public long Version { get; init; }

        public DateTimeOffset ExpiresAtUtc { get; init; }

        internal static FenceState From(
            JobLease lease,
            long version,
            DateTimeOffset expiresAtUtc) =>
            new()
            {
                JobId = lease.JobId.Value,
                JobOwner = lease.Owner.Value,
                JobAcquiredAtUtc = lease.AcquiredAtUtc,
                JobExpiresAtUtc = lease.ExpiresAtUtc,
                JobVersion = lease.JobVersion.Value,
                Version = version,
                ExpiresAtUtc = expiresAtUtc,
            };

        internal DerivativeFence ToFence(Guid tenantId, Guid requestId) =>
            new(
                tenantId,
                requestId,
                Version,
                ExpiresAtUtc,
                new JobLease(
                    new JobId(JobId),
                    new JobLeaseOwner(JobOwner),
                    JobAcquiredAtUtc,
                    JobExpiresAtUtc,
                    new JobVersion(JobVersion)));
    }

    private sealed record StagedState
    {
        public string Key { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public long Bytes { get; init; }

        public string Sha256 { get; init; } = string.Empty;

        public string ContentType { get; init; } = string.Empty;

        internal static StagedState From(DerivativeStagedOutput staged) =>
            new()
            {
                Key = staged.Identity.Key.Value,
                Version = staged.Identity.Version.Value,
                Bytes = staged.Bytes,
                Sha256 = staged.Sha256.Value,
                ContentType = staged.ContentType.Value,
            };

        internal DerivativeStagedOutput ToStaged() =>
            new(
                new BlobIdentity(new BlobKey(Key), new BlobVersion(Version)),
                Bytes,
                new ImageSha256(Sha256),
                new BlobMediaType(ContentType));
    }

    private sealed class FailureState
    {
        public DerivativeFailureCode Code { get; set; }

        public bool Retryable { get; set; }

        public DateTimeOffset FailedAtUtc { get; set; }
    }

    private sealed class WorkState
    {
        public Guid TenantId { get; set; }

        public Guid AssetId { get; set; }

        public Guid RevisionId { get; set; }

        public long RevisionNumber { get; set; }

        public string SourceSha256 { get; set; } = string.Empty;

        public string StorageProvider { get; set; } = string.Empty;

        public string SourceKey { get; set; } = string.Empty;

        public string SourceVersion { get; set; } = string.Empty;

        public long SourceLength { get; set; }

        public string PresetName { get; set; } = string.Empty;

        public int PresetRevision { get; set; }

        public string PipelineFingerprint { get; set; } = string.Empty;

        public string GenerationIdentity { get; set; } = string.Empty;

        public string RecipeFingerprint { get; set; } = string.Empty;

        public string CacheKey { get; set; } = string.Empty;

        internal static WorkState From(
            DerivativeGenerationRequest generation,
            string storageProvider,
            string sourceKey,
            string sourceVersion,
            long sourceLength) =>
            new()
            {
                TenantId = generation.Source.TenantId,
                AssetId = generation.Source.AssetId,
                RevisionId = generation.Source.RevisionId,
                RevisionNumber = generation.Source.RevisionNumber,
                SourceSha256 = generation.Source.SourceSha256.Value,
                StorageProvider = storageProvider,
                SourceKey = sourceKey,
                SourceVersion = sourceVersion,
                SourceLength = sourceLength,
                PresetName = generation.Preset.Id.Name,
                PresetRevision = generation.Preset.Id.Revision,
                PipelineFingerprint = generation.PipelineFingerprint.Value,
                GenerationIdentity = generation.Identity.Value,
                RecipeFingerprint = generation.Recipe.Fingerprint,
                CacheKey = generation.CacheKey.Value,
            };

        internal DerivativeWorkItem ToWork(
            Guid requestId,
            DerivativePresetRegistry presets)
        {
            var source = new DerivativeSourceIdentity(
                TenantId,
                AssetId,
                RevisionId,
                RevisionNumber,
                new ImageSha256(SourceSha256));
            DerivativeResolutionResult resolution = presets.ResolveDefault(
                source,
                new DerivativePresetId(PresetName, PresetRevision),
                new ImagePipelineFingerprint(PipelineFingerprint));
            DerivativeGenerationRequest generation =
                resolution.GenerationRequest ??
                throw new InvalidOperationException(
                    "Durable derivative work no longer resolves.");
            if (!string.Equals(
                    generation.Identity.Value,
                    GenerationIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    generation.Recipe.Fingerprint,
                    RecipeFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    generation.CacheKey.Value,
                    CacheKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Durable derivative work does not match its canonical identity.");
            }

            return new DerivativeWorkItem(
                requestId,
                generation,
                new BlobKey(SourceKey),
                new BlobVersion(SourceVersion),
                SourceLength);
        }
    }
}
