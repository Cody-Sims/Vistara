using Microsoft.EntityFrameworkCore;
using Vistara.Application.Derivatives;
using Vistara.Persistence.Jobs;

namespace Vistara.Persistence.Derivatives;

public sealed record PersistedDerivativeSource(
    Guid TenantId,
    Guid AssetId,
    Guid RevisionId,
    long RevisionNumber,
    string SourceSha256,
    bool IsPublic);

public sealed record PersistedDerivativeSubmission
{
    public PersistedDerivativeSubmission(
        Guid requestId,
        Guid jobId,
        string idempotencyKey,
        string requestHash,
        DerivativeJobPayloadV1 jobPayload,
        bool isPublic,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(jobPayload);
        RequestId = requestId;
        JobId = jobId;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        JobPayload = jobPayload;
        IsPublic = isPublic;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid RequestId { get; }

    public Guid JobId { get; }

    public string IdempotencyKey { get; }

    public string RequestHash { get; }

    public DerivativeJobPayloadV1 JobPayload { get; }

    public bool IsPublic { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}

public sealed record PersistedDerivativeRequest(
    Guid RequestId,
    Guid TenantId,
    Guid AssetId,
    Guid RevisionId,
    Guid JobId,
    string IdempotencyKey,
    string RequestHash,
    string PresetName,
    int PresetRevision,
    int Width,
    int Height,
    string Fit,
    string Format,
    int Quality,
    decimal? FocalPointX,
    decimal? FocalPointY,
    decimal? CropX,
    decimal? CropY,
    decimal? CropWidth,
    decimal? CropHeight,
    string PipelineId,
    string PipelineFingerprint,
    string SourceSha256,
    string RecipeSha256,
    string GenerationIdentity,
    string CacheKey,
    string Extension,
    bool IsPublic,
    string State,
    string? FailureCode,
    string? RepresentationStorageKey,
    long? RepresentationContentLength,
    string? RepresentationContentType,
    string? RepresentationSha256,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

public enum PersistedDerivativeSubmissionStatus
{
    Created,
    Attached,
    Replayed,
    Reused,
    IdempotencyConflict,
}

public sealed record PersistedDerivativeSubmissionResult(
    PersistedDerivativeSubmissionStatus Status,
    PersistedDerivativeRequest? Request);

public sealed class RelationalDerivativeRequestStore(
    VistaraDbContext context,
    ITenantScope tenantScope)
{
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly ITenantScope _tenantScope =
        tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));

    public async ValueTask<PersistedDerivativeSource?> GetSourceAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await (
            from asset in _context.Assets.AsNoTracking()
            join revision in _context.AssetRevisions.AsNoTracking()
                on new { asset.TenantId, Id = asset.CurrentRevisionId }
                equals new
                {
                    revision.TenantId,
                    Id = (Guid?)revision.Id,
                }
            join blob in _context.Blobs.AsNoTracking()
                on new { revision.TenantId, Id = revision.BlobId }
                equals new { blob.TenantId, blob.Id }
            where asset.Id == assetId &&
                asset.Status == "Ready" &&
                blob.State == "Active"
            select new PersistedDerivativeSource(
                tenantId,
                asset.Id,
                revision.Id,
                revision.RevisionNumber,
                blob.Sha256,
                asset.Visibility == "Public"))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async ValueTask<PersistedDerivativeSubmissionResult> SubmitAsync(
        PersistedDerivativeSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        DerivativeGenerationDescriptorV1 generation =
            submission.JobPayload.Generation;
        EnsureTenant(generation.TenantId);
        DerivativeRequestRow? idempotent = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.AssetId == generation.AssetId &&
                    row.IdempotencyKey == submission.IdempotencyKey,
                cancellationToken);
        if (idempotent is not null)
        {
            JobRow? job = await FindJobAsync(
                idempotent.JobId,
                cancellationToken);
            return idempotent.RequestHash == submission.RequestHash
                ? new(
                    PersistedDerivativeSubmissionStatus.Replayed,
                    ToPersisted(idempotent, job))
                : new(
                    PersistedDerivativeSubmissionStatus.IdempotencyConflict,
                    null);
        }

        DerivativeRequestRow? reusable = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.GenerationIdentity == generation.GenerationIdentity,
                cancellationToken);
        if (reusable is not null)
        {
            if (IsPreGenerated(reusable))
            {
                try
                {
                    int attached = await _context
                        .Set<DerivativeRequestRow>()
                        .Where(row =>
                            row.Id == reusable.Id &&
                            row.IdempotencyKey == reusable.IdempotencyKey &&
                            row.RequestHash == reusable.RequestHash)
                        .ExecuteUpdateAsync(
                            updates => updates
                                .SetProperty(
                                    row => row.IdempotencyKey,
                                    submission.IdempotencyKey)
                                .SetProperty(
                                    row => row.RequestHash,
                                    submission.RequestHash)
                                .SetProperty(
                                    row => row.IsPublic,
                                    submission.IsPublic),
                            cancellationToken);
                    if (attached == 1)
                    {
                        reusable.IdempotencyKey = submission.IdempotencyKey;
                        reusable.RequestHash = submission.RequestHash;
                        reusable.IsPublic = submission.IsPublic;
                        JobRow? attachedJob = await FindJobAsync(
                            reusable.JobId,
                            cancellationToken);
                        return new(
                            PersistedDerivativeSubmissionStatus.Attached,
                            ToPersisted(reusable, attachedJob));
                    }
                }
                catch (DbUpdateException)
                {
                    _context.ChangeTracker.Clear();
                }

                DerivativeRequestRow current = await _context
                    .Set<DerivativeRequestRow>()
                    .AsNoTracking()
                    .SingleAsync(
                        row => row.Id == reusable.Id,
                        cancellationToken);
                if (current.IdempotencyKey == submission.IdempotencyKey)
                {
                    return current.RequestHash == submission.RequestHash
                        ? new(
                            PersistedDerivativeSubmissionStatus.Replayed,
                            ToPersisted(
                                current,
                                await FindJobAsync(
                                    current.JobId,
                                    cancellationToken)))
                        : new(
                            PersistedDerivativeSubmissionStatus.IdempotencyConflict,
                            null);
                }

                reusable = current;
            }

            JobRow? job = await FindJobAsync(
                reusable.JobId,
                cancellationToken);
            return new(
                PersistedDerivativeSubmissionStatus.Reused,
                ToPersisted(reusable, job));
        }

