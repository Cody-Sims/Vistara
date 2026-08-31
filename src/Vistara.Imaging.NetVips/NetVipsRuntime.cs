using System.Reflection;
using Vistara.Application.Common.Imaging;

namespace Vistara.Imaging.NetVips;

internal static class NetVipsRuntime
{
    private const int MinimumMajorVersion = 8;
    private const int MinimumMinorVersion = 15;

    private static readonly Lazy<NetVipsRuntimeState> Runtime = new(
        Create,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static NetVipsRuntimeState State
    {
        get
        {
            try
            {
                return Runtime.Value;
            }
            catch (ImageProcessorException)
            {
                throw;
            }
            catch (Exception)
            {
                throw NativeUnavailable();
            }
        }
    }

    /// <summary>
    /// Builds the runtime description from values already read out of the
    /// native library. The method performs no native calls so the
    /// version-dependent save argument paths stay deterministically testable.
    /// </summary>
    public static NetVipsRuntimeState CreateState(
        int major,
        int minor,
        int patch,
        string netVipsVersion,
        IReadOnlySet<string> jpegArguments,
        IReadOnlySet<string> pngArguments,
        IReadOnlySet<string> webpArguments,
        bool supportsIccTransform)
    {
        ArgumentException.ThrowIfNullOrEmpty(netVipsVersion);
        RequireSupportedVersion(major, minor, patch);
        NetVipsSaverSupport savers = NetVipsSaverSupport.FromOptionalArguments(
            jpegArguments,
            pngArguments,
            webpArguments);

        string libVipsVersion = $"{major}.{minor}.{patch}";
        string fingerprint = string.Join(
            ';',
            "vistara-pipeline=2",
            "vistara-recipe-schema=1",
            $"netvips={netVipsVersion}",
            $"libvips={libVipsVersion}",
            $"jpeg[codec=libjpeg@libvips-{libVipsVersion},q=recipe,optimize-coding=true,progressive=false,subsample=on,keep=none]",
            "png[codec=libpng-or-spng@libvips-" + libVipsVersion + ",compression=9,filter=all,interlace=false,bitdepth=8,keep=none]",
            "webp[codec=libwebp@libvips-" + libVipsVersion + ",q=recipe,effort=4,passes=1," +
                savers.WebpExactToken + ",keep=none]",
            "color=embedded-icc-to-srgb-or-libvips-srgb,depth=8",
            "resize=lanczos3,cover=scale-then-anchor-crop,background=opaque-white",
            "metadata=none");

        return new NetVipsRuntimeState(
            new ImagePipelineFingerprint(fingerprint),
            supportsIccTransform,
            savers);
    }

    /// <summary>
    /// Reads the optional input argument names advertised by a libvips
    /// operation. Introspection is the only truthful source for
    /// version-dependent arguments such as WebP <c>exact</c>.
    /// </summary>
    public static IReadOnlySet<string> OptionalArguments(string operation)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        return global::NetVips.Introspect
            .Get(operation)
            .OptionalInput
            .Keys
            .ToHashSet(StringComparer.Ordinal);
    }

    private static NetVipsRuntimeState Create()
    {
        try
        {
            int major = global::NetVips.NetVips.Version(0);
            int minor = global::NetVips.NetVips.Version(1);
            int patch = global::NetVips.NetVips.Version(2);
            if (major <= 0)
            {
                throw NativeUnavailable();
            }

            global::NetVips.NetVips.BlockUntrusted = true;
            string[] operations = global::NetVips.NetVips.GetOperations().ToArray();
            RequireCodec(operations, "jpegload", "jpegsave");
            RequireCodec(operations, "pngload", "pngsave");
            RequireCodec(operations, "webpload", "webpsave");

            string netVipsVersion = typeof(global::NetVips.Image)
                .Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                .Split('+', 2)[0] ??
                typeof(global::NetVips.Image).Assembly.GetName().Version?.ToString(3) ??
                "unknown";

            return CreateState(
                major,
                minor,
                patch,
                netVipsVersion,
                OptionalArguments("jpegsave_target"),
                OptionalArguments("pngsave_target"),
                OptionalArguments("webpsave_target"),
                operations.Any(operation =>
                    operation.Contains("icc_transform", StringComparison.OrdinalIgnoreCase)));
        }
        catch (ImageProcessorException)
        {
            throw;
        }
        catch (Exception)
        {
            throw NativeUnavailable();
        }
    }

    private static void RequireSupportedVersion(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw NativeUnavailable();
        }

        if (major < MinimumMajorVersion ||
            (major == MinimumMajorVersion && minor < MinimumMinorVersion))
        {
            throw new ImageProcessorException(
                ImageProcessorErrorCode.Unsupported,
                $"Native libvips {major}.{minor}.{patch} is older than the supported " +
                $"{MinimumMajorVersion}.{MinimumMinorVersion} baseline required for " +
                "deterministic, metadata-free encoding.");
        }
    }

    private static void RequireCodec(
        IEnumerable<string> operations,
        string loader,
        string saver)
    {
        bool hasLoader = operations.Any(operation =>
            operation.Contains(loader, StringComparison.OrdinalIgnoreCase));
        bool hasSaver = operations.Any(operation =>
            operation.Contains(saver, StringComparison.OrdinalIgnoreCase));
        if (!hasLoader || !hasSaver)
        {
            throw new ImageProcessorException(
                ImageProcessorErrorCode.Unsupported,
                "Native libvips is missing a required JPEG, PNG, or WebP codec.");
        }
    }

    private static ImageProcessorException NativeUnavailable() =>
        new(
            ImageProcessorErrorCode.Unsupported,
            "Native libvips is unavailable. Install a compatible libvips runtime before enabling imaging.");
}

internal sealed record NetVipsRuntimeState(
    ImagePipelineFingerprint PipelineFingerprint,
    bool SupportsIccTransform,
    NetVipsSaverSupport Savers);
