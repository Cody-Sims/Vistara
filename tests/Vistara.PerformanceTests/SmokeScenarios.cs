using System.Diagnostics;

namespace Vistara.PerformanceTests;

internal static class SmokeScenarios
{
    internal static async Task<(
        IReadOnlyDictionary<string, Measurement> Measurements,
        IReadOnlyList<PrerequisiteResult> Prerequisites)> RunAsync(
        ProjectPaths paths,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var measurements = new Dictionary<string, Measurement>(StringComparer.Ordinal);
        var prerequisites = new List<PrerequisiteResult>();

        await using (ManagementReadFixture management =
                     await ManagementReadFixture.CreateAsync(1_200))
        {
            _ = await management.ReadAsync();
            var latencies = new List<double>(options.Samples);
            var payloadSizes = new List<double>(options.Samples);
            for (int index = 0; index < options.Samples; index++)
            {
                (double milliseconds, double payloadKiB) = await management.ReadAsync();
                latencies.Add(milliseconds);
                payloadSizes.Add(payloadKiB);
            }

            measurements["management-query-p95-ms"] =
                Measurement.Available(Statistics.Percentile(latencies, 95));
            measurements["metadata-page-brotli-kib"] =
                Measurement.Available(payloadSizes.Max());
        }

        try
        {
            var transform = new TransformFixture();
            _ = await transform.TransformAsync();
            var latencies = new List<double>(options.Samples);
            var allocations = new List<double>(options.Samples);
            for (int index = 0; index < options.Samples; index++)
            {
                (double milliseconds, double allocatedMiB, _) =
                    await transform.TransformAsync();
                latencies.Add(milliseconds);
                allocations.Add(allocatedMiB);
            }

            measurements["cold-transform-p95-ms"] =
                Measurement.Available(Statistics.Percentile(latencies, 95));
            measurements["transform-managed-allocation-mib"] =
                Measurement.Available(allocations.Max());
            prerequisites.Add(new PrerequisiteResult(
                "libvips JPEG/WebP codecs",
                BudgetStatus.Passed,
                "A real 2 MP JPEG was transformed by NetVipsImageProcessor."));
        }
        catch (Exception exception) when (IsNativeImagingUnavailable(exception))
        {
            string detail = ExceptionSummary.Create("Cold transform unavailable", exception);
            measurements["cold-transform-p95-ms"] = Measurement.Unavailable(detail);
            measurements["transform-managed-allocation-mib"] =
                Measurement.Unavailable(detail);
            prerequisites.Add(new PrerequisiteResult(
                "libvips JPEG/WebP codecs",
                BudgetStatus.Unavailable,
                detail));
        }

        (int busyErrors, int operationErrors) =
            await SqliteContentionScenario.RunAsync(paths, cancellationToken);
        measurements["sqlite-busy-errors"] = Measurement.Available(busyErrors);
        measurements["upload-job-errors"] = Measurement.Available(operationErrors);

        foreach ((string id, Measurement measurement) in
                 await FrontendBudgetScenario.MeasureAsync(paths, cancellationToken))
        {
            measurements[id] = measurement;
        }

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long processMemory = Math.Max(process.PeakWorkingSet64, process.WorkingSet64);
        measurements["process-peak-working-set-mib"] =
            Measurement.Available(processMemory / 1024d / 1024d);

        AddReferenceHostUnavailable(measurements, prerequisites);
        AddBrowserUnavailable(measurements, prerequisites, options.FrontendObservationsPath);
        return (measurements, prerequisites);
    }

    private static void AddReferenceHostUnavailable(
        Dictionary<string, Measurement> measurements,
        List<PrerequisiteResult> prerequisites)
    {
        const string detail =
            "Run k6/reference-host.js against a populated reference host and import its JSON summary.";
        measurements["warm-management-get-p95-ms"] = Measurement.Unavailable(detail);
        measurements["warm-derivative-ttfb-p95-ms"] = Measurement.Unavailable(detail);
        prerequisites.Add(new PrerequisiteResult(
            "reference host and authorization",
            BudgetStatus.Unavailable,
            detail));
    }

    private static void AddBrowserUnavailable(
        Dictionary<string, Measurement> measurements,
        List<PrerequisiteResult> prerequisites,
        string? observationsPath)
    {
        string detail = observationsPath is null
            ? "Run k6/frontend-browser.js and import the machine-readable browser observations."
            : "Smoke mode does not import browser observations; use --mode evaluate.";
        foreach (string id in new[]
                 {
                     "initial-visible-image-kib",
                     "thumbnail-dom-nodes",
                     "high-priority-images",
                     "scroll-long-task-ms",
                     "lcp-p75-ms",
                     "inp-p75-ms",
                     "cls-p75",
                     "browser-js-heap-mib",
                 })
        {
            measurements[id] = Measurement.Unavailable(detail);
        }

        prerequisites.Add(new PrerequisiteResult(
            "Chromium k6 browser run",
            BudgetStatus.Unavailable,
            detail));
    }

    private static bool IsNativeImagingUnavailable(Exception exception) =>
        exception is DllNotFoundException or TypeInitializationException ||
        exception.GetBaseException() is DllNotFoundException ||
        exception.Message.Contains("libvips", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("codec", StringComparison.OrdinalIgnoreCase);
}
