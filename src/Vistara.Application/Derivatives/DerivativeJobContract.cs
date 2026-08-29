using System.Text.Json;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Derivatives;

public sealed record DerivativeJobPayloadV1
{
    public DerivativeJobPayloadV1(
        Guid assetId,
        Guid revisionId,
        string preset)
    {
        EnsureUuid7(assetId, nameof(assetId));
        EnsureUuid7(revisionId, nameof(revisionId));
        _ = new DerivativePresetId(preset, DerivativeJobContract.PresetRevision);
        AssetId = assetId;
        RevisionId = revisionId;
        Preset = preset.Trim();
    }

    public Guid AssetId { get; }

    public Guid RevisionId { get; }

    public string Preset { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Derivative job IDs must be UUIDv7 values.", parameterName);
        }
    }
}

public static class DerivativeJobContract
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public const string TypeName = "asset.derivative.generate";
    public const int PayloadVersion = 1;
    public const int PresetRevision = 1;

    public static JobType Type { get; } = new(TypeName);

    public static string Serialize(DerivativeJobPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static bool TryParse(
        JobType type,
        int payloadVersion,
        string json,
        out DerivativeJobPayloadV1? payload)
    {
        payload = null;
        if (type != Type || payloadVersion != PayloadVersion)
        {
            return false;
        }

        try
        {
            PayloadDto? parsed = JsonSerializer.Deserialize<PayloadDto>(json, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            payload = new DerivativeJobPayloadV1(
                parsed.AssetId,
                parsed.RevisionId,
                parsed.Preset);
            return true;
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException or
                InvalidOperationException)
        {
            return false;
        }
    }

    public static JobDedupeKey CreateDedupeKey(DerivativeJobPayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new JobDedupeKey(
            $"asset-revision:{payload.RevisionId:D}:preset:{payload.Preset}:" +
            $"{PayloadVersion}");
    }

    private sealed record PayloadDto(
        Guid AssetId,
        Guid RevisionId,
        string Preset);
}
