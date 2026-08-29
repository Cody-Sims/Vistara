using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;

namespace Vistara.UnitTests.Derivatives;

public sealed class DerivativeIdentityTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000101");
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000102");
    private static readonly Guid RevisionId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000103");

    [Fact]
    public void Equivalent_requests_share_generation_cache_and_dedupe_identity()
    {
        DerivativePresetRegistry registry = Registry("viewer", revision: 4);
        DerivativeGenerationRequest first = Resolve(
            registry,
            Source(),
            "viewer",
            presetRevision: 4,
            pipeline: "netvips-3.2|libvips-8.18|pipeline-1");
        DerivativeGenerationRequest same = Resolve(
            registry,
            Source(),
            "viewer",
            presetRevision: 4,
            pipeline: "netvips-3.2|libvips-8.18|pipeline-1");

        Assert.Equal(first.Identity, same.Identity);
        Assert.Equal(first.CacheKey, same.CacheKey);
        Assert.Equal(first.DedupeIdentity, same.DedupeIdentity);
    }

    [Fact]
    public void Source_preset_recipe_and_pipeline_changes_create_new_immutable_keys()
    {
        DerivativeSourceIdentity source = Source();
        DerivativeGenerationRequest baseline = Resolve(
            Registry("viewer", revision: 4),
            source,
            "viewer",
            presetRevision: 4,
            pipeline: "pipeline-1");
        DerivativeGenerationRequest revisedPreset = Resolve(
            Registry("viewer", revision: 5),
            source,
            "viewer",
            presetRevision: 5,
            pipeline: "pipeline-1");
        DerivativeGenerationRequest changedRecipe = Resolve(
            Registry("viewer", revision: 4, quality: 83),
            source,
            "viewer",
            presetRevision: 4,
            pipeline: "pipeline-1");
        DerivativeGenerationRequest changedPipeline = Resolve(
            Registry("viewer", revision: 4),
            source,
            "viewer",
            presetRevision: 4,
            pipeline: "pipeline-2");
        DerivativeGenerationRequest changedRevision = Resolve(
            Registry("viewer", revision: 4),
            Source(revisionId: Guid.Parse("01990a2a-bc00-7000-8000-000000000104")),
            "viewer",
            presetRevision: 4,
            pipeline: "pipeline-1");

        Assert.NotEqual(baseline.CacheKey, revisedPreset.CacheKey);
        Assert.NotEqual(baseline.CacheKey, changedRecipe.CacheKey);
        Assert.NotEqual(baseline.CacheKey, changedPipeline.CacheKey);
        Assert.NotEqual(baseline.CacheKey, changedRevision.CacheKey);
    }

    [Fact]
    public void Framed_identity_does_not_conflate_ambiguous_component_boundaries()
    {
        DerivativeGenerationIdentity first = DerivativeGenerationIdentity.Create(
            Source(),
            new DerivativePresetId("ab", 1),
            recipeFingerprint: new string('1', 64),
            new ImagePipelineFingerprint("c"));
        DerivativeGenerationIdentity second = DerivativeGenerationIdentity.Create(
            Source(),
            new DerivativePresetId("a", 1),
            recipeFingerprint: new string('1', 64),
            new ImagePipelineFingerprint("bc"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Cache_and_dedupe_keys_are_opaque_lowercase_and_contain_no_sensitive_text()
    {
        const string sensitive =
            "family-photo.jpg|gps=51.5074,-0.1278|camera-owner=private";
        DerivativeGenerationRequest request = Resolve(
            Registry("viewer", revision: 1),
            Source(),
            "viewer",
            presetRevision: 1,
            pipeline: sensitive);

        Assert.StartsWith("derivatives/v1/", request.CacheKey.Value);
        Assert.EndsWith(".webp", request.CacheKey.Value);
        Assert.DoesNotContain("family", request.CacheKey.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gps", request.CacheKey.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", request.CacheKey.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("family", request.DedupeIdentity.Key.Value, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            request.CacheKey.Value,
            character => Assert.True(
                char.IsAsciiLetterLower(character) ||
                char.IsAsciiDigit(character) ||
                character is '/' or '.' or '-'));
    }

    [Fact]
    public void Resolution_exposes_typed_worker_boundaries_only_after_allowlist_negotiation()
    {
        DerivativePresetRegistry registry = Registry("viewer", revision: 1);
        DerivativeResolutionResult resolved = registry.Resolve(
            new DerivativeRequest(
                Source(),
                new DerivativeOutputRequest(
                    new DerivativePresetId("viewer", 1),
                    new DerivativeDimensions(1_600, 1_600),
                    [DerivativeFormat.WebP]),
                new ImagePipelineFingerprint("pipeline-1")));
        DerivativeResolutionResult rejected = registry.Resolve(
            new DerivativeRequest(
                Source(),
                new DerivativeOutputRequest(
                    new DerivativePresetId("viewer", 1),
                    new DerivativeDimensions(1_599, 1_599),
                    [DerivativeFormat.WebP]),
                new ImagePipelineFingerprint("pipeline-1")));

        Assert.Equal(DerivativeNegotiationStatus.Selected, resolved.Status);
        Assert.NotNull(resolved.GenerationRequest);
        Assert.Equal("image/webp", resolved.GenerationRequest.Output.ContentType);
        Assert.Equal(DerivativeNegotiationStatus.OutputNotAllowed, rejected.Status);
        Assert.Null(rejected.GenerationRequest);
    }

    [Fact]
    public void Generation_result_requires_matching_typed_identity_and_positive_bytes()
    {
        DerivativeGenerationRequest request = Resolve(
            Registry("viewer", revision: 1),
            Source(),
            "viewer",
            presetRevision: 1,
            pipeline: "pipeline-1");

        DerivativeGenerationResult result = new(
            request.Identity,
            request.CacheKey,
            request.Output,
            bytesWritten: 42,
            new ImageSha256(new string('a', 64)));

        Assert.Equal(request.Identity, result.Identity);
        Assert.Equal(42, result.BytesWritten);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DerivativeGenerationResult(
                request.Identity,
                request.CacheKey,
                request.Output,
                bytesWritten: 0,
                new ImageSha256(new string('a', 64))));
    }

    [Fact]
    public void Source_identity_requires_uuidv7_and_positive_revision_number()
    {
        Guid versionFour = Guid.Parse("11111111-1111-4111-8111-111111111111");

        Assert.Throws<ArgumentException>(
            () => Source(tenantId: versionFour));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Source(revisionNumber: 0));
    }

    private static DerivativePresetRegistry Registry(
        string name,
        int revision,
        int quality = 82) =>
        new(
            [
                new DerivativePreset(
                    new DerivativePresetId(name, revision),
                    [
                        new DerivativeRecipe(
                            schemaVersion: 1,
                            new DerivativeDimensions(1_600, 1_600),
                            DerivativeFit.Contain,
                            DerivativeFormat.WebP,
                            quality,
                            DerivativeBackground.Transparent,
                            allowUpscale: false,
                            DerivativeMetadataBehavior.StripSensitive),
                    ]),
            ]);

    private static DerivativeSourceIdentity Source(
        Guid? tenantId = null,
        Guid? assetId = null,
        Guid? revisionId = null,
        long revisionNumber = 7) =>
        new(
            tenantId ?? TenantId,
            assetId ?? AssetId,
            revisionId ?? RevisionId,
            revisionNumber,
            new ImageSha256(new string('b', 64)));

    private static DerivativeGenerationRequest Resolve(
        DerivativePresetRegistry registry,
        DerivativeSourceIdentity source,
        string preset,
        int presetRevision,
        string pipeline)
    {
        DerivativeResolutionResult resolution = registry.Resolve(
            new DerivativeRequest(
                source,
                new DerivativeOutputRequest(
                    new DerivativePresetId(preset, presetRevision),
                    new DerivativeDimensions(1_600, 1_600),
                    [DerivativeFormat.WebP]),
                new ImagePipelineFingerprint(pipeline)));

        Assert.Equal(DerivativeNegotiationStatus.Selected, resolution.Status);
        return Assert.IsType<DerivativeGenerationRequest>(resolution.GenerationRequest);
    }
}