        DbUpdateException? conflict = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            JobRow? existingJob = await FindCanonicalJobAsync(
                generation.TenantId,
                submission.JobPayload,
                cancellationToken);
            Guid jobId = existingJob?.Id ?? submission.JobId;
            DerivativeRequestRow request = CreateRequest(
                submission,
                generation,
                jobId);
            _context.Add(request);
            if (existingJob is null)
            {
                _context.Jobs.Add(CreateJob(submission, generation));
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return new(
                    existingJob is null
                        ? PersistedDerivativeSubmissionStatus.Created
                        : PersistedDerivativeSubmissionStatus.Attached,
                    ToPersisted(request, existingJob));
            }
            catch (DbUpdateException exception)
            {
                conflict = exception;
                _context.ChangeTracker.Clear();
                if (attempt == 0)
                {
                    continue;
                }

                PersistedDerivativeSubmissionResult? raced =
                    await ResolveSubmissionRaceAsync(
                        submission,
                        generation,
                        cancellationToken);
                if (raced is not null)
                {
                    return raced;
                }
            }
        }

        if (conflict is not null)
        {
            throw conflict;
        }

        throw new InvalidOperationException(
            "The derivative request could not be attached atomically.");
    }

    public async ValueTask<IReadOnlyList<PersistedDerivativeRequest>> ListAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        DerivativeRequestRow[] rows = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(row => row.AssetId == assetId)
            .OrderBy(row => row.CreatedAtUtc)
            .ThenBy(row => row.Id)
            .ToArrayAsync(cancellationToken);
        Guid[] jobIds = rows.Select(row => row.JobId).Distinct().ToArray();
        Dictionary<Guid, JobRow> jobs = await _context.Jobs
            .AsNoTracking()
            .Where(row => jobIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, cancellationToken);
        return rows
            .Select(row => ToPersisted(
                row,
                jobs.GetValueOrDefault(row.JobId)))
            .ToArray();
    }

    public async ValueTask<PersistedDerivativeRequest?> GetAsync(
        Guid tenantId,
        Guid assetId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        DerivativeRequestRow? row = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == requestId &&
                    candidate.AssetId == assetId,
                cancellationToken);
        return row is null
            ? null
            : ToPersisted(
                row,
                await FindJobAsync(row.JobId, cancellationToken));
    }

    public async ValueTask<PersistedDerivativeRequest?> FindByRouteAsync(
        Guid tenantId,
        string pipelineId,
        string sourceSha256,
        string recipeSha256,
        string extension,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        DerivativeRequestRow? row = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(candidate =>
                    candidate.PipelineId == pipelineId &&
                    candidate.SourceSha256 == sourceSha256 &&
                    candidate.RecipeSha256 == recipeSha256 &&
                    candidate.Extension == extension)
            .OrderByDescending(candidate => candidate.State == "Ready")
            .ThenByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : ToPersisted(
                row,
                await FindJobAsync(row.JobId, cancellationToken));
    }

    private void EnsureTenant(Guid tenantId)
    {
        if (TenantScopeGuard.RequireTenantId(_tenantScope) != tenantId)
        {
            throw new InvalidOperationException(
                "Derivative requests cannot cross tenant scope.");
        }
    }

    private ValueTask<JobRow?> FindJobAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        new(_context.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == jobId,
                cancellationToken));

    private async ValueTask<JobRow?> FindCanonicalJobAsync(
        Guid tenantId,
        DerivativeJobPayloadV1 payload,
        CancellationToken cancellationToken)
    {
        string dedupeKey = DerivativeJobContract.CreateDedupeKey(payload).Value;
        JobRow? job = await _context.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.TenantId == tenantId &&
                    row.DedupeKey == dedupeKey,
                cancellationToken);
        if (job is null)
        {
            return null;
        }

        if (!DerivativeJobContract.TryParse(
                new Vistara.Domain.Jobs.JobType(job.Type),
                job.PayloadVersion,
                job.Payload,
                out DerivativeJobPayloadV1? existing) ||
            existing is null ||
            existing.Generation != payload.Generation)
        {
            throw new InvalidOperationException(
                "The canonical derivative dedupe key belongs to a different job.");
        }

        return job;
    }

    private async ValueTask<PersistedDerivativeSubmissionResult?>
        ResolveSubmissionRaceAsync(
            PersistedDerivativeSubmission submission,
            DerivativeGenerationDescriptorV1 generation,
            CancellationToken cancellationToken)
    {
        DerivativeRequestRow? idempotent = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.AssetId == generation.AssetId &&
                    row.IdempotencyKey == submission.IdempotencyKey,
                cancellationToken);
        if (idempotent is not null)
        {
            return idempotent.RequestHash == submission.RequestHash
                ? new(
                    PersistedDerivativeSubmissionStatus.Replayed,
                    ToPersisted(
                        idempotent,
                        await FindJobAsync(
                            idempotent.JobId,
                            cancellationToken)))
                : new(
                    PersistedDerivativeSubmissionStatus.IdempotencyConflict,
                    null);
        }

        DerivativeRequestRow? reusable = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.GenerationIdentity == generation.GenerationIdentity,
                cancellationToken);
        if (reusable is null)
        {
            return null;
        }

        return new(
            PersistedDerivativeSubmissionStatus.Reused,
            ToPersisted(
                reusable,
                await FindJobAsync(
                    reusable.JobId,
                    cancellationToken)));
    }

    private static DerivativeRequestRow CreateRequest(
        PersistedDerivativeSubmission submission,
        DerivativeGenerationDescriptorV1 generation,
        Guid jobId) =>
        new()
        {
            Id = submission.RequestId,
            TenantId = generation.TenantId,
            AssetId = generation.AssetId,
            RevisionId = generation.RevisionId,
            JobId = jobId,
            IdempotencyKey = submission.IdempotencyKey,
            RequestHash = submission.RequestHash,
            PresetName = generation.PresetName,
            PresetRevision = generation.PresetRevision,
            Width = generation.Width,
            Height = generation.Height,
            Fit = generation.Fit,
            Format = generation.Format,
            Quality = generation.Quality,
            PipelineId = generation.PipelineIdentity,
            PipelineFingerprint = generation.PipelineFingerprint,
            SourceSha256 = generation.SourceSha256,
            RecipeSha256 = generation.RecipeSha256,
            GenerationIdentity = generation.GenerationIdentity,
            CacheKey = generation.CacheKey,
            Extension = generation.ToGenerationRequest().Output.FileExtension,
            IsPublic = submission.IsPublic,
            State = "Queued",
            CreatedAtUtc = submission.CreatedAtUtc,
            UpdatedAtUtc = submission.CreatedAtUtc,
            Version = 1,
        };

    private static JobRow CreateJob(
        PersistedDerivativeSubmission submission,
        DerivativeGenerationDescriptorV1 generation) =>
        new()
        {
            Id = submission.JobId,
            TenantId = generation.TenantId,
            Type = DerivativeJobContract.TypeName,
            Payload = DerivativeJobContract.Serialize(submission.JobPayload),
            PayloadVersion = DerivativeJobContract.PayloadVersion,
            DedupeKey =
                DerivativeJobContract.CreateDedupeKey(submission.JobPayload).Value,
            Priority = 0,
            MaxAttempts = 5,
            State = "Pending",
            AvailableAtUtc = submission.CreatedAtUtc,
            CreatedAtUtc = submission.CreatedAtUtc,
            Version = 1,
        };

    private static bool IsPreGenerated(DerivativeRequestRow request) =>
        request.Id == request.JobId &&
        request.IdempotencyKey ==
            DerivativeRequestPersistenceIdentity
                .PreGeneratedIdempotencyKey(request.JobId) &&
        request.RequestHash == request.GenerationIdentity;

    private static PersistedDerivativeRequest ToPersisted(
        DerivativeRequestRow row,
        JobRow? job) =>
        new(
            row.Id,
            row.TenantId,
            row.AssetId,
            row.RevisionId,
            row.JobId,
            row.IdempotencyKey,
            row.RequestHash,
            row.PresetName,
            row.PresetRevision,
            row.Width,
            row.Height,
            row.Fit,
            row.Format,
            row.Quality,
            row.FocalPointX,
            row.FocalPointY,
            row.CropX,
            row.CropY,
            row.CropWidth,
            row.CropHeight,
            row.PipelineId,
            row.PipelineFingerprint,
            row.SourceSha256,
            row.RecipeSha256,
            row.GenerationIdentity,
            row.CacheKey,
            row.Extension,
            row.IsPublic,
            EffectiveState(row.State, job?.State),
            row.FailureCode ?? job?.FailureCode,
            row.RepresentationStorageKey,
            row.RepresentationContentLength,
            row.RepresentationContentType,
            row.RepresentationSha256,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.Version);

    private static string EffectiveState(
        string persistedState,
        string? jobState) =>
        persistedState != "Queued"
            ? persistedState
            : jobState switch
            {
                "Leased" => "Processing",
                "Completed" => "Ready",
                "DeadLettered" => "Failed",
                _ => "Queued",
            };
}
