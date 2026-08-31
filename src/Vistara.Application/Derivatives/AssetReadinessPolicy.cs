namespace Vistara.Application.Derivatives;

/// <summary>
/// The authoritative criterion for promoting an asset from
/// <c>Processing</c> to <c>Ready</c>. Ingest pre-generates one derivative per
/// required standard preset for the current revision, and the asset only
/// becomes deliverable once every one of those presets has a successfully
/// published derivative. Partial, failed, or dead-lettered work therefore
/// never advertises a rendition.
/// </summary>
public static class AssetReadinessPolicy
{
    /// <summary>
    /// The standard derivative presets from the specification. Ingest enqueues
    /// exactly these, and readiness requires exactly these.
    /// </summary>
    public static IReadOnlyList<string> RequiredPresetNames { get; } =
        ["thumb", "grid", "viewer", "download-web"];

    public static bool IsSatisfiedBy(IEnumerable<string> readyPresetNames)
    {
        ArgumentNullException.ThrowIfNull(readyPresetNames);
        HashSet<string> ready = new(readyPresetNames, StringComparer.Ordinal);
        foreach (string required in RequiredPresetNames)
        {
            if (!ready.Contains(required))
            {
                return false;
            }
        }

        return true;
    }
}
