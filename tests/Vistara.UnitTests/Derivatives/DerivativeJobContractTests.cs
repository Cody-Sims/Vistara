using System.Security.Cryptography;
using System.Text.Json;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Domain.Jobs;
using Xunit;

namespace Vistara.UnitTests.Derivatives;

public sealed class DerivativeJobContractTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void Asset_ingest_payload_is_the_versioned_derivative_job_contract()
    {
        Guid assetId = Guid.CreateVersion7();
        Guid revisionId = Guid.CreateVersion7();
        string json = JsonSerializer.Serialize(
            new
            {
                assetId,
                revisionId,
                preset = "thumb",
            },
            JsonOptions);

        bool parsed = DerivativeJobContract.TryParse(
            new JobType("asset.derivative.generate"),
            payloadVersion: 1,
            json,
            out DerivativeJobPayloadV1? payload);

        Assert.True(parsed);
        Assert.NotNull(payload);
        Assert.Equal(assetId, payload.AssetId);
        Assert.Equal(revisionId, payload.RevisionId);
        Assert.Equal("thumb", payload.Preset);
        Assert.Equal(
            $"asset-revision:{revisionId:D}:preset:thumb:1",
            DerivativeJobContract.CreateDedupeKey(payload).Value);
    }

    [Theory]
    [InlineData("derivative.generate", 1)]
    [InlineData("asset.derivative.generate", 2)]
    public void Unsupported_job_alias_or_payload_version_is_rejected(
        string type,
        int payloadVersion)
    {
        string json = JsonSerializer.Serialize(
            new
            {
                assetId = Guid.CreateVersion7(),
                revisionId = Guid.CreateVersion7(),
                preset = "thumb",
            });

        Assert.False(DerivativeJobContract.TryParse(
            new JobType(type),
            payloadVersion,
            json,
            out _));
    }

    [Theory]
    [InlineData("thumb", 256)]
    [InlineData("grid", 512)]
    [InlineData("viewer", 1024)]
    [InlineData("download-web", 1024)]
    public void Default_ingest_derivative_selects_smallest_preferred_webp(
        string preset,
        int expectedDimension)
    {
        var source = new DerivativeSourceIdentity(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            revisionNumber: 1,
            new ImageSha256(Convert.ToHexStringLower(SHA256.HashData("source"u8))));

        DerivativeResolutionResult result = DerivativePresetRegistry.Standard.ResolveDefault(
            source,
            new DerivativePresetId(preset, 1),
            new ImagePipelineFingerprint("pipeline"));

        Assert.Equal(DerivativeNegotiationStatus.Selected, result.Status);
        Assert.Equal(expectedDimension, result.GenerationRequest?.Recipe.Dimensions.Width);
        Assert.Equal(expectedDimension, result.GenerationRequest?.Recipe.Dimensions.Height);
        Assert.Equal(DerivativeFormat.WebP, result.GenerationRequest?.Recipe.Format);
    }
}
