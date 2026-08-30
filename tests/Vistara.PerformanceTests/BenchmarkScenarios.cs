using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Vistara.PerformanceTests;

internal static class BenchmarkScenarios
{
    internal static Task<(
        IReadOnlyDictionary<string, Measurement> Measurements,
        IReadOnlyList<PrerequisiteResult> Prerequisites)> RunAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string artifacts = Path.Combine(paths.ArtifactsDirectory, "benchmarkdotnet");
        Directory.CreateDirectory(artifacts);
        ManualConfig config = ManualConfig
            .Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithId("reference-bounded")
                .WithWarmupCount(3)
                .WithIterationCount(8)
                .WithInvocationCount(1)
                .WithUnrollFactor(1))
            .WithArtifactsPath(artifacts);

        bool transformAvailable;
        string? transformUnavailable = null;
        try
        {
            var preflight = new TransformFixture();
            _ = preflight.TransformAsync().GetAwaiter().GetResult();
            transformAvailable = true;
        }
        catch (Exception exception) when (IsNativeImagingUnavailable(exception))
        {
            transformAvailable = false;
            transformUnavailable = ExceptionSummary.Create(
                "Benchmark unavailable",
                exception);
        }

        Summary management = BenchmarkRunner.Run<ManagementReadBenchmarks>(config);
        Measurement managementMeasurement = MeasurementFromSummary(management);
        var measurements = new Dictionary<string, Measurement>(StringComparer.Ordinal)
        {
            ["management-query-p95-ms"] =
                managementMeasurement,
            ["metadata-page-brotli-kib"] =
                Measurement.Skipped("Use smoke mode for compressed response size."),
        };
        var prerequisites = new List<PrerequisiteResult>
        {
            new(
                "BenchmarkDotNet management read",
                managementMeasurement.Value is not null
                    ? BudgetStatus.Passed
                    : BudgetStatus.Unavailable,
                managementMeasurement.Value is not null
                    ? "RelationalAssetQueryStore ran against a seeded SQLite database."
                    : managementMeasurement.UnavailableReason ??
                      "BenchmarkDotNet did not produce a management measurement."),
        };

        if (transformAvailable)
        {
            Summary transform = BenchmarkRunner.Run<ColdTransformBenchmarks>(config);
            Measurement transformMeasurement = MeasurementFromSummary(transform);
            measurements["cold-transform-p95-ms"] = transformMeasurement;
            measurements["transform-managed-allocation-mib"] =
                Measurement.Skipped(
                    "MemoryDiagnoser output is available in the BenchmarkDotNet artifact.");
            prerequisites.Add(new PrerequisiteResult(
                "BenchmarkDotNet libvips transform",
                transformMeasurement.Value is not null
                    ? BudgetStatus.Passed
                    : BudgetStatus.Unavailable,
                transformMeasurement.Value is not null
                    ? "NetVipsImageProcessor transformed a fresh 2 MP JPEG to 1200 px WebP."
                    : transformMeasurement.UnavailableReason ??
                      "BenchmarkDotNet did not produce a transform measurement."));
        }
        else
        {
            string detail = transformUnavailable!;
            measurements["cold-transform-p95-ms"] = Measurement.Unavailable(detail);
            measurements["transform-managed-allocation-mib"] =
                Measurement.Unavailable(detail);
            prerequisites.Add(new PrerequisiteResult(
                "BenchmarkDotNet libvips transform",
                BudgetStatus.Unavailable,
                detail));
        }

        const string referenceDetail =
            "Reference HTTP and browser budgets require the committed k6 scenarios.";
        foreach (string id in BudgetCatalog.All
                     .Select(budget => budget.Id)
                     .Where(id => !measurements.ContainsKey(id)))
        {
            measurements[id] = Measurement.Unavailable(referenceDetail);
        }

        return Task.FromResult<(
            IReadOnlyDictionary<string, Measurement>,
            IReadOnlyList<PrerequisiteResult>)>((measurements, prerequisites));
    }

    private static Measurement MeasurementFromSummary(Summary summary)
    {
        BenchmarkReport? report = summary.Reports.SingleOrDefault();
        if (report is null)
        {
            return Measurement.Unavailable(
                "BenchmarkDotNet rejected the scenario before producing a report.");
        }

        if (report.ResultStatistics is null)
        {
            return Measurement.Unavailable(
                $"BenchmarkDotNet produced no statistics for {report.BenchmarkCase.DisplayInfo}.");
        }

        return Measurement.Available(
            report.ResultStatistics.Percentiles.P95 / 1_000_000d);
    }

    private static bool IsNativeImagingUnavailable(Exception exception) =>
        exception is DllNotFoundException or TypeInitializationException ||
        exception.GetBaseException() is DllNotFoundException ||
        exception.Message.Contains("libvips", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("codec", StringComparison.OrdinalIgnoreCase);
}

[MemoryDiagnoser]
public class ManagementReadBenchmarks
{
    private ManagementReadFixture? _fixture;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixture = await ManagementReadFixture.CreateAsync(5_000);
        _ = await _fixture.ReadAsync();
    }

    [Benchmark(Description = "Warm management asset page")]
    public Task<(double Milliseconds, double MetadataBrotliKiB)> ReadManagementPageAsync() =>
        (_fixture ?? throw new InvalidOperationException("Benchmark is not initialized."))
        .ReadAsync();

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }
}

[MemoryDiagnoser]
public class ColdTransformBenchmarks
{
    private TransformFixture? _fixture;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixture = new TransformFixture();
        _ = await _fixture.TransformAsync();
    }

    [Benchmark(Description = "Uncached 2 MP JPEG to 1200 px WebP")]
    public Task<(double Milliseconds, double AllocatedMiB, long OutputBytes)>
        TransformAsync() =>
        (_fixture ?? throw new InvalidOperationException("Benchmark is not initialized."))
        .TransformAsync();
}
