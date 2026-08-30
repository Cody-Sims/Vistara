namespace Vistara.PerformanceTests;

internal static class BudgetCatalog
{
    internal static readonly BudgetDefinition[] All =
    [
        new("warm-management-get-p95-ms", 200, "ms", true),
        new("management-query-p95-ms", 200, "ms", false),
        new("cold-transform-p95-ms", 750, "ms", true),
        new("warm-derivative-ttfb-p95-ms", 100, "ms", true),
        new("sqlite-busy-errors", 0, "count", true),
        new("upload-job-errors", 0, "count", true),
        new("initial-js-brotli-kib", 180, "KiB", true),
        new("initial-css-brotli-kib", 40, "KiB", true),
        new("metadata-page-brotli-kib", 100, "KiB", true),
        new("initial-visible-image-kib", 500, "KiB", true),
        new("thumbnail-dom-nodes", 400, "count", true),
        new("high-priority-images", 1, "count", true),
        new("scroll-long-task-ms", 50, "ms", true),
        new("lcp-p75-ms", 2500, "ms", true),
        new("inp-p75-ms", 200, "ms", true),
        new("cls-p75", 0.1, "score", true),
        new("transform-managed-allocation-mib", 256, "MiB", false),
        new("process-peak-working-set-mib", 1024, "MiB", false),
        new("browser-js-heap-mib", 256, "MiB", false),
    ];
}
