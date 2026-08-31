namespace Vistara.Persistence.Gallery;

/// <summary>
/// The one place that turns a ready derivative into the same-origin delivery
/// path published by every gallery projection, so an asset rendition and an
/// album cover can never disagree about how an image is addressed.
/// </summary>
internal static class AssetRenditionDelivery
{
    /// <summary>
    /// The preset a cover is drawn from, in preference order. An album card is
    /// a small square, so the cover reuses the grid rendition and falls back to
    /// the thumbnail before any other ready preset.
    /// </summary>
    internal static IReadOnlyList<string> CoverPresetPreference { get; } =
        ["grid", "thumb"];

    internal static string Path(
        Guid assetId,
        Guid requestId,
        bool isPublic,
        string pipelineId,
        string sourceSha256,
        string recipeSha256,
        string extension) =>
        isPublic
            ? $"/media/{Uri.EscapeDataString(pipelineId)}/" +
                $"{Uri.EscapeDataString(sourceSha256)}/" +
                $"{Uri.EscapeDataString(recipeSha256)}." +
                Uri.EscapeDataString(extension)
            : $"/delivery/assets/{assetId:D}/{requestId:D}";
}
