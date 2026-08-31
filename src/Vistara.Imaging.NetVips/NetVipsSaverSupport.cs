using Vistara.Application.Common.Imaging;

namespace Vistara.Imaging.NetVips;

/// <summary>
/// Records which optional arguments the installed libvips savers advertise.
/// libvips added <c>target_size</c>, <c>passes</c>, and <c>smart_deblock</c> to
/// the WebP saver in 8.16 and <c>exact</c> in 8.18, so a supported runtime can
/// still reject arguments this pipeline would otherwise set unconditionally.
/// Every gate below is an argument whose absence is known and understood; any
/// other missing argument is a hard, typed failure instead of a silent
/// downgrade.
/// </summary>
internal sealed record NetVipsSaverSupport(
    bool WebpExact,
    bool WebpTargetSize,
    bool WebpPasses,
    bool WebpSmartDeblock)
{
    private static readonly string[] RequiredJpegArguments =
    [
        "Q",
        "optimize_coding",
        "interlace",
        "trellis_quant",
        "overshoot_deringing",
        "optimize_scans",
        "quant_table",
        "subsample_mode",
        "restart_interval",
        "keep",
    ];

    private static readonly string[] RequiredPngArguments =
    [
        "Q",
        "compression",
        "interlace",
        "filter",
        "palette",
        "dither",
        "bitdepth",
        "effort",
        "keep",
    ];

    private static readonly string[] RequiredWebpArguments =
    [
        "Q",
        "lossless",
        "preset",
        "smart_subsample",
        "near_lossless",
        "alpha_q",
        "min_size",
        "effort",
        "mixed",
        "keep",
    ];

    /// <summary>
    /// Describes the WebP transparency handling this runtime can actually
    /// perform. The token travels in the pipeline fingerprint because omitting
    /// <c>exact</c> changes the encoded bytes for images with transparency.
    /// </summary>
    public string WebpExactToken => WebpExact ? "exact=true" : "exact=unavailable";

    /// <summary>
    /// The optional arguments the WebP writer sets on this runtime. Keep in
    /// step with <c>NetVipsImageProcessor.WriteDeterministically</c>; NetVips
    /// rejects any argument the installed saver does not advertise.
    /// </summary>
    public IReadOnlyList<string> WebpArgumentsInUse =>
    [
        .. RequiredWebpArguments,
        .. WebpExact ? new[] { "exact" } : [],
        .. WebpTargetSize ? new[] { "target_size" } : [],
        .. WebpSmartDeblock ? new[] { "smart_deblock" } : [],
        .. WebpPasses ? new[] { "passes" } : [],
    ];

    /// <summary>
    /// Builds the support matrix from the optional argument names advertised by
    /// the installed <c>jpegsave_target</c>, <c>pngsave_target</c>, and
    /// <c>webpsave_target</c> operations.
    /// </summary>
    public static NetVipsSaverSupport FromOptionalArguments(
        IReadOnlySet<string> jpegArguments,
        IReadOnlySet<string> pngArguments,
        IReadOnlySet<string> webpArguments)
    {
        ArgumentNullException.ThrowIfNull(jpegArguments);
        ArgumentNullException.ThrowIfNull(pngArguments);
        ArgumentNullException.ThrowIfNull(webpArguments);

        RequireArguments("jpegsave_target", jpegArguments, RequiredJpegArguments);
        RequireArguments("pngsave_target", pngArguments, RequiredPngArguments);
        RequireArguments("webpsave_target", webpArguments, RequiredWebpArguments);

        return new NetVipsSaverSupport(
            webpArguments.Contains("exact"),
            webpArguments.Contains("target_size"),
            webpArguments.Contains("passes"),
            webpArguments.Contains("smart_deblock"));
    }

    private static void RequireArguments(
        string operation,
        IReadOnlySet<string> advertised,
        IEnumerable<string> required)
    {
        string[] missing = required
            .Where(argument => !advertised.Contains(argument))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        throw new ImageProcessorException(
            ImageProcessorErrorCode.Unsupported,
            $"Native libvips {operation} does not support the deterministic save arguments " +
            $"this pipeline requires: {string.Join(", ", missing)}. " +
            "Install libvips 8.15 or newer before enabling imaging.");
    }
}
