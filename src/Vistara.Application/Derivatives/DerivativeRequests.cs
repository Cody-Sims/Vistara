using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Common.Imaging;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Derivatives;

public sealed record DerivativeSourceIdentity
{
    public DerivativeSourceIdentity(
        Guid tenantId,
        Guid assetId,
        Guid revisionId,
        long revisionNumber,
        ImageSha256 sourceSha256)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(assetId, nameof(assetId));
        EnsureUuid7(revisionId, nameof(revisionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revisionNumber);
        ArgumentNullException.ThrowIfNull(sourceSha256);
        TenantId = tenantId;
        AssetId = assetId;
        RevisionId = revisionId;
        RevisionNumber = revisionNumber;
        SourceSha256 = sourceSha256;
    }

    public Guid TenantId { get; }

    public Guid AssetId { get; }

    public Guid RevisionId { get; }

    public long RevisionNumber { get; }

    public ImageSha256 SourceSha256 { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Derivative IDs must be UUIDv7 values.", parameterName);
        }
    }
}

public sealed record DerivativeGenerationIdentity
{
    private DerivativeGenerationIdentity(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static DerivativeGenerationIdentity Create(
        DerivativeSourceIdentity source,
        DerivativePresetId preset,
        string recipeFingerprint,
        ImagePipelineFingerprint pipelineFingerprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preset);
        EnsureSha256(recipeFingerprint, nameof(recipeFingerprint));
        ArgumentNullException.ThrowIfNull(pipelineFingerprint);
        if (pipelineFingerprint.Value.Length > 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pipelineFingerprint),
                "Pipeline fingerprints cannot exceed 512 characters.");
        }

        using MemoryStream framed = new();
        WriteFrame(framed, "domain", "vistara.derivative.identity.v1");
        WriteFrame(framed, "tenant", source.TenantId.ToString("N"));
        WriteFrame(framed, "asset", source.AssetId.ToString("N"));
        WriteFrame(framed, "revision-id", source.RevisionId.ToString("N"));
        WriteFrame(
            framed,
            "revision-number",
            source.RevisionNumber.ToString(CultureInfo.InvariantCulture));
        WriteFrame(framed, "source-sha256", source.SourceSha256.Value);
        WriteFrame(framed, "preset-name", preset.Name);
        WriteFrame(
            framed,
            "preset-revision",
            preset.Revision.ToString(CultureInfo.InvariantCulture));
        WriteFrame(framed, "recipe-sha256", recipeFingerprint);
        WriteFrame(framed, "pipeline", pipelineFingerprint.Value);
        return new DerivativeGenerationIdentity(
            Convert.ToHexStringLower(SHA256.HashData(framed.ToArray())));
    }

    private static void WriteFrame(Stream destination, string name, string value)
    {
        WriteLengthPrefixed(destination, Encoding.UTF8.GetBytes(name));
        WriteLengthPrefixed(destination, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteLengthPrefixed(Stream destination, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        destination.Write(length);
        destination.Write(value);
    }

    private static void EnsureSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Recipe fingerprints must be SHA-256 hexadecimal values.",
                parameterName);
        }
    }
}

public sealed record DerivativeCacheKey
{
    private DerivativeCacheKey(string value)
    {
        Value = value;
    }

    public string Value { get; }

    internal static DerivativeCacheKey Create(
        DerivativeGenerationIdentity identity,
        DerivativeFormat format)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string extension = format switch
        {
            DerivativeFormat.Jpeg => "jpg",
            DerivativeFormat.Png => "png",
            DerivativeFormat.WebP => "webp",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return new DerivativeCacheKey(
            $"derivatives/v1/{identity.Value[..2]}/{identity.Value}.{extension}");
    }

    public override string ToString() => Value;
}

