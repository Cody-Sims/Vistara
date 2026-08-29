using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistara.Api.Features.Derivatives;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Contracts.Derivatives;
using Vistara.Contracts.Idempotency;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Media;

namespace Vistara.Api.Composition.Platform;

internal sealed class PlatformDerivativePersistenceAdapter(
    RelationalDerivativeRequestStore requests,
    RelationalMediaCatalogStore media,
    PlatformDerivativePresetCatalog presets,
    IImageProcessor imageProcessor,
    IClock clock,
    IUuid7Generator idGenerator) : IDerivativeApplicationPort
{
    public ValueTask<IReadOnlyList<DerivativePresetDefinition>> ListPresetsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(presets.Definitions);
    }

    public ValueTask<DerivativeCanonicalizationResult> CanonicalizeAsync(
        DerivativeAssetScope scope,
        DerivativeRequestContract request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlatformDerivativeResolution resolution = presets.Resolve(request);
        return ValueTask.FromResult(
            resolution.Status == DerivativeCanonicalizationStatus.Accepted
                ? DerivativeCanonicalizationResult.Accepted(
                    resolution.Canonical!)
                : DerivativeCanonicalizationResult.Rejected(
                    resolution.Status));
    }

    public async ValueTask<DerivativeSubmissionResult> RequestAsync(
        DerivativeAssetScope scope,
        CanonicalDerivativeRequest request,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        PlatformDerivativeResolvedRecipe? resolved =
            presets.FindCanonical(request);
        if (resolved is null)
        {
            throw new InvalidOperationException(
                "The canonical derivative request is not in the active preset catalog.");
        }

        PersistedDerivativeSource? source = await requests.GetSourceAsync(
            scope.TenantId,
            scope.AssetId,
            cancellationToken);
        if (source is null)
        {
            throw new InvalidOperationException(
                "The derivative source is unavailable.");
        }

        var sourceIdentity = new DerivativeSourceIdentity(
            source.TenantId,
            source.AssetId,
            source.RevisionId,
            source.RevisionNumber,
            new ImageSha256(source.SourceSha256));
        DerivativeGenerationRequest generation = new DerivativePresetRegistry(
                [resolved.Preset])
            .Resolve(new DerivativeRequest(
                sourceIdentity,
                new DerivativeOutputRequest(
                    resolved.Preset.Id,
                    resolved.Recipe.Dimensions,
                    [resolved.Recipe.Format]),
                imageProcessor.PipelineFingerprint))
            .GenerationRequest ??
            throw new InvalidOperationException(
                "The canonical derivative request could not be resolved.");

        DateTimeOffset now = clock.UtcNow;
        Guid requestId = idGenerator.NewId();
        string pipelineId = Hash(imageProcessor.PipelineFingerprint.Value);
        var jobPayload = new DerivativeJobPayloadV1(
            source.AssetId,
            source.RevisionId,
            resolved.Preset.Id.Name);
        string payload = DerivativeJobContract.Serialize(jobPayload);
        string jobDedupeKey =
            DerivativeJobContract.CreateDedupeKey(jobPayload).Value;
        PersistedDerivativeSubmissionResult stored = await requests.SubmitAsync(
            new PersistedDerivativeSubmission(
                requestId,
                source.TenantId,
                source.AssetId,
                source.RevisionId,
                requestId,
                payload,
                jobDedupeKey,
                idempotencyKey.Value,
                request.RequestHash,
                request.Preset,
                request.Revision,
                request.Parameters.Width,
                request.Parameters.Height,
                request.Parameters.Fit,
                request.Parameters.Format,
                request.Parameters.Quality,
                request.Parameters.FocalPoint?.X,
                request.Parameters.FocalPoint?.Y,
                request.Parameters.Crop?.X,
                request.Parameters.Crop?.Y,
                request.Parameters.Crop?.Width,
                request.Parameters.Crop?.Height,
                pipelineId,
                imageProcessor.PipelineFingerprint.Value,
                source.SourceSha256,
                resolved.Recipe.Fingerprint,
                generation.Identity.Value,
                generation.CacheKey.Value,
                generation.Output.FileExtension,
                source.IsPublic,
                now),
            cancellationToken);
        if (stored.Status == PersistedDerivativeSubmissionStatus.IdempotencyConflict)
        {
            return DerivativeSubmissionResult.Conflict();
        }

        PersistedDerivativeRequest persisted = stored.Request ??
            throw new InvalidOperationException(
                "A persisted derivative submission must return its request.");
        if (source.IsPublic)
        {
            await media.RegisterPublicDerivativeAsync(
                source.TenantId,
                persisted.RequestId,
                persisted.PipelineId,
                persisted.SourceSha256,
                persisted.RecipeSha256,
                persisted.Extension,
                persisted.CreatedAtUtc,
                cancellationToken);
        }

        DerivativeWorkSnapshot snapshot = ToSnapshot(persisted);
        bool replayed =
            stored.Status == PersistedDerivativeSubmissionStatus.Replayed;
        bool reused =
            stored.Status == PersistedDerivativeSubmissionStatus.Reused;
        return snapshot.State == DerivativeWorkState.Ready
            ? DerivativeSubmissionResult.Ready(
                snapshot,
                reusedExisting: reused,
                replayed)
            : DerivativeSubmissionResult.Accepted(
                snapshot,
                reusedExisting: reused,
                replayed);
    }

    public async ValueTask<IReadOnlyList<DerivativeWorkSnapshot>> ListAsync(
        DerivativeAssetScope scope,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PersistedDerivativeRequest> persisted =
            await requests.ListAsync(
                scope.TenantId,
                scope.AssetId,
                cancellationToken);
        return persisted.Select(ToSnapshot).ToArray();
    }

    public async ValueTask<DerivativeWorkSnapshot?> GetStatusAsync(
        DerivativeAssetScope scope,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        PersistedDerivativeRequest? persisted = await requests.GetAsync(
            scope.TenantId,
            scope.AssetId,
            requestId,
            cancellationToken);
        return persisted is null ? null : ToSnapshot(persisted);
    }

    private static DerivativeWorkSnapshot ToSnapshot(
        PersistedDerivativeRequest request)
    {
        DerivativeWorkState state = Enum.Parse<DerivativeWorkState>(
            request.State);
        DerivativeReadyRepresentation? representation =
            state == DerivativeWorkState.Ready &&
            request.RepresentationContentType is not null &&
            request.RepresentationSha256 is not null
                ? new DerivativeReadyRepresentation(
                    request.Width,
                    request.Height,
                    request.Format,
                    request.RepresentationContentType,
                    $"\"{request.RepresentationSha256}\"")
                : null;
        return new DerivativeWorkSnapshot(
            request.RequestId,
            request.PresetName,
            request.PresetRevision,
            new CanonicalDerivativeParameters(
                request.Width,
                request.Height,
                request.Fit,
                request.Format,
                request.Quality,
                request.FocalPointX.HasValue && request.FocalPointY.HasValue
                    ? new DerivativeFocalPointContract(
                        request.FocalPointX.Value,
                        request.FocalPointY.Value)
                    : null,
                request.CropX.HasValue &&
                request.CropY.HasValue &&
                request.CropWidth.HasValue &&
                request.CropHeight.HasValue
                    ? new DerivativeCropRectangleContract(
                        request.CropX.Value,
                        request.CropY.Value,
                        request.CropWidth.Value,
                        request.CropHeight.Value)
                    : null),
            state,
            request.Version,
            request.CreatedAtUtc,
            request.UpdatedAtUtc,
            representation,
            request.FailureCode);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ToName(DerivativeFormat format) => format switch
    {
        DerivativeFormat.Jpeg => "jpeg",
        DerivativeFormat.Png => "png",
        DerivativeFormat.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

}

internal sealed class PlatformDerivativePresetCatalog
{
    private readonly Dictionary<string, DerivativePreset> _presets;

    public PlatformDerivativePresetCatalog()
    {
        DerivativePreset[] presets =
        [
            CreatePreset("thumb", DerivativeFit.Cover, [256, 512], false),
            CreatePreset("grid", DerivativeFit.Cover, [512, 1_024], false),
            CreatePreset(
                "viewer",
                DerivativeFit.Contain,
                [1_024, 1_600, 2_400],
                false),
            CreatePreset(
                "download-web",
                DerivativeFit.Contain,
                [1_024, 1_600, 2_400],
                true),
        ];
        _presets = presets.ToDictionary(
            preset => preset.Id.Name,
            StringComparer.Ordinal);
        Definitions = presets
            .Select(ToDefinition)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    internal IReadOnlyList<DerivativePresetDefinition> Definitions { get; }

    internal PlatformDerivativeResolution Resolve(
        DerivativeRequestContract request)
    {
        if (!_presets.TryGetValue(request.Preset, out DerivativePreset? preset))
        {
            return PlatformDerivativeResolution.Rejected(
                DerivativeCanonicalizationStatus.PresetNotFound);
        }

        if (request.Revision != preset.Id.Revision)
        {
            return PlatformDerivativeResolution.Rejected(
                DerivativeCanonicalizationStatus.RevisionNotActive);
        }

        DerivativeParametersContract? parameters = request.Parameters;
        IEnumerable<DerivativeRecipe> candidates = [DefaultRecipe(preset)];
        if (parameters?.Width is { } width)
        {
            candidates = candidates.Where(
                recipe => recipe.Dimensions.Width == width);
        }

        if (parameters?.Height is { } height)
        {
            candidates = candidates.Where(
                recipe => recipe.Dimensions.Height == height);
        }

        if (parameters?.Fit is { } fit)
        {
            candidates = candidates.Where(
                recipe => ToName(recipe.Fit) == fit);
        }

        if (parameters?.Format is { } format)
        {
            candidates = candidates.Where(
                recipe => ToName(recipe.Format) == format);
        }
        else
        {
            DerivativeRecipe[] preferred = candidates
                .Where(recipe => recipe.Format == DerivativeFormat.WebP)
                .ToArray();
            if (preferred.Length > 0)
            {
                candidates = preferred;
            }
        }

        if (parameters?.Quality is { } quality)
        {
            candidates = candidates.Where(recipe => recipe.Quality == quality);
        }

        if (parameters?.FocalPoint is not null ||
            parameters?.Crop is not null ||
            parameters?.MetadataPolicy == "strip-all")
        {
            return PlatformDerivativeResolution.Rejected(
                DerivativeCanonicalizationStatus.ParametersNotAllowed);
        }

        DerivativeRecipe? selected = candidates
            .OrderBy(recipe => recipe.Dimensions.Width)
            .ThenBy(recipe => recipe.Dimensions.Height)
            .ThenBy(recipe => recipe.Format)
            .FirstOrDefault();
        if (selected is null)
        {
            return PlatformDerivativeResolution.Rejected(
                DerivativeCanonicalizationStatus.ParametersNotAllowed);
        }

        var canonicalParameters = new CanonicalDerivativeParameters(
            selected.Dimensions.Width,
            selected.Dimensions.Height,
            ToName(selected.Fit),
            ToName(selected.Format),
            selected.Quality,
            parameters?.FocalPoint,
            parameters?.Crop);
        var canonical = new CanonicalDerivativeRequest(
            preset.Id.Name,
            preset.Id.Revision,
            canonicalParameters,
            HashCanonical(
                preset.Id.Name,
                preset.Id.Revision,
                canonicalParameters));
        return PlatformDerivativeResolution.Accepted(
            canonical,
            preset,
            selected);
    }

    internal PlatformDerivativeResolvedRecipe? FindCanonical(
        CanonicalDerivativeRequest request)
    {
        if (!_presets.TryGetValue(
                request.Preset,
                out DerivativePreset? preset) ||
            preset.Id.Revision != request.Revision ||
            request.Parameters.FocalPoint is not null ||
            request.Parameters.Crop is not null ||
            !string.Equals(
                request.RequestHash,
                HashCanonical(
                    request.Preset,
                    request.Revision,
                    request.Parameters),
                StringComparison.Ordinal))
        {
            return null;
        }

        DerivativeRecipe? recipe = preset.Outputs.SingleOrDefault(
            candidate =>
                candidate.Dimensions.Width == request.Parameters.Width &&
                candidate.Dimensions.Height == request.Parameters.Height &&
                ToName(candidate.Fit) == request.Parameters.Fit &&
                ToName(candidate.Format) == request.Parameters.Format &&
                candidate.Quality == request.Parameters.Quality);
        return recipe is null
            ? null
            : new PlatformDerivativeResolvedRecipe(preset, recipe);
    }

    private static DerivativePresetDefinition ToDefinition(
        DerivativePreset preset)
    {
        DerivativeRecipe[] outputs = [DefaultRecipe(preset)];
        var bounds = new DerivativeParameterBounds(
            outputs.Min(output => output.Dimensions.Width),
            outputs.Max(output => output.Dimensions.Width),
            outputs.Min(output => output.Dimensions.Height),
            outputs.Max(output => output.Dimensions.Height),
            outputs.Min(output => output.Quality),
            outputs.Max(output => output.Quality),
            outputs.Select(output => ToName(output.Fit))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            outputs.Select(output => ToName(output.Format))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
        return new DerivativePresetDefinition(
            preset.Id.Name,
            preset.Id.Revision,
            [
                new DerivativePresetRevisionDefinition(
                    preset.Id.Revision,
                    true,
                    bounds),
            ]);
    }

    private static DerivativeRecipe DefaultRecipe(DerivativePreset preset)
    {
        DerivativeDimensions dimensions = preset.Outputs
            .OrderBy(output => output.Dimensions.Width)
            .ThenBy(output => output.Dimensions.Height)
            .Select(output => output.Dimensions)
            .First();
        return preset.Outputs.Single(output =>
            output.Dimensions == dimensions &&
            output.Format == DerivativeFormat.WebP);
    }

    private static DerivativePreset CreatePreset(
        string name,
        DerivativeFit fit,
        IEnumerable<int> sizes,
        bool includePng)
    {
        var outputs = new List<DerivativeRecipe>();
        foreach (int size in sizes)
        {
            outputs.Add(CreateRecipe(size, fit, DerivativeFormat.WebP));
            outputs.Add(CreateRecipe(size, fit, DerivativeFormat.Jpeg));
            if (includePng)
            {
                outputs.Add(CreateRecipe(size, fit, DerivativeFormat.Png));
            }
        }

        return new DerivativePreset(
            new DerivativePresetId(name, 1),
            outputs);
    }

    private static DerivativeRecipe CreateRecipe(
        int size,
        DerivativeFit fit,
        DerivativeFormat format) =>
        new(
            1,
            new DerivativeDimensions(size, size),
            fit,
            format,
            format == DerivativeFormat.Png ? 100 : 82,
            format == DerivativeFormat.Jpeg
                ? DerivativeBackground.White
                : DerivativeBackground.Transparent,
            false,
            DerivativeMetadataBehavior.StripSensitive);

    private static string HashCanonical(
        string preset,
        int revision,
        CanonicalDerivativeParameters parameters)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("preset", preset);
            writer.WriteNumber("revision", revision);
            writer.WriteNumber("width", parameters.Width);
            writer.WriteNumber("height", parameters.Height);
            writer.WriteString("fit", parameters.Fit);
            writer.WriteString("format", parameters.Format);
            writer.WriteNumber("quality", parameters.Quality);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(buffer.ToArray()));
    }

    private static string ToName(DerivativeFit fit) => fit switch
    {
        DerivativeFit.Contain => "contain",
        DerivativeFit.Cover => "cover",
        DerivativeFit.Crop => "crop",
        _ => throw new ArgumentOutOfRangeException(nameof(fit)),
    };

    private static string ToName(DerivativeFormat format) => format switch
    {
        DerivativeFormat.Jpeg => "jpeg",
        DerivativeFormat.Png => "png",
        DerivativeFormat.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

internal sealed record PlatformDerivativeResolvedRecipe(
    DerivativePreset Preset,
    DerivativeRecipe Recipe);

internal sealed record PlatformDerivativeResolution(
    DerivativeCanonicalizationStatus Status,
    CanonicalDerivativeRequest? Canonical,
    PlatformDerivativeResolvedRecipe? Resolved)
{
    internal static PlatformDerivativeResolution Accepted(
        CanonicalDerivativeRequest canonical,
        DerivativePreset preset,
        DerivativeRecipe recipe) =>
        new(
            DerivativeCanonicalizationStatus.Accepted,
            canonical,
            new PlatformDerivativeResolvedRecipe(preset, recipe));

    internal static PlatformDerivativeResolution Rejected(
        DerivativeCanonicalizationStatus status) =>
        new(status, null, null);
}
