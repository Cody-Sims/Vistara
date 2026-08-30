using Vistara.Persistence.Model;

namespace Vistara.Persistence.Derivatives;

internal sealed class DerivativeRequestRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid JobId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string PresetName { get; set; } = string.Empty;
    public int PresetRevision { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Fit { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public int Quality { get; set; }
    public decimal? FocalPointX { get; set; }
    public decimal? FocalPointY { get; set; }
    public decimal? CropX { get; set; }
    public decimal? CropY { get; set; }
    public decimal? CropWidth { get; set; }
    public decimal? CropHeight { get; set; }
    public string PipelineId { get; set; } = string.Empty;
    public string PipelineFingerprint { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string RecipeSha256 { get; set; } = string.Empty;
    public string GenerationIdentity { get; set; } = string.Empty;
    public string CacheKey { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public string State { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public string? RepresentationStorageKey { get; set; }
    public long? RepresentationContentLength { get; set; }
    public string? RepresentationContentType { get; set; }
    public string? RepresentationSha256 { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}

internal static class DerivativeRequestPersistenceIdentity
{
    internal static string PreGeneratedIdempotencyKey(Guid jobId) =>
        $"internal/pregenerated/{jobId:N}";
}