public sealed record DerivativeOutputDescriptor
{
    internal DerivativeOutputDescriptor(DerivativeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Dimensions = recipe.Dimensions;
        Format = recipe.Format;
        (FileExtension, ContentType) = recipe.Format switch
        {
            DerivativeFormat.Jpeg => ("jpg", "image/jpeg"),
            DerivativeFormat.Png => ("png", "image/png"),
            DerivativeFormat.WebP => ("webp", "image/webp"),
            _ => throw new ArgumentOutOfRangeException(nameof(recipe)),
        };
    }

    public DerivativeDimensions Dimensions { get; }

    public DerivativeFormat Format { get; }

    public string FileExtension { get; }

    public string ContentType { get; }
}

public sealed record DerivativeRequest
{
    public DerivativeRequest(
        DerivativeSourceIdentity source,
        DerivativeOutputRequest output,
        ImagePipelineFingerprint pipelineFingerprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pipelineFingerprint);
        Source = source;
        Output = output;
        PipelineFingerprint = pipelineFingerprint;
    }

    public DerivativeSourceIdentity Source { get; }

    public DerivativeOutputRequest Output { get; }

    public ImagePipelineFingerprint PipelineFingerprint { get; }
}

public sealed record DerivativeGenerationRequest
{
    internal DerivativeGenerationRequest(
        DerivativeSourceIdentity source,
        DerivativePreset preset,
        DerivativeRecipe recipe,
        ImagePipelineFingerprint pipelineFingerprint)
    {
        Source = source;
        Preset = preset;
        Recipe = recipe;
        PipelineFingerprint = pipelineFingerprint;
        Identity = DerivativeGenerationIdentity.Create(
            source,
            preset.Id,
            recipe.Fingerprint,
            pipelineFingerprint);
        CacheKey = DerivativeCacheKey.Create(Identity, recipe.Format);
        DedupeIdentity = new JobDedupeIdentity(
            new JobTenantId(source.TenantId),
            new JobDedupeKey($"derivative:{Identity.Value}"));
        Output = new DerivativeOutputDescriptor(recipe);
    }

    public DerivativeSourceIdentity Source { get; }

    public DerivativePreset Preset { get; }

    public DerivativeRecipe Recipe { get; }

    public ImagePipelineFingerprint PipelineFingerprint { get; }

    public DerivativeGenerationIdentity Identity { get; }

    public DerivativeCacheKey CacheKey { get; }

    public JobDedupeIdentity DedupeIdentity { get; }

    public DerivativeOutputDescriptor Output { get; }
}

public sealed record DerivativeResolutionResult(
    DerivativeNegotiationStatus Status,
    DerivativeGenerationRequest? GenerationRequest);

public sealed record DerivativeGenerationResult
{
    public DerivativeGenerationResult(
        DerivativeGenerationIdentity identity,
        DerivativeCacheKey cacheKey,
        DerivativeOutputDescriptor output,
        long bytesWritten,
        ImageSha256 representationSha256)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesWritten);
        ArgumentNullException.ThrowIfNull(representationSha256);
        Identity = identity;
        CacheKey = cacheKey;
        Output = output;
        BytesWritten = bytesWritten;
        RepresentationSha256 = representationSha256;
    }

    public DerivativeGenerationIdentity Identity { get; }

    public DerivativeCacheKey CacheKey { get; }

    public DerivativeOutputDescriptor Output { get; }

    public long BytesWritten { get; }

    public ImageSha256 RepresentationSha256 { get; }
}

public sealed partial class DerivativePresetRegistry
{
    public DerivativeResolutionResult Resolve(DerivativeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DerivativeNegotiationResult negotiation = Negotiate(request.Output);
        if (negotiation.Status != DerivativeNegotiationStatus.Selected ||
            negotiation.Preset is null ||
            negotiation.Recipe is null)
        {
            return new DerivativeResolutionResult(negotiation.Status, null);
        }

        return new DerivativeResolutionResult(
            DerivativeNegotiationStatus.Selected,
            new DerivativeGenerationRequest(
                request.Source,
                negotiation.Preset,
                negotiation.Recipe,
                request.PipelineFingerprint));
    }
}
