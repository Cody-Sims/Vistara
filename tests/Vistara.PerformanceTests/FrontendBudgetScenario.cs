using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Vistara.PerformanceTests;

internal static partial class FrontendBudgetScenario
{
    internal static async Task<IReadOnlyDictionary<string, Measurement>> MeasureAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        string dist = Path.Combine(paths.RepositoryRoot, "src", "Vistara.Web", "dist");
        string indexPath = Path.Combine(dist, "index.html");
        if (!File.Exists(indexPath))
        {
            const string reason =
                "src/Vistara.Web/dist is absent; run the production web build before evaluation.";
            return new Dictionary<string, Measurement>(StringComparer.Ordinal)
            {
                ["initial-js-brotli-kib"] = Measurement.Unavailable(reason),
                ["initial-css-brotli-kib"] = Measurement.Unavailable(reason),
            };
        }

        string html = await File.ReadAllTextAsync(indexPath, cancellationToken);
        string[] references = AssetReferenceRegex()
            .Matches(html)
            .Select(match => match.Groups["path"].Value.TrimStart('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] scripts = references
            .Where(path => path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] styles = references
            .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        double scriptKiB = await BrotliKiBAsync(dist, scripts, cancellationToken);
        double styleKiB = await BrotliKiBAsync(dist, styles, cancellationToken);
        return new Dictionary<string, Measurement>(StringComparer.Ordinal)
        {
            ["initial-js-brotli-kib"] = Measurement.Available(scriptKiB),
            ["initial-css-brotli-kib"] = Measurement.Available(styleKiB),
        };
    }

    private static async Task<double> BrotliKiBAsync(
        string dist,
        IEnumerable<string> assets,
        CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (string relative in assets)
        {
            string path = Path.GetFullPath(Path.Combine(dist, relative));
            if (!path.StartsWith(
                    Path.GetFullPath(dist) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) ||
                !File.Exists(path))
            {
                throw new InvalidDataException(
                    $"The production index references a missing asset: {relative}");
            }

            byte[] content = await File.ReadAllBytesAsync(path, cancellationToken);
            await using var output = new MemoryStream();
            await using (var brotli = new BrotliStream(
                             output,
                             CompressionLevel.SmallestSize,
                             leaveOpen: true))
            {
                await brotli.WriteAsync(content, cancellationToken);
            }

            total += output.Length;
        }

        return total / 1024d;
    }

    [GeneratedRegex(
        """(?:src|href)=["'](?<path>[^"'?#]+\.(?:js|css))["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetReferenceRegex();
}
