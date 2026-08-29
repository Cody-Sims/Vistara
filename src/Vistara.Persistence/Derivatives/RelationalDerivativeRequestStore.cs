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

public sealed record PersistedDerivativeSubmission(
    Guid RequestId,
    Guid TenantId,
    Guid AssetId,
    Guid RevisionId,
    Guid JobId,
    string JobPayload,
    string JobDedupeKey,
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
    DateTimeOffset CreatedAtUtc);

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
        EnsureTenant(submission.TenantId);
        DerivativeRequestRow? idempotent = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.AssetId == submission.AssetId &&
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
                row => row.GenerationIdentity == submission.GenerationIdentity,
                cancellationToken);
        if (reusable is not null)
        {
            JobRow? job = await FindJobAsync(
                reusable.JobId,
                cancellationToken);
            return new(
                PersistedDerivativeSubmissionStatus.Reused,
                ToPersisted(reusable, job));
        }

        var request = new DerivativeRequestRow
        {
            Id = submission.RequestId,
            TenantId = submission.TenantId,
            AssetId = submission.AssetId,
            RevisionId = submission.RevisionId,
            JobId = submission.JobId,
            IdempotencyKey = submission.IdempotencyKey,
            RequestHash = submission.RequestHash,
            PresetName = submission.PresetName,
            PresetRevision = submission.PresetRevision,
            Width = submission.Width,
            Height = submission.Height,
            Fit = submission.Fit,
            Format = submission.Format,
            Quality = submission.Quality,
            FocalPointX = submission.FocalPointX,
            FocalPointY = submission.FocalPointY,
            CropX = submission.CropX,
            CropY = submission.CropY,
            CropWidth = submission.CropWidth,
            CropHeight = submission.CropHeight,
            PipelineId = submission.PipelineId,
            PipelineFingerprint = submission.PipelineFingerprint,
            SourceSha256 = submission.SourceSha256,
            RecipeSha256 = submission.RecipeSha256,
            GenerationIdentity = submission.GenerationIdentity,
            CacheKey = submission.CacheKey,
            Extension = submission.Extension,
            IsPublic = submission.IsPublic,
            State = "Queued",
            CreatedAtUtc = submission.CreatedAtUtc,
            UpdatedAtUtc = submission.CreatedAtUtc,
            Version = 1,
        };
        _context.Add(request);
        _context.Jobs.Add(new JobRow
        {
            Id = submission.JobId,
            TenantId = submission.TenantId,
            Type = DerivativeJobContract.TypeName,
            Payload = submission.JobPayload,
            PayloadVersion = DerivativeJobContract.PayloadVersion,
            DedupeKey = submission.JobDedupeKey,
            Priority = 0,
            MaxAttempts = 5,
            State = "Pending",
            AvailableAtUtc = submission.CreatedAtUtc,
            CreatedAtUtc = submission.CreatedAtUtc,
            Version = 1,
        });
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new(
                PersistedDerivativeSubmissionStatus.Created,
                ToPersisted(request, null));
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            DerivativeRequestRow? raced = await _context
                .Set<DerivativeRequestRow>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row =>
                        row.GenerationIdentity == submission.GenerationIdentity ||
                        (row.AssetId == submission.AssetId &&
                         row.IdempotencyKey == submission.IdempotencyKey),
                    cancellationToken);
            if (raced is null)
            {
                throw;
            }

            if (raced.IdempotencyKey == submission.IdempotencyKey &&
                raced.RequestHash != submission.RequestHash)
            {
                return new(
                    PersistedDerivativeSubmissionStatus.IdempotencyConflict,
                    null);
            }

            return new(
                raced.IdempotencyKey == submission.IdempotencyKey
                    ? PersistedDerivativeSubmissionStatus.Replayed
                    : PersistedDerivativeSubmissionStatus.Reused,
                ToPersisted(
                    raced,
                    await FindJobAsync(
                        raced.JobId,
                        cancellationToken)));
        }
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
